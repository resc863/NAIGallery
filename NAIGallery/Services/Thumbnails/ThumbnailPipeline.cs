using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NAIGallery;
using NAIGallery.Models;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Threading.Channels;

namespace NAIGallery.Services;

internal sealed class ThumbnailPipeline : IThumbnailPipeline, IDisposable
{
    // Hint trimmer: keep WriteableBitmap public constructors
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(WriteableBitmap))]
    public ThumbnailPipeline(int capacityBytes, ILogger? logger = null)
    {
        _logger = logger;
        _byteCapacity = Math.Max(1, capacityBytes);
    }

    private readonly ILogger? _logger;
    private DispatcherQueue? _dispatcher;

    private sealed class PixelEntry
    {
        public required byte[] Pixels;
        public required int W;
        public required int H;
        public required int Rented;
    }

    private sealed class LruNode
    {
        public required string Key;
        public required PixelEntry Entry;
        public LruNode? Prev;
        public LruNode? Next;
    }

    private readonly Dictionary<string, LruNode> _cacheMap = new(StringComparer.Ordinal);
    private LruNode? _head;
    private LruNode? _tail;
    private long _currentBytes;
    private long _byteCapacity;
    private readonly object _cacheLock = new();

    // Error / stats
    private long _ioErrors, _formatErrors, _canceled, _unknownErrors;
    private long _comUnsupportedFormat, _comOutOfMemory, _comAccessDenied, _comWrongState, _comDeviceLost, _comOther;

    private readonly ConcurrentDictionary<string, byte> _inflight = new();
    
    // 실제 로딩 중인 아이템 추적 (UI 상태 표시용)
    private readonly ConcurrentDictionary<string, ImageMetadata> _activeLoading = new(StringComparer.OrdinalIgnoreCase);
    
    // Decode concurrency: increased for better throughput
    private static readonly int MaxDecodeParallelism = Math.Clamp(Environment.ProcessorCount, 4, 16);
    private readonly SemaphoreSlim _decodeGate = new(MaxDecodeParallelism);
    
    // Bitmap creation gate: increased from 4 to 8 for faster UI application
    private static readonly SemaphoreSlim _bitmapCreationGate = new(8, 8);
    
    private readonly ConcurrentDictionary<string, int> _fileGeneration = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _maxRequested = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _decodeInProgressMax = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _pendingScheduled = new(StringComparer.OrdinalIgnoreCase);

    // Apply queue remains concurrent since UI marshals to dispatcher
    private readonly ConcurrentQueue<(ImageMetadata Meta, PixelEntry Entry, int Width, int Gen)> _applyQ = new();
    private int _applyScheduled;
    private volatile bool _applySuspended;
    
    // 주기적 드레인 타이머 - TryEnqueue 실패 시 복구용
    private Timer? _drainRetryTimer;
    
    // 썸네일 적용 후 콜백 (UI 갱신용)
    public event Action<ImageMetadata>? ThumbnailApplied;

    private sealed record ThumbReq(ImageMetadata Meta, int Width, bool High, int Epoch);

    // Priority channels - increased capacity and use DropOldest for high priority
    private readonly Channel<ThumbReq> _chHigh = Channel.CreateBounded<ThumbReq>(
        new BoundedChannelOptions(1024) { SingleReader = false, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Channel<ThumbReq> _chNormal = Channel.CreateBounded<ThumbReq>(
        new BoundedChannelOptions(4096) { SingleReader = false, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest });

    private int _highBacklog = 0, _normalBacklog = 0;

    private readonly CancellationTokenSource _schedCts = new();
    private int _activeWorkers = 0;
    private volatile int _epoch = 0;
    private int _targetWorkers = Math.Clamp(Environment.ProcessorCount, 4, 12);

    private Timer? _uiPulseTimer;
    private double _uiLagMs;
    private volatile bool _uiBusy;

    // 메모리 압력 추적 - 더 빠른 체크
    private volatile bool _memoryPressure = false;
    private long _lastMemoryCheckTicks = 0;
    private const long MemoryCheckIntervalTicks = TimeSpan.TicksPerSecond; // 1초로 단축
    
    // 비트맵 생성 간 최소 간격 - 거의 제거 (0.5ms)
    private long _lastBitmapCreationTicks = 0;
    private const long MinBitmapCreationIntervalTicks = TimeSpan.TicksPerMillisecond / 2;

    public void InitializeDispatcher(DispatcherQueue dispatcherQueue)
    {
        _dispatcher = dispatcherQueue;
        StartUiPulseMonitor();
        StartDrainRetryTimer();
        EnsureWorkers();
    }

    /// <summary>
    /// 주기적으로 applyQ를 확인하고 드레인을 시도하는 타이머 시작
    /// TryEnqueue 실패로 인한 데드락 방지
    /// </summary>
    private void StartDrainRetryTimer()
    {
        _drainRetryTimer?.Dispose();
        _drainRetryTimer = new Timer(_ =>
        {
            // applyQ에 아이템이 있고 드레인이 스케줄되지 않았으면 강제 드레인
            if (!_applyQ.IsEmpty && Volatile.Read(ref _applyScheduled) == 0)
            {
                ScheduleDrain();
            }
            // 드레인이 스케줄된 상태로 오래 머물러 있으면 리셋 (데드락 복구)
            else if (!_applyQ.IsEmpty && Volatile.Read(ref _applyScheduled) == 1)
            {
                // 강제로 리셋하고 다시 스케줄
                Interlocked.Exchange(ref _applyScheduled, 0);
                ScheduleDrain();
            }
        }, null, dueTime: 100, period: 50); // 50ms마다 확인
    }

    public int CacheCapacity
    {
        get => (int)Math.Min(int.MaxValue, Interlocked.Read(ref _byteCapacity));
        set
        {
            long newCap = Math.Max(1, value);
            lock (_cacheLock)
            {
                _byteCapacity = newCap;
                Prune_NoLock();
            }
        }
    }

    public void SetApplySuspended(bool suspended)
    {
        _applySuspended = suspended;
        if (!suspended) ScheduleDrain();
    }

    public void FlushApplyQueue() => ScheduleDrain();

    public void DrainVisible(HashSet<ImageMetadata> visible)
    {
        if (_dispatcher == null || visible.Count == 0) return;
        
        // 즉시 처리할 아이템 수집 - 더 많이 처리
        var toProcess = new List<(ImageMetadata Meta, PixelEntry Entry, int Width, int Gen)>();
        var remainder = new List<(ImageMetadata, PixelEntry, int, int)>();
        
        while (_applyQ.TryDequeue(out var it))
        {
            if (visible.Contains(it.Meta) && toProcess.Count < 32) // 16 → 32로 증가
            {
                toProcess.Add(it);
            }
            else
            {
                remainder.Add(it);
            }
        }
        
        // 나머지 다시 큐에 넣기
        foreach (var r in remainder) _applyQ.Enqueue(r);
        
        if (toProcess.Count == 0) return;
        
        bool enqueued = _dispatcher.TryEnqueue(DispatcherQueuePriority.High, async () =>
        {
            foreach (var item in toProcess)
            {
                try
                {
                    await TryApplyFromEntryAsync(item.Meta, item.Entry, item.Width, item.Gen, true);
                }
                catch { }
            }
        });
        
        // TryEnqueue 실패 시 다시 큐에 넣기
        if (!enqueued)
        {
            foreach (var item in toProcess)
                _applyQ.Enqueue(item);
        }
    }

    public void ClearCache()
    {
        lock (_cacheLock)
        {
            foreach (var kv in _cacheMap)
            {
                try { ArrayPool<byte>.Shared.Return(kv.Value.Entry.Pixels); } catch { }
            }
            _cacheMap.Clear();
            _head = _tail = null;
            _currentBytes = 0;
        }
    }

    // Scheduling API
    public void Schedule(ImageMetadata meta, int width, bool highPriority = false)
    {
        InternalEnqueue(meta, width, highPriority, _epoch);
        EnsureWorkers();
    }

    public void BoostVisible(IEnumerable<ImageMetadata> metas, int width)
    {
        int ep = _epoch;
        foreach (var m in metas)
            InternalEnqueue(m, width, true, ep, force: true);
        EnsureWorkers();
    }

    public void UpdateViewport(IReadOnlyList<ImageMetadata> orderedVisible, IReadOnlyList<ImageMetadata> bufferItems, int width)
    {
        int newEpoch = Interlocked.Increment(ref _epoch);
        _pendingScheduled.Clear();
        foreach (var m in orderedVisible)
            InternalEnqueue(m, width, true, newEpoch, force: true);
        foreach (var m in bufferItems)
            InternalEnqueue(m, width, false, newEpoch);
        EnsureWorkers();
    }

    private void InternalEnqueue(ImageMetadata meta, int width, bool high, int epoch, bool force = false)
    {
        if (meta?.FilePath == null) return;
        if (!File.Exists(meta.FilePath)) return;

        int existing = meta.ThumbnailPixelWidth ?? 0;
        if (!force && existing >= width) return;

        _pendingScheduled.AddOrUpdate(meta.FilePath, width, (_, prev) => width > prev ? width : prev);
        int pendingWidth = _pendingScheduled[meta.FilePath];
        if (width < pendingWidth && !force) return;

        var req = new ThumbReq(meta, pendingWidth, high, epoch);
        if (high)
        {
            if (_chHigh.Writer.TryWrite(req))
                Interlocked.Increment(ref _highBacklog);
        }
        else
        {
            if (_chNormal.Writer.TryWrite(req))
                Interlocked.Increment(ref _normalBacklog);
        }
        UpdateWorkerTarget();
    }

    private void UpdateWorkerTarget()
    {
        int backlog = Math.Max(0, Volatile.Read(ref _highBacklog)) + Math.Max(0, Volatile.Read(ref _normalBacklog));
        int cpu = Math.Max(4, Environment.ProcessorCount);
        int ideal;

        // 더 공격적인 워커 스케일링
        if (_memoryPressure)
        {
            ideal = Math.Max(2, cpu / 4);
        }
        else if (backlog <= 4)
        {
            ideal = Math.Max(4, cpu / 2);
        }
        else if (backlog <= 16)
        {
            ideal = Math.Min(cpu, 10);
        }
        else
        {
            ideal = Math.Min(cpu + 4, 16);
        }

        // UI 바쁨 여부에 따른 조절 더 완화 - 스크롤 중에도 더 많은 워커 유지
        if (_uiBusy)
        {
            ideal = Math.Max(4, ideal * 3 / 4); // 3/4로 감소 (기존 2/3)
        }

        ideal = Math.Clamp(ideal, 2, 16);
        int cur = _targetWorkers;
        if (ideal != cur)
        {
            _targetWorkers = ideal;
            EnsureWorkers();
        }
    }

    private void EnsureWorkers()
    {
        while (!_schedCts.IsCancellationRequested)
        {
            int cur = _activeWorkers;
            if (cur >= _targetWorkers) break;
            if (Interlocked.CompareExchange(ref _activeWorkers, cur + 1, cur) == cur)
            {
                _ = Task.Run(WorkerLoopAsync);
            }
        }
    }

    private async Task<ThumbReq?> TryDequeueAsync(CancellationToken token)
    {
        // Always prioritize high-priority channel - drain multiple items if available
        while (_chHigh.Reader.TryRead(out var hItem))
        {
            Interlocked.Decrement(ref _highBacklog);
            // epoch 체크 더 완화 - 5 epoch 전까지 허용
            if (hItem.Epoch >= _epoch - 5)
                return hItem;
        }
        
        // Only process normal channel if high channel is empty
        if (_chNormal.Reader.TryRead(out var n))
        {
            Interlocked.Decrement(ref _normalBacklog);
            return n;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(300);
        try
        {
            // Wait for high-priority first with shorter timeout
            var highTask = _chHigh.Reader.WaitToReadAsync(timeoutCts.Token).AsTask();
            var delayTask = Task.Delay(30, timeoutCts.Token); // 50 → 30ms로 단축
            
            var first = await Task.WhenAny(highTask, delayTask).ConfigureAwait(false);
            if (first == highTask && highTask.Result && _chHigh.Reader.TryRead(out var h1))
            {
                Interlocked.Decrement(ref _highBacklog);
                return h1;
            }
            
            // Check normal channel
            if (_chNormal.Reader.TryRead(out n))
            {
                Interlocked.Decrement(ref _normalBacklog);
                return n;
            }
            
            // Wait for either channel
            var normTask = _chNormal.Reader.WaitToReadAsync(timeoutCts.Token).AsTask();
            var completed = await Task.WhenAny(highTask, normTask).ConfigureAwait(false);
            if (completed.Result)
            {
                // Prefer high even if normal completed
                if (_chHigh.Reader.TryRead(out var h2))
                {
                    Interlocked.Decrement(ref _highBacklog);
                    return h2;
                }
                if (_chNormal.Reader.TryRead(out n))
                {
                    Interlocked.Decrement(ref _normalBacklog);
                    return n;
                }
            }
        }
        catch (OperationCanceledException) { }
        return null;
    }

    private async Task WorkerLoopAsync()
    {
        try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; } catch { }

        var token = _schedCts.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                // 주기적으로 메모리 압력 확인
                CheckMemoryPressure();

                if (_memoryPressure)
                {
                    await Task.Delay(100, token).ConfigureAwait(false);
                    try { GC.Collect(0, GCCollectionMode.Optimized); } catch { }
                }

                var reqObj = await TryDequeueAsync(token).ConfigureAwait(false);
                if (reqObj is null)
                {
                    if (_activeWorkers > 4) break;
                    continue;
                }

                var req = reqObj;
                // epoch 체크 더 완화 - 5 epoch 전까지 허용 (스크롤 중에도 처리)
                int currentEpoch = _epoch;
                if (req.Epoch < currentEpoch - 5) continue;
                if ((req.Meta.ThumbnailPixelWidth ?? 0) >= req.Width) continue;

                try
                {
                    await EnsureThumbnailAsync(req.Meta, req.Width, token, false).ConfigureAwait(false);

                    // 메모리 압박 시에만 양보
                    if (_memoryPressure)
                    {
                        await Task.Delay(5, token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref _canceled);
                    Telemetry.DecodeCanceled.Add(1);
                }
                catch (COMException) { /* LoadDecodeAsync에서 로깅됨 */ }
                catch (OutOfMemoryException)
                {
                    _memoryPressure = true;
                    try { GC.Collect(2, GCCollectionMode.Aggressive); } catch { }
                    await Task.Delay(500, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            Interlocked.Decrement(ref _activeWorkers);
            if (!_schedCts.IsCancellationRequested &&
                (Volatile.Read(ref _highBacklog) > 0 || Volatile.Read(ref _normalBacklog) > 0))
            {
                EnsureWorkers();
            }
        }
    }

    private void CheckMemoryPressure()
    {
        long now = Stopwatch.GetTimestamp();
        if (now - _lastMemoryCheckTicks < MemoryCheckIntervalTicks) return;
        _lastMemoryCheckTicks = now;

        try
        {
            var memInfo = GC.GetGCMemoryInfo();
            double usedRatio = (double)memInfo.MemoryLoadBytes / Math.Max(1, memInfo.HighMemoryLoadThresholdBytes);
            bool wasUnderPressure = _memoryPressure;
            _memoryPressure = usedRatio > 0.88; // 임계값 더 상향

            if (_memoryPressure && !wasUnderPressure)
            {
                // 메모리 압력 시작 시 캐시 용량 감소
                lock (_cacheLock)
                {
                    _byteCapacity = Math.Max(1000, _byteCapacity / 2);
                    Prune_NoLock();
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// 로딩 상태 시작을 UI 스레드에서 설정
    /// </summary>
    private void SetLoadingState(ImageMetadata meta, bool isLoading)
    {
        if (_dispatcher == null) return;
        
        if (isLoading)
        {
            _activeLoading.TryAdd(meta.FilePath, meta);
        }
        else
        {
            _activeLoading.TryRemove(meta.FilePath, out _);
        }
        
        _dispatcher.TryEnqueue(DispatcherQueuePriority.Normal, () =>
        {
            try { meta.IsLoadingThumbnail = isLoading; } catch { }
        });
    }

    public async Task EnsureThumbnailAsync(ImageMetadata meta, int decodeWidth, CancellationToken ct, bool allowDownscale)
    {
        try
        {
            if (_dispatcher == null || ct.IsCancellationRequested) return;
            if (meta?.FilePath == null) return;
            if (!File.Exists(meta.FilePath)) return;

            long ticks = SafeGetTicks(meta.FilePath);
            if (ticks == 0) return;

            int maxReq = _maxRequested.AddOrUpdate(meta.FilePath, decodeWidth, (_, old) => decodeWidth > old ? decodeWidth : old);
            if (decodeWidth < maxReq) decodeWidth = maxReq;

            int current = meta.ThumbnailPixelWidth ?? 0;
            if (!allowDownscale && current >= decodeWidth)
            {
                Touch(meta.FilePath, ticks, decodeWidth);
                return;
            }

            int inProg = _decodeInProgressMax.AddOrUpdate(meta.FilePath, decodeWidth, (_, old) => decodeWidth > old ? decodeWidth : old);
            if (inProg > decodeWidth) return;

            string key = MakeCacheKey(meta.FilePath, ticks, decodeWidth);
            if (TryGetEntry(key, out var entry))
            {
                int gen = _fileGeneration.AddOrUpdate(meta.FilePath, 1, (_, g) => g + 1);
                EnqueueApply(meta, entry!, decodeWidth, gen, allowDownscale);
                _decodeInProgressMax.TryRemove(meta.FilePath, out _);
                return;
            }

            // 로딩 상태 시작
            SetLoadingState(meta, true);

            try
            {
                // 프로그레시브 로딩 최적화: 작은 이미지나 이미 일부 로드된 경우 스킵
                int small = Math.Clamp(decodeWidth / 2, AppDefaults.SmallThumbMin, AppDefaults.SmallThumbMax);
                bool shouldDoProgressive = !allowDownscale && current == 0 && decodeWidth > small * 2;
                
                if (shouldDoProgressive)
                {
                    int smallMarker = _decodeInProgressMax.AddOrUpdate(meta.FilePath, small, (_, old) => old > small ? old : small);
                    if (smallMarker == small)
                    {
                        int sgen = _fileGeneration.AddOrUpdate(meta.FilePath, 1, (_, g) => g + 1);
                        // 작은 썸네일을 비동기로 시작하되 기다리지 않음
                        _ = LoadDecodeAsync(meta, small, ticks, ct, sgen, false);
                    }
                }

                int mainGen = _fileGeneration.AddOrUpdate(meta.FilePath, 1, (_, g) => g + 1);
                await LoadDecodeAsync(meta, decodeWidth, ticks, ct, mainGen, allowDownscale).ConfigureAwait(false);
            }
            finally
            {
                _decodeInProgressMax.TryRemove(meta.FilePath, out _);
                // 로딩 완료 시 상태 해제 (썸네일이 설정되었거나 실패했거나)
                SetLoadingState(meta, false);
            }
        }
        catch (OperationCanceledException)
        {
            SetLoadingState(meta, false);
            Interlocked.Increment(ref _canceled);
            Telemetry.DecodeCanceled.Add(1);
        }
    }

    public async Task PreloadAsync(IEnumerable<ImageMetadata> items, int decodeWidth, CancellationToken ct, int maxParallelism)
    {
        if (ct.IsCancellationRequested) return;

        if (maxParallelism <= 0)
        {
            int cpu = Math.Max(1, Environment.ProcessorCount);
            int reserve = _uiBusy || _memoryPressure ? Math.Max(2, cpu / 4) : 1;
            int effective = Math.Max(2, cpu - reserve);
            maxParallelism = Math.Clamp(effective, 2, _memoryPressure ? 6 : (_uiBusy ? 6 : 12));
        }

        var po = new ParallelOptions
        {
            MaxDegreeOfParallelism = maxParallelism,
            CancellationToken = ct,
            TaskScheduler = TaskScheduler.Default
        };

        await Parallel.ForEachAsync(items, po, async (m, token) =>
        {
            await EnsureThumbnailAsync(m, decodeWidth, token, false).ConfigureAwait(false);
        });
    }

    private async Task LoadDecodeAsync(ImageMetadata meta, int width, long ticks, CancellationToken ct, int gen, bool allowDownscale)
    {
        string key = MakeCacheKey(meta.FilePath, ticks, width);
        if (TryGetEntry(key, out var cached))
        {
            EnqueueApply(meta, cached!, width, gen, allowDownscale);
            return;
        }

        if (!_inflight.TryAdd(key, 0)) return;

        var sw = Stopwatch.StartNew();
        try
        {
            await _decodeGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var fs = File.Open(meta.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using IRandomAccessStream ras = fs.AsRandomAccessStream();

                // 원본 크기 추출 - 병렬로 진행
                var decodeSizeTask = Task.CompletedTask;
                if (!meta.OriginalWidth.HasValue || !meta.OriginalHeight.HasValue)
                {
                    decodeSizeTask = Task.Run(async () =>
                    {
                        try
                        {
                            using var fs2 = File.Open(meta.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                            using IRandomAccessStream ras2 = fs2.AsRandomAccessStream();
                            var decoder = await BitmapDecoder.CreateAsync(ras2);
                            meta.OriginalWidth = (int)decoder.PixelWidth;
                            meta.OriginalHeight = (int)decoder.PixelHeight;
                        }
                        catch { }
                    }, ct);
                }

                var sb = await DecodeAsync(ras, width, ct).ConfigureAwait(false);
                
                // 크기 추출 완료 대기
                await decodeSizeTask.ConfigureAwait(false);
                
                if (sb == null || ct.IsCancellationRequested)
                {
                    if (ct.IsCancellationRequested)
                    {
                        Interlocked.Increment(ref _canceled);
                        Telemetry.DecodeCanceled.Add(1);
                    }
                    else
                    {
                        Interlocked.Increment(ref _formatErrors);
                        Telemetry.DecodeFormatErrors.Add(1);
                    }
                    return;
                }

                int pxCount = (int)(sb.PixelWidth * sb.PixelHeight * 4);
                byte[] rented;
                try
                {
                    rented = ArrayPool<byte>.Shared.Rent(pxCount);
                }
                catch (OutOfMemoryException)
                {
                    _memoryPressure = true;
                    try { sb.Dispose(); } catch { }
                    return;
                }

                try
                {
                    sb.CopyToBuffer(rented.AsBuffer());
                }
                catch
                {
                    ArrayPool<byte>.Shared.Return(rented);
                    Interlocked.Increment(ref _formatErrors);
                    Telemetry.DecodeFormatErrors.Add(1);
                    try { sb.Dispose(); } catch { }
                    return;
                }

                var entry = new PixelEntry
                {
                    Pixels = rented,
                    W = (int)sb.PixelWidth,
                    H = (int)sb.PixelHeight,
                    Rented = rented.Length
                };
                try { sb.Dispose(); } catch { }

                AddEntry(key, entry);
                EnqueueApply(meta, entry, width, gen, allowDownscale);
            }
            catch (IOException)
            {
                Interlocked.Increment(ref _ioErrors);
                Telemetry.DecodeIoErrors.Add(1);
            }
            catch (UnauthorizedAccessException)
            {
                Interlocked.Increment(ref _ioErrors);
                Telemetry.DecodeIoErrors.Add(1);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref _canceled);
                Telemetry.DecodeCanceled.Add(1);
            }
            catch (COMException comEx)
            {
                HandleComException(comEx, meta.FilePath);
            }
            catch (OutOfMemoryException)
            {
                _memoryPressure = true;
                Interlocked.Increment(ref _comOutOfMemory);
                Telemetry.ComOutOfMemory.Add(1);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _unknownErrors);
                Telemetry.DecodeUnknownErrors.Add(1);
                _logger?.LogDebug(ex, "Decode error {File}", meta.FilePath);
            }
            finally
            {
                _decodeGate.Release();
            }
        }
        finally
        {
            sw.Stop();
            Telemetry.DecodeLatencyMs.Record(sw.Elapsed.TotalMilliseconds);
            _inflight.TryRemove(key, out _);
        }
    }

    private void HandleComException(COMException comEx, string filePath)
    {
        switch ((uint)comEx.HResult)
        {
            case 0x88982F50:
            case 0x88982F44:
                Interlocked.Increment(ref _comUnsupportedFormat);
                Telemetry.ComUnsupportedFormat.Add(1);
                break;
            case 0x8007000E:
            case 0x88982F07:
                _memoryPressure = true;
                Interlocked.Increment(ref _comOutOfMemory);
                Telemetry.ComOutOfMemory.Add(1);
                break;
            case 0x80070005:
                Interlocked.Increment(ref _comAccessDenied);
                Telemetry.ComAccessDenied.Add(1);
                break;
            case 0x88982F81:
                Interlocked.Increment(ref _comWrongState);
                Telemetry.ComWrongState.Add(1);
                break;
            case 0x887A0005:
            case 0x887A0006:
                Interlocked.Increment(ref _comDeviceLost);
                Telemetry.ComDeviceLost.Add(1);
                break;
            default:
                Interlocked.Increment(ref _comOther);
                Telemetry.ComOther.Add(1);
                break;
        }
        _logger?.LogDebug(comEx, "COM decode error {HResult:X8} {File}", comEx.HResult, filePath);
    }

    private static async Task<SoftwareBitmap?> DecodeAsync(IRandomAccessStream ras, int targetWidth, CancellationToken ct)
    {
        try
        {
            var decoder = await BitmapDecoder.CreateAsync(ras);
            uint sw = decoder.PixelWidth, sh = decoder.PixelHeight;
            if (sw == 0 || sh == 0) return null;

            double scale = Math.Min(1.0, targetWidth / (double)sw);
            uint ow = (uint)Math.Max(1, Math.Round(sw * scale));
            uint oh = (uint)Math.Max(1, Math.Round(sh * scale));

            var transform = new BitmapTransform
            {
                ScaledWidth = ow,
                ScaledHeight = oh,
                InterpolationMode = BitmapInterpolationMode.Linear // Fant보다 빠름
            };

            try
            {
                return await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.DoNotColorManage); // 색상 관리 스킵으로 속도 향상
            }
            catch (COMException)
            {
                var sb = await decoder.GetSoftwareBitmapAsync(
                    decoder.BitmapPixelFormat,
                    decoder.BitmapAlphaMode,
                    transform,
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.DoNotColorManage);

                if (sb.BitmapPixelFormat != BitmapPixelFormat.Bgra8 ||
                    sb.BitmapAlphaMode != BitmapAlphaMode.Premultiplied)
                {
                    var conv = SoftwareBitmap.Convert(sb, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                    sb.Dispose();
                    sb = conv;
                }
                return sb;
            }
        }
        catch (OperationCanceledException) { return null; }
        catch { return null; }
    }

    private void EnqueueApply(ImageMetadata meta, PixelEntry entry, int width, int gen, bool allowDownscale)
    {
        int latest = _fileGeneration.TryGetValue(meta.FilePath, out var g) ? g : gen;
        // generation 체크 더 완화 - 5 이상 차이 나면 스킵
        if (gen < latest - 5 && !allowDownscale) return;

        _applyQ.Enqueue((meta, entry, width, gen));
        ScheduleDrain();
    }

    private void ScheduleDrain()
    {
        if (_dispatcher == null) return;
        
        // _applySuspended 상태에서도 더 많이 드레인 허용
        if (Interlocked.CompareExchange(ref _applyScheduled, 1, 0) != 0) return;

        bool enqueued = _dispatcher.TryEnqueue(DispatcherQueuePriority.High, async () =>
        {
            try
            {
                int processed = 0;
                // 배치 크기 대폭 증가 - 더 빠른 UI 반영
                int batchSize = _applySuspended ? 8 : (_uiBusy ? 12 : 24);

                while (_applyQ.TryDequeue(out var item) && processed < batchSize)
                {
                    try
                    {
                        await TryApplyFromEntryAsync(item.Meta, item.Entry, item.Width, item.Gen, false);
                    }
                    catch (COMException comEx)
                    {
                        HandleComException(comEx, item.Meta.FilePath);
                    }
                    catch (OutOfMemoryException)
                    {
                        _memoryPressure = true;
                        _applyQ.Enqueue(item);
                        break;
                    }

                    processed++;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Drain error");
            }
            finally
            {
                Interlocked.Exchange(ref _applyScheduled, 0);
                if (!_applyQ.IsEmpty)
                {
                    // 즉시 다음 배치 스케줄
                    ScheduleDrain();
                }
            }
        });
        
        // TryEnqueue 실패 시 _applyScheduled 리셋
        if (!enqueued)
        {
            Interlocked.Exchange(ref _applyScheduled, 0);
        }
    }

    private async Task TryApplyFromEntryAsync(ImageMetadata meta, PixelEntry entry, int width, int gen, bool allowDownscale)
    {
        int latest = _fileGeneration.TryGetValue(meta.FilePath, out var g) ? g : gen;
        // generation 체크 더 완화 - 이미 디코딩된 것은 최대한 적용
        // 썸네일이 없거나 더 작은 경우에만 generation 체크
        if (gen < latest - 5 && !allowDownscale && meta.Thumbnail != null && (meta.ThumbnailPixelWidth ?? 0) >= width) return;

        // 비트맵 생성 간격 거의 제거
        long now = Stopwatch.GetTimestamp();
        long last = Interlocked.Read(ref _lastBitmapCreationTicks);
        long elapsed = now - last;
        if (elapsed < MinBitmapCreationIntervalTicks)
        {
            // 매우 짧은 대기만 (최대 1ms)
            await Task.Yield();
        }
        Interlocked.Exchange(ref _lastBitmapCreationTicks, Stopwatch.GetTimestamp());

        // WriteableBitmap 생성 - 제한된 병렬성 허용 (타임아웃 증가)
        bool acquired = await _bitmapCreationGate.WaitAsync(300);
        if (!acquired)
        {
            _applyQ.Enqueue((meta, entry, width, gen));
            return;
        }

        try
        {
            // 유효성 검사
            if (entry.W <= 0 || entry.H <= 0 || entry.W > 8192 || entry.H > 8192)
            {
                return;
            }

            WriteableBitmap? wb = null;
            try
            {
                wb = new WriteableBitmap(entry.W, entry.H);
                var buffer = wb.PixelBuffer;
                int expected = entry.W * entry.H * 4;
                int capacity = (int)buffer.Capacity;
                int toWrite = Math.Min(expected, Math.Min(capacity, entry.Rented));

                using var s = buffer.AsStream();
                s.Write(entry.Pixels, 0, toWrite);
                wb.Invalidate();
            }
            catch (COMException comEx)
            {
                HandleComException(comEx, meta.FilePath);
                return;
            }
            catch (OutOfMemoryException)
            {
                _memoryPressure = true;
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "WriteableBitmap creation failed for {File}", meta.FilePath);
                return;
            }

            // UI 스레드에서 실행 중이므로 직접 설정 가능
            // PropertyChanged 이벤트가 UI 스레드에서 발생하므로 바인딩 업데이트됨
            if (wb != null && (meta.Thumbnail == null || width >= (meta.ThumbnailPixelWidth ?? 0)))
            {
                meta.Thumbnail = wb;
                meta.ThumbnailPixelWidth = width;
                // 썸네일 설정 후 로딩 상태 해제
                meta.IsLoadingThumbnail = false;
                
                // 썸네일 적용 콜백 호출 (UI 갱신용)
                try { ThumbnailApplied?.Invoke(meta); } catch { }
            }

            if (!meta.OriginalWidth.HasValue || !meta.OriginalHeight.HasValue)
            {
                if (entry.W > 0 && entry.H > 0)
                {
                    double ar = Math.Clamp(entry.W / (double)Math.Max(1, entry.H), 0.1, 10.0);
                    if (Math.Abs(ar - meta.AspectRatio) > 0.001)
                    {
                        meta.AspectRatio = ar;
                    }
                }
            }
        }
        finally
        {
            _bitmapCreationGate.Release();
        }
    }

    private void Touch(string file, long ticks, int width)
    {
        var key = MakeCacheKey(file, ticks, width);
        lock (_cacheLock)
        {
            if (_cacheMap.TryGetValue(key, out var node))
            {
                MoveToHead_NoLock(node);
            }
        }
    }

    private string MakeCacheKey(string file, long ticks, int width)
    {
        var ts = ticks.ToString();
        var ws = width.ToString();
        return string.Create(file.Length + 1 + ts.Length + 2 + ws.Length, (file, ts, ws), (dst, s) =>
        {
            s.file.AsSpan().CopyTo(dst);
            int p = s.file.Length;
            dst[p++] = '|';
            s.ts.AsSpan().CopyTo(dst[p..]);
            p += s.ts.Length;
            dst[p++] = '|';
            dst[p++] = 'w';
            s.ws.AsSpan().CopyTo(dst[p..]);
        });
    }

    private bool TryGetEntry(string key, out PixelEntry? entry)
    {
        lock (_cacheLock)
        {
            if (_cacheMap.TryGetValue(key, out var node))
            {
                MoveToHead_NoLock(node);
                entry = node.Entry;
                return true;
            }
        }
        entry = null;
        return false;
    }

    private void AddEntry(string key, PixelEntry entry)
    {
        lock (_cacheLock)
        {
            if (_cacheMap.TryGetValue(key, out var existing))
            {
                _currentBytes -= existing.Entry.Rented;
                try { ArrayPool<byte>.Shared.Return(existing.Entry.Pixels); } catch { }
                existing.Entry = entry;
                _currentBytes += entry.Rented;
                MoveToHead_NoLock(existing);
                Prune_NoLock();
                return;
            }

            var node = new LruNode { Key = key, Entry = entry };
            _cacheMap[key] = node;
            _currentBytes += entry.Rented;
            InsertHead_NoLock(node);
            Prune_NoLock();
        }
    }

    private void InsertHead_NoLock(LruNode node)
    {
        node.Prev = null;
        node.Next = _head;
        if (_head != null) _head.Prev = node;
        _head = node;
        if (_tail == null) _tail = node;
    }

    private void MoveToHead_NoLock(LruNode node)
    {
        if (node == _head) return;
        if (node.Prev != null) node.Prev.Next = node.Next;
        if (node.Next != null) node.Next.Prev = node.Prev;
        if (node == _tail) _tail = node.Prev;
        node.Prev = null;
        node.Next = _head;
        if (_head != null) _head.Prev = node;
        _head = node;
        if (_tail == null) _tail = node;
    }

    private void Prune_NoLock()
    {
        while (_currentBytes > _byteCapacity && _tail != null)
        {
            var evict = _tail;
            var prev = evict.Prev;
            if (prev != null) prev.Next = null;
            _tail = prev;
            if (_tail == null) _head = null;
            _cacheMap.Remove(evict.Key);
            _currentBytes -= evict.Entry.Rented;
            try { ArrayPool<byte>.Shared.Return(evict.Entry.Pixels); } catch { }
        }
    }

    private static long SafeGetTicks(string file)
    {
        try { return new FileInfo(file).LastWriteTimeUtc.Ticks; }
        catch { return 0; }
    }

    private void StartUiPulseMonitor()
    {
        try
        {
            _uiPulseTimer?.Dispose();
            _uiPulseTimer = new Timer(_ =>
            {
                if (_dispatcher == null) return;
                long stamp = Stopwatch.GetTimestamp();
                _dispatcher.TryEnqueue(DispatcherQueuePriority.Normal, () =>
                {
                    double ms = (Stopwatch.GetTimestamp() - stamp) * 1000.0 / Stopwatch.Frequency;
                    _uiLagMs = _uiLagMs <= 0 ? ms : (_uiLagMs * 0.85 + ms * 0.15);
                    bool busy = ms > AppDefaults.UiLagBusyThresholdMs || _uiLagMs > AppDefaults.UiLagEmaBusyThresholdMs;
                    if (busy != _uiBusy)
                    {
                        _uiBusy = busy;
                        UpdateWorkerTarget();
                    }
                });
            }, null, dueTime: 0, period: AppDefaults.UiPulsePeriodMs);
        }
        catch { }
    }

    public void Dispose()
    {
        try { _schedCts.Cancel(); } catch { }
        _schedCts.Dispose();
        try { _uiPulseTimer?.Dispose(); } catch { }
        try { _drainRetryTimer?.Dispose(); } catch { }
        _decodeGate.Dispose();
    }

    /// <summary>
    /// Clears pending/inflight tracking state to allow re-scheduling of previously attempted items.
    /// Call this when user explicitly requests a refresh.
    /// </summary>
    public void ResetPendingState()
    {
        // 스케줄링 추적 상태 초기화
        _pendingScheduled.Clear();
        _decodeInProgressMax.Clear();
        _maxRequested.Clear();
        // inflight는 실제 진행 중인 작업이므로 유지
        
        // 로딩 상태도 초기화
        foreach (var kvp in _activeLoading)
        {
            try { kvp.Value.IsLoadingThumbnail = false; } catch { }
        }
        _activeLoading.Clear();
        
        // _applyScheduled 리셋 (데드락 해제)
        Interlocked.Exchange(ref _applyScheduled, 0);
        
        // epoch 증가하여 이전 요청들 무효화
        Interlocked.Increment(ref _epoch);
        
        // 큐에 남은 아이템 즉시 드레인
        ScheduleDrain();
    }
}
