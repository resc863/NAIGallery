using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using NAIGallery.Models;

namespace NAIGallery.Services;

internal sealed partial class ThumbnailPipeline
{
    #region Worker Management
    
    private void UpdateWorkerTarget()
    {
        int backlog = Math.Max(0, Volatile.Read(ref _highBacklog)) + Math.Max(0, Volatile.Read(ref _normalBacklog));
        int cpu = Math.Max(4, Environment.ProcessorCount);
        int ideal;

        if (_memoryPressure)
            ideal = Math.Max(2, cpu / 4);
        else if (_uiBusy)
            ideal = Math.Max(2, cpu / 3); // UI 바쁠 때 더 적극적으로 줄임
        else if (backlog <= 4)
            ideal = Math.Max(4, cpu / 2);
        else if (backlog <= 16)
            ideal = Math.Min(cpu - 1, 8); // 최대값 축소
        else
            ideal = Math.Min(cpu, 10); // 최대값 축소

        ideal = Math.Clamp(ideal, 2, 10); // 최대 10개로 제한
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
        // 고속 채널에서 먼저 시도
        while (_chHigh.Reader.TryRead(out var hItem))
        {
            Interlocked.Decrement(ref _highBacklog);
            if (hItem.Epoch >= _epoch - 3) // epoch 허용 범위 축소
                return hItem;
        }
        
        if (_chNormal.Reader.TryRead(out var n))
        {
            Interlocked.Decrement(ref _normalBacklog);
            return n;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(200); // 타임아웃 축소
        try
        {
            var highTask = _chHigh.Reader.WaitToReadAsync(timeoutCts.Token).AsTask();
            var delayTask = Task.Delay(20, timeoutCts.Token); // 지연 축소
            
            var first = await Task.WhenAny(highTask, delayTask).ConfigureAwait(false);
            if (first == highTask && highTask.Result && _chHigh.Reader.TryRead(out var h1))
            {
                Interlocked.Decrement(ref _highBacklog);
                return h1;
            }
            
            if (_chNormal.Reader.TryRead(out n))
            {
                Interlocked.Decrement(ref _normalBacklog);
                return n;
            }
            
            var normTask = _chNormal.Reader.WaitToReadAsync(timeoutCts.Token).AsTask();
            var completed = await Task.WhenAny(highTask, normTask).ConfigureAwait(false);
            if (completed.Result)
            {
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
        int consecutiveSkips = 0; // 연속 스킵 카운터
        
        try
        {
            while (!token.IsCancellationRequested)
            {
                CheckMemoryPressure();

                if (_memoryPressure)
                {
                    await Task.Delay(150, token).ConfigureAwait(false); // 지연 증가
                    try { GC.Collect(0, GCCollectionMode.Optimized); } catch { }
                }

                // UI가 바쁠 때 추가 지연
                if (_uiBusy)
                {
                    await Task.Delay(30, token).ConfigureAwait(false);
                }

                var reqObj = await TryDequeueAsync(token).ConfigureAwait(false);
                if (reqObj is null)
                {
                    if (_activeWorkers > 3) break; // 유휴 워커 더 빨리 종료
                    continue;
                }

                var req = reqObj;
                int currentEpoch = _epoch;
                
                // epoch가 너무 오래된 요청은 건너뜀
                if (req.Epoch < currentEpoch - 3)
                {
                    consecutiveSkips++;
                    if (consecutiveSkips > 10)
                    {
                        await Task.Delay(10, token).ConfigureAwait(false);
                        consecutiveSkips = 0;
                    }
                    continue;
                }
                
                consecutiveSkips = 0;
                
                // 이미 충분한 해상도의 썸네일이 있으면 건너뜀
                if ((req.Meta.ThumbnailPixelWidth ?? 0) >= req.Width) continue;

                try
                {
                    await EnsureThumbnailAsync(req.Meta, req.Width, token, false).ConfigureAwait(false);

                    // 메모리 압박이나 UI 바쁠 때 추가 지연
                    if (_memoryPressure || _uiBusy)
                    {
                        await Task.Delay(10, token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref _canceled);
                    Telemetry.DecodeCanceled.Add(1);
                }
                catch (System.Runtime.InteropServices.COMException) { }
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
            _memoryPressure = usedRatio > 0.85; // 임계값 약간 낮춤

            if (_memoryPressure && !wasUnderPressure)
            {
                _cache.Capacity = Math.Max(1000, _cache.Capacity / 2);
            }
        }
        catch { }
    }
    
    #endregion
}
