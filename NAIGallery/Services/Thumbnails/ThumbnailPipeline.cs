using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using NAIGallery.Models;
using NAIGallery.Services.Thumbnails;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace NAIGallery.Services;

/// <summary>
/// 단순하고 안정적인 썸네일 파이프라인
/// </summary>
internal sealed class ThumbnailPipeline : IThumbnailPipeline, IDisposable
{
    private readonly ILogger? _logger;
    private readonly ThumbnailCache _cache;
    private DispatcherQueue? _dispatcher;
    
    // 동시 디코딩 제한
    private readonly SemaphoreSlim _decodeGate;
    private readonly SemaphoreSlim _applyGate = new(4, 4);
    
    // 요청 큐
    private readonly ConcurrentQueue<ThumbnailRequest> _highQueue = new();
    private readonly ConcurrentQueue<ThumbnailRequest> _normalQueue = new();
    
    // UI 적용 큐
    private readonly ConcurrentQueue<ApplyRequest> _applyQueue = new();
    
    // 상태 추적
    private readonly ConcurrentDictionary<string, int> _processing = new(StringComparer.OrdinalIgnoreCase);
    
    // 워커 관리
    private readonly CancellationTokenSource _cts = new();
    private int _workerCount;
    private readonly int _maxWorkers;
    
    // UI 상태
    private volatile bool _applySuspended;
    private int _applyScheduled;
    
    // 타이머
    private Timer? _workerTimer;
    
    public event Action<ImageMetadata>? ThumbnailApplied;

    private sealed record ThumbnailRequest(ImageMetadata Meta, int Width, bool HighPriority);
    private sealed record ApplyRequest(ImageMetadata Meta, PixelData Data, int Width);

    public ThumbnailPipeline(int capacityBytes, ILogger? logger = null)
    {
        _logger = logger;
        _cache = new ThumbnailCache(capacityBytes);
        _maxWorkers = Math.Clamp(Environment.ProcessorCount, 2, 8);
        _decodeGate = new SemaphoreSlim(_maxWorkers, _maxWorkers);
    }

    public int CacheCapacity
    {
        get => (int)_cache.Capacity;
        set => _cache.Capacity = value;
    }

    public void InitializeDispatcher(DispatcherQueue dispatcherQueue)
    {
        _dispatcher = dispatcherQueue;
        
        // 워커 관리 타이머 시작
        _workerTimer = new Timer(OnWorkerTimerTick, null, 100, 100);
        
        // 초기 워커 시작
        EnsureWorkers(2);
    }

    private void OnWorkerTimerTick(object? state)
    {
        // 큐에 항목이 있으면 워커 추가
        int pending = _highQueue.Count + _normalQueue.Count;
        if (pending > 0)
        {
            int needed = Math.Min(pending, _maxWorkers);
            EnsureWorkers(needed);
        }
        
        // 적용 큐 처리
        if (!_applyQueue.IsEmpty && !_applySuspended)
        {
            ScheduleApply();
        }
    }

    private void EnsureWorkers(int count)
    {
        while (_workerCount < count && _workerCount < _maxWorkers)
        {
            if (Interlocked.Increment(ref _workerCount) <= _maxWorkers)
            {
                _ = Task.Run(WorkerLoopAsync);
            }
            else
            {
                Interlocked.Decrement(ref _workerCount);
                break;
            }
        }
    }

    private async Task WorkerLoopAsync()
    {
        var token = _cts.Token;
        int idleCount = 0;
        
        try
        {
            while (!token.IsCancellationRequested)
            {
                ThumbnailRequest? request = null;
                
                // 높은 우선순위 먼저
                if (!_highQueue.TryDequeue(out request))
                {
                    _normalQueue.TryDequeue(out request);
                }
                
                if (request == null)
                {
                    idleCount++;
                    if (idleCount > 10 && _workerCount > 2)
                    {
                        // 유휴 워커 종료
                        break;
                    }
                    await Task.Delay(50, token);
                    continue;
                }
                
                idleCount = 0;
                
                try
                {
                    await ProcessRequestAsync(request, token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Worker error");
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _workerCount);
        }
    }

    private async Task ProcessRequestAsync(ThumbnailRequest request, CancellationToken ct)
    {
        var meta = request.Meta;
        int width = request.Width;
        
        if (meta?.FilePath == null || !File.Exists(meta.FilePath))
            return;
        
        // 이미 충분한 해상도가 있으면 스킵
        if ((meta.ThumbnailPixelWidth ?? 0) >= width)
            return;
        
        // 이미 처리 중이면 스킵
        int processingWidth = _processing.GetOrAdd(meta.FilePath, 0);
        if (processingWidth >= width)
            return;
        _processing[meta.FilePath] = width;
        
        try
        {
            // 캐시 확인
            string cacheKey = MakeCacheKey(meta.FilePath, width);
            if (_cache.TryGet(cacheKey, out var cached) && cached != null)
            {
                EnqueueApply(new ApplyRequest(meta, cached, width));
                return;
            }
            
            // 디코딩
            await _decodeGate.WaitAsync(ct);
            try
            {
                var pixelData = await DecodeImageAsync(meta.FilePath, width, ct);
                if (pixelData != null)
                {
                    _cache.Add(cacheKey, pixelData);
                    EnqueueApply(new ApplyRequest(meta, pixelData, width));
                }
            }
            finally
            {
                _decodeGate.Release();
            }
        }
        finally
        {
            _processing.TryRemove(meta.FilePath, out _);
        }
    }

    private async Task<PixelData?> DecodeImageAsync(string filePath, int targetWidth, CancellationToken ct)
    {
        try
        {
            using var fs = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var stream = fs.AsRandomAccessStream();
            
            BitmapDecoder decoder;
            try
            {
                decoder = await BitmapDecoder.CreateAsync(stream);
            }
            catch
            {
                Telemetry.DecodeFormatErrors.Add(1);
                return null;
            }
            
            uint srcW = decoder.PixelWidth;
            uint srcH = decoder.PixelHeight;
            if (srcW == 0 || srcH == 0) return null;
            
            // 스케일 계산
            double scale = Math.Min(1.0, targetWidth / (double)srcW);
            uint outW = (uint)Math.Max(1, Math.Round(srcW * scale));
            uint outH = (uint)Math.Max(1, Math.Round(srcH * scale));
            
            var transform = new BitmapTransform
            {
                ScaledWidth = outW,
                ScaledHeight = outH,
                InterpolationMode = BitmapInterpolationMode.Linear
            };
            
            SoftwareBitmap? bitmap = null;
            try
            {
                bitmap = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.DoNotColorManage);
            }
            catch (COMException)
            {
                // 포맷 변환 실패 시 원본 포맷으로 시도 후 변환
                try
                {
                    var temp = await decoder.GetSoftwareBitmapAsync(
                        decoder.BitmapPixelFormat,
                        decoder.BitmapAlphaMode,
                        transform,
                        ExifOrientationMode.RespectExifOrientation,
                        ColorManagementMode.DoNotColorManage);
                    
                    bitmap = SoftwareBitmap.Convert(temp, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                    temp.Dispose();
                }
                catch
                {
                    Telemetry.DecodeFormatErrors.Add(1);
                    return null;
                }
            }
            
            if (bitmap == null) return null;
            
            try
            {
                int byteCount = (int)(bitmap.PixelWidth * bitmap.PixelHeight * 4);
                var pixels = ArrayPool<byte>.Shared.Rent(byteCount);
                
                try
                {
                    bitmap.CopyToBuffer(pixels.AsBuffer());
                    return new PixelData(pixels, (int)bitmap.PixelWidth, (int)bitmap.PixelHeight, byteCount);
                }
                catch
                {
                    ArrayPool<byte>.Shared.Return(pixels);
                    return null;
                }
            }
            finally
            {
                bitmap.Dispose();
            }
        }
        catch (IOException)
        {
            Telemetry.DecodeIoErrors.Add(1);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            Telemetry.DecodeIoErrors.Add(1);
            return null;
        }
        catch (OperationCanceledException)
        {
            Telemetry.DecodeCanceled.Add(1);
            return null;
        }
        catch (Exception ex)
        {
            Telemetry.DecodeUnknownErrors.Add(1);
            _logger?.LogDebug(ex, "Decode error: {File}", filePath);
            return null;
        }
    }

    private void EnqueueApply(ApplyRequest request)
    {
        _applyQueue.Enqueue(request);
        ScheduleApply();
    }

    private void ScheduleApply()
    {
        if (_dispatcher == null || _applySuspended) return;
        if (Interlocked.CompareExchange(ref _applyScheduled, 1, 0) != 0) return;
        
        _dispatcher.TryEnqueue(DispatcherQueuePriority.Normal, ProcessApplyQueue);
    }

    private async void ProcessApplyQueue()
    {
        try
        {
            int count = 0;
            while (_applyQueue.TryDequeue(out var request) && count < 16)
            {
                await ApplyThumbnailAsync(request);
                count++;
                
                if (count % 4 == 0)
                    await Task.Yield();
            }
        }
        finally
        {
            Interlocked.Exchange(ref _applyScheduled, 0);
            
            if (!_applyQueue.IsEmpty && !_applySuspended)
            {
                ScheduleApply();
            }
        }
    }

    private async Task ApplyThumbnailAsync(ApplyRequest request)
    {
        var meta = request.Meta;
        var data = request.Data;
        int width = request.Width;
        
        if (!data.IsValid) return;
        if ((meta.ThumbnailPixelWidth ?? 0) >= width) return;
        
        await _applyGate.WaitAsync();
        try
        {
            if (!data.IsValid) return;
            
            var wb = new WriteableBitmap(data.Width, data.Height);
            using (var s = wb.PixelBuffer.AsStream())
            {
                int toWrite = Math.Min(data.ByteCount, (int)s.Length);
                s.Write(data.Pixels, 0, toWrite);
            }
            wb.Invalidate();
            
            meta.Thumbnail = wb;
            meta.ThumbnailPixelWidth = width;
            meta.IsLoadingThumbnail = false;
            
            // 원본 크기 업데이트
            if (!meta.OriginalWidth.HasValue || !meta.OriginalHeight.HasValue)
            {
                meta.AspectRatio = data.Width / (double)Math.Max(1, data.Height);
            }
            
            ThumbnailApplied?.Invoke(meta);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Apply error");
        }
        finally
        {
            _applyGate.Release();
        }
    }

    private static string MakeCacheKey(string filePath, int width)
    {
        try
        {
            var lastWrite = File.GetLastWriteTimeUtc(filePath).Ticks;
            return $"{filePath}|{lastWrite}|{width}";
        }
        catch
        {
            return $"{filePath}|0|{width}";
        }
    }

    #region Public API

    public void Schedule(ImageMetadata meta, int width, bool highPriority = false)
    {
        if (meta?.FilePath == null) return;
        if ((meta.ThumbnailPixelWidth ?? 0) >= width) return;
        
        var request = new ThumbnailRequest(meta, width, highPriority);
        if (highPriority)
            _highQueue.Enqueue(request);
        else
            _normalQueue.Enqueue(request);
        
        EnsureWorkers(1);
    }

    public void BoostVisible(IEnumerable<ImageMetadata> metas, int width)
    {
        foreach (var meta in metas)
        {
            Schedule(meta, width, highPriority: true);
        }
    }

    public void UpdateViewport(IReadOnlyList<ImageMetadata> orderedVisible, IReadOnlyList<ImageMetadata> bufferItems, int width)
    {
        // 화면에 보이는 항목 우선
        foreach (var meta in orderedVisible)
        {
            Schedule(meta, width, highPriority: true);
        }
        
        // 버퍼 항목
        foreach (var meta in bufferItems)
        {
            Schedule(meta, width, highPriority: false);
        }
    }

    public async Task EnsureThumbnailAsync(ImageMetadata meta, int decodeWidth, CancellationToken ct, bool allowDownscale)
    {
        if (meta?.FilePath == null) return;
        
        int current = meta.ThumbnailPixelWidth ?? 0;
        if (!allowDownscale && current >= decodeWidth) return;
        
        var request = new ThumbnailRequest(meta, decodeWidth, true);
        await ProcessRequestAsync(request, ct);
    }

    public async Task PreloadAsync(IEnumerable<ImageMetadata> items, int decodeWidth, CancellationToken ct, int maxParallelism)
    {
        if (maxParallelism <= 0)
            maxParallelism = Math.Max(2, Environment.ProcessorCount / 2);
        
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = maxParallelism,
            CancellationToken = ct
        };
        
        await Parallel.ForEachAsync(items, options, async (meta, token) =>
        {
            await EnsureThumbnailAsync(meta, decodeWidth, token, false);
        });
    }

    public void SetApplySuspended(bool suspended)
    {
        _applySuspended = suspended;
        if (!suspended) ScheduleApply();
    }

    public void FlushApplyQueue() => ScheduleApply();

    public void DrainVisible(HashSet<ImageMetadata> visible)
    {
        if (_dispatcher == null || visible.Count == 0) return;
        
        var priority = new List<ApplyRequest>();
        var remainder = new List<ApplyRequest>();
        
        while (_applyQueue.TryDequeue(out var req))
        {
            if (visible.Contains(req.Meta))
                priority.Add(req);
            else
                remainder.Add(req);
        }
        
        // 우선 항목 먼저 다시 추가
        foreach (var req in priority)
            _applyQueue.Enqueue(req);
        foreach (var req in remainder)
            _applyQueue.Enqueue(req);
        
        ScheduleApply();
    }

    public void ClearCache() => _cache.Clear();

    public void ResetPendingState()
    {
        // 큐 비우기
        while (_highQueue.TryDequeue(out _)) { }
        while (_normalQueue.TryDequeue(out _)) { }
        
        _processing.Clear();
        
        // 적용 큐 유지 (이미 디코딩된 것은 적용)
        ScheduleApply();
    }

    #endregion

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _workerTimer?.Dispose();
        _decodeGate.Dispose();
        _applyGate.Dispose();
        _cache.Dispose();
    }
}
