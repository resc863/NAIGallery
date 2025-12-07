using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using NAIGallery.Models;
using NAIGallery.Services.Thumbnails;
using System.Threading.Channels;

namespace NAIGallery.Services;

internal sealed partial class ThumbnailPipeline : IThumbnailPipeline, IDisposable
{
    #region Fields
    
    private readonly ILogger? _logger;
    private DispatcherQueue? _dispatcher;
    private readonly ThumbnailCache _cache;

    // Error / stats
    private long _ioErrors, _formatErrors, _canceled, _unknownErrors;
    private long _comUnsupportedFormat, _comOutOfMemory, _comAccessDenied, _comWrongState, _comDeviceLost, _comOther;

    private readonly ConcurrentDictionary<string, byte> _inflight = new();
    
    // Active loading tracking (for UI state)
    private readonly ConcurrentDictionary<string, ImageMetadata> _activeLoading = new(StringComparer.OrdinalIgnoreCase);
    
    // Decode concurrency - 더 적극적인 제한
    private static readonly int MaxDecodeParallelism = Math.Clamp(Environment.ProcessorCount - 1, 2, 10);
    private readonly SemaphoreSlim _decodeGate = new(MaxDecodeParallelism);
    
    // Bitmap creation gate - 더 적극적인 제한
    private static readonly SemaphoreSlim _bitmapCreationGate = new(4, 4);
    
    private readonly ConcurrentDictionary<string, int> _fileGeneration = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _maxRequested = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _decodeInProgressMax = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _pendingScheduled = new(StringComparer.OrdinalIgnoreCase);

    // Apply queue
    private readonly ConcurrentQueue<(ImageMetadata Meta, PixelEntry Entry, int Width, int Gen)> _applyQ = new();
    private int _applyScheduled;
    private volatile bool _applySuspended;
    
    // Drain retry timer
    private Timer? _drainRetryTimer;
    
    #endregion

    #region Events
    
    /// <inheritdoc />
    public event Action<ImageMetadata>? ThumbnailApplied;
    
    #endregion

    #region Request Types
    
    private sealed record ThumbReq(ImageMetadata Meta, int Width, bool High, int Epoch);

    #endregion

    #region Priority Channels
    
    private readonly Channel<ThumbReq> _chHigh = Channel.CreateBounded<ThumbReq>(
        new BoundedChannelOptions(1024) { SingleReader = false, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Channel<ThumbReq> _chNormal = Channel.CreateBounded<ThumbReq>(
        new BoundedChannelOptions(4096) { SingleReader = false, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest });

    private int _highBacklog = 0, _normalBacklog = 0;
    
    #endregion

    #region Worker State
    
    private readonly CancellationTokenSource _schedCts = new();
    private int _activeWorkers = 0;
    private volatile int _epoch = 0;
    private int _targetWorkers = Math.Clamp(Environment.ProcessorCount, 4, 12);
    
    #endregion

    #region UI Monitoring
    
    private Timer? _uiPulseTimer;
    private double _uiLagMs;
    private volatile bool _uiBusy;
    
    #endregion

    #region Memory Pressure
    
    private volatile bool _memoryPressure = false;
    private long _lastMemoryCheckTicks = 0;
    private const long MemoryCheckIntervalTicks = TimeSpan.TicksPerSecond;
    
    // Bitmap creation throttling
    private long _lastBitmapCreationTicks = 0;
    private const long MinBitmapCreationIntervalTicks = TimeSpan.TicksPerMillisecond / 2;
    
    #endregion

    #region Constructor
    
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap))]
    public ThumbnailPipeline(int capacityBytes, ILogger? logger = null)
    {
        _logger = logger;
        _cache = new ThumbnailCache(Math.Max(1, capacityBytes));
    }
    
    #endregion

    #region Initialization
    
    public void InitializeDispatcher(DispatcherQueue dispatcherQueue)
    {
        _dispatcher = dispatcherQueue;
        StartUiPulseMonitor();
        StartDrainRetryTimer();
        EnsureWorkers();
    }

    private void StartDrainRetryTimer()
    {
        _drainRetryTimer?.Dispose();
        _drainRetryTimer = new Timer(_ =>
        {
            // 일시 중지 상태면 drain하지 않음
            if (_applySuspended) return;
            
            if (!_applyQ.IsEmpty && Volatile.Read(ref _applyScheduled) == 0)
            {
                ScheduleDrain();
            }
            else if (!_applyQ.IsEmpty && Volatile.Read(ref _applyScheduled) == 1)
            {
                Interlocked.Exchange(ref _applyScheduled, 0);
                ScheduleDrain();
            }
        }, null, dueTime: 150, period: 80); // 주기 증가
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
                _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => // Low 우선순위 사용
                {
                    double ms = (Stopwatch.GetTimestamp() - stamp) * 1000.0 / Stopwatch.Frequency;
                    _uiLagMs = _uiLagMs <= 0 ? ms : (_uiLagMs * 0.8 + ms * 0.2); // 더 빠른 반응
                    bool busy = ms > AppDefaults.UiLagBusyThresholdMs || _uiLagMs > AppDefaults.UiLagEmaBusyThresholdMs;
                    if (busy != _uiBusy)
                    {
                        _uiBusy = busy;
                        UpdateWorkerTarget();
                        
                        // UI가 바빠지면 apply 일시 중지
                        if (busy && !_applySuspended)
                        {
                            // 자동으로 중지하지는 않음 - 외부에서 제어
                        }
                    }
                });
            }, null, dueTime: 0, period: AppDefaults.UiPulsePeriodMs);
        }
        catch { }
    }
    
    #endregion

    #region Public Properties
    
    public int CacheCapacity
    {
        get => (int)Math.Min(int.MaxValue, _cache.Capacity);
        set => _cache.Capacity = Math.Max(1, value);
    }
    
    #endregion

    #region Public Methods - Suspend/Flush
    
    public void SetApplySuspended(bool suspended)
    {
        _applySuspended = suspended;
        if (!suspended) ScheduleDrain();
    }

    public void FlushApplyQueue() => ScheduleDrain();

    public void DrainVisible(HashSet<ImageMetadata> visible)
    {
        if (_dispatcher == null || visible.Count == 0) return;
        
        var toProcess = new List<(ImageMetadata Meta, PixelEntry Entry, int Width, int Gen)>();
        var remainder = new List<(ImageMetadata, PixelEntry, int, int)>();
        
        while (_applyQ.TryDequeue(out var it))
        {
            if (visible.Contains(it.Meta) && toProcess.Count < 32)
                toProcess.Add(it);
            else
                remainder.Add(it);
        }
        
        foreach (var r in remainder) _applyQ.Enqueue(r);
        
        if (toProcess.Count == 0) return;
        
        bool enqueued = _dispatcher.TryEnqueue(DispatcherQueuePriority.High, async () =>
        {
            foreach (var item in toProcess)
            {
                try { await TryApplyFromEntryAsync(item.Meta, item.Entry, item.Width, item.Gen, true); }
                catch { }
            }
        });
        
        if (!enqueued)
        {
            foreach (var item in toProcess) _applyQ.Enqueue(item);
        }
    }

    public void ClearCache()
    {
        _cache.Clear();
    }
    
    #endregion

    #region Public Methods - Scheduling
    
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
    
    #endregion

    #region Public Methods - Thumbnail Loading
    
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
                _cache.Touch(MakeCacheKey(meta.FilePath, ticks, decodeWidth));
                return;
            }

            int inProg = _decodeInProgressMax.AddOrUpdate(meta.FilePath, decodeWidth, (_, old) => decodeWidth > old ? decodeWidth : old);
            if (inProg > decodeWidth) return;

            string key = MakeCacheKey(meta.FilePath, ticks, decodeWidth);
            if (_cache.TryGet(key, out var entry))
            {
                int gen = _fileGeneration.AddOrUpdate(meta.FilePath, 1, (_, g) => g + 1);
                EnqueueApply(meta, entry!, decodeWidth, gen, allowDownscale);
                _decodeInProgressMax.TryRemove(meta.FilePath, out _);
                return;
            }

            SetLoadingState(meta, true);

            try
            {
                int small = Math.Clamp(decodeWidth / 2, AppDefaults.SmallThumbMin, AppDefaults.SmallThumbMax);
                bool shouldDoProgressive = !allowDownscale && current == 0 && decodeWidth > small * 2;
                
                if (shouldDoProgressive)
                {
                    int smallMarker = _decodeInProgressMax.AddOrUpdate(meta.FilePath, small, (_, old) => old > small ? old : small);
                    if (smallMarker == small)
                    {
                        int sgen = _fileGeneration.AddOrUpdate(meta.FilePath, 1, (_, g) => g + 1);
                        _ = LoadDecodeAsync(meta, small, ticks, ct, sgen, false);
                    }
                }

                int mainGen = _fileGeneration.AddOrUpdate(meta.FilePath, 1, (_, g) => g + 1);
                await LoadDecodeAsync(meta, decodeWidth, ticks, ct, mainGen, allowDownscale).ConfigureAwait(false);
            }
            finally
            {
                _decodeInProgressMax.TryRemove(meta.FilePath, out _);
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
    
    #endregion

    #region Public Methods - State Reset
    
    public void ResetPendingState()
    {
        // 모든 스케줄링/진행 상태 클리어
        _pendingScheduled.Clear();
        _decodeInProgressMax.Clear();
        _maxRequested.Clear();
        _inflight.Clear();
        _fileGeneration.Clear();
        
        // 로딩 상태 초기화
        foreach (var kvp in _activeLoading)
        {
            try { kvp.Value.IsLoadingThumbnail = false; } catch { }
        }
        _activeLoading.Clear();
        
        // 채널 비우기 (오래된 요청 제거)
        while (_chHigh.Reader.TryRead(out _)) 
            Interlocked.Decrement(ref _highBacklog);
        while (_chNormal.Reader.TryRead(out _)) 
            Interlocked.Decrement(ref _normalBacklog);
        
        Interlocked.Exchange(ref _applyScheduled, 0);
        Interlocked.Increment(ref _epoch);
        
        ScheduleDrain();
    }
    
    #endregion

    #region Private Helpers
    
    private void SetLoadingState(ImageMetadata meta, bool isLoading)
    {
        if (_dispatcher == null) return;
        
        if (isLoading)
            _activeLoading.TryAdd(meta.FilePath, meta);
        else
            _activeLoading.TryRemove(meta.FilePath, out _);
        
        _dispatcher.TryEnqueue(DispatcherQueuePriority.Normal, () =>
        {
            try { meta.IsLoadingThumbnail = isLoading; } catch { }
        });
    }

    private static string MakeCacheKey(string file, long ticks, int width)
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

    private static long SafeGetTicks(string file)
    {
        try { return new FileInfo(file).LastWriteTimeUtc.Ticks; }
        catch { return 0; }
    }
    
    #endregion

    #region IDisposable
    
    public void Dispose()
    {
        try { _schedCts.Cancel(); } catch { }
        _schedCts.Dispose();
        try { _uiPulseTimer?.Dispose(); } catch { }
        try { _drainRetryTimer?.Dispose(); } catch { }
        _decodeGate.Dispose();
        _cache.Dispose();
    }
    
    #endregion
}
