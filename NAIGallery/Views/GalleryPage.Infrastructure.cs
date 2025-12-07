using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using NAIGallery.Models;
using NAIGallery.Services;

namespace NAIGallery.Views;

public sealed partial class GalleryPage
{
    private CancellationTokenSource? _scrollIdleCts;
    private bool _scrollingUp = false; // direction flag
    private double _lastOffsetForDir = 0; // for direction detection
    private const double PrefetchBufferFactor = 1.5; // extra viewport heights to prefetch
    private volatile int _idleBackfillCursor = 0; // progressive backfill pointer for remaining thumbnails

    // Scroll throttling - 스크롤 중에는 최소한의 업데이트만 수행
    private long _lastScrollProcessTicks = 0;
    private const long MinScrollProcessIntervalTicks = TimeSpan.TicksPerMillisecond * 50; // 50ms마다만 처리 (20fps)

    // 스크롤 idle 감지용 타이머
    private long _lastScrollEventTicks = 0;
    private const long ScrollIdleThresholdTicks = TimeSpan.TicksPerMillisecond * 120; // 120ms 후 idle 판정

    // 고속 스크롤 감지
    private double _scrollVelocity = 0;
    private double _prevScrollOffset = 0;
    private long _prevScrollTicks = 0;
    private const double FastScrollThreshold = 2000; // pixels per second

    private void HookScroll()
    {
        try { _scrollViewer = RepeaterScroll; } catch { _scrollViewer = null; }
        if (_scrollViewer == null) return;
        _lastScrollOffset = _scrollViewer.VerticalOffset;
        _lastOffsetForDir = _lastScrollOffset;
        _prevScrollOffset = _lastScrollOffset;
        _prevScrollTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        
        _scrollViewer.ViewChanged += (s, args) =>
        {
            if (!_isLoaded) return;
            
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            _lastScrollEventTicks = now;
            
            double current = _scrollViewer.VerticalOffset;
            _scrollingUp = current < _lastOffsetForDir - 0.5; // small hysteresis
            _lastOffsetForDir = current;
            bool intermediate = args.IsIntermediate || Math.Abs(current - _lastScrollOffset) > 0.5;
            _isScrollBubbling = intermediate;

            // 스크롤 속도 계산
            long ticksDiff = now - _prevScrollTicks;
            if (ticksDiff > 0)
            {
                double secondsDiff = ticksDiff / (double)System.Diagnostics.Stopwatch.Frequency;
                double pixelsDiff = Math.Abs(current - _prevScrollOffset);
                _scrollVelocity = pixelsDiff / Math.Max(0.001, secondsDiff);
                _prevScrollOffset = current;
                _prevScrollTicks = now;
            }
            
            bool isFastScrolling = _scrollVelocity > FastScrollThreshold;

            if (intermediate)
            {
                // 스크롤 중에는 적용 일시 중지
                _service.SetApplySuspended(true);
                
                _scrollIdleCts?.Cancel();
                _scrollIdleCts = new CancellationTokenSource();
                _idleFillCts?.Cancel();
                
                // 고속 스크롤 중에는 더 긴 간격으로만 처리
                long minInterval = isFastScrolling ? TimeSpan.TicksPerMillisecond * 100 : MinScrollProcessIntervalTicks;
                
                if (now - _lastScrollProcessTicks >= minInterval)
                {
                    _lastScrollProcessTicks = now;
                    
                    // 고속 스크롤 중에는 viewport 업데이트만, 느린 스크롤에서는 가시 영역도 처리
                    if (!isFastScrolling)
                    {
                        EnqueueVisibleStrict();
                    }
                    UpdateSchedulerViewportInternal();
                }
            }
            else
            {
                // 스크롤 완료 - 즉시 처리 시작
                _scrollVelocity = 0;
                EnqueueVisibleStrict();
                UpdateSchedulerViewportInternal();
                _service.SetApplySuspended(false);
                _service.FlushApplyQueue();
            }

            // Idle 타이머 설정
            _scrollIdleCts?.Cancel();
            _scrollIdleCts = new CancellationTokenSource();
            var ct = _scrollIdleCts.Token;
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(100, ct); } catch { return; }
                if (ct.IsCancellationRequested) return;
                
                // 추가적 스크롤 이벤트가 없었는지 확인
                long currentTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                if (currentTicks - _lastScrollEventTicks < ScrollIdleThresholdTicks - TimeSpan.TicksPerMillisecond * 50)
                    return;
                
                DispatcherQueue?.TryEnqueue(() =>
                {
                    _scrollVelocity = 0;
                    _service.SetApplySuspended(false);
                    _service.FlushApplyQueue();
                    EnqueueVisibleStrict();
                    _ = ProcessQueueAsync();
                    StartIdleFill();
                });
            });

            _lastScrollOffset = current;
        };
    }

    private void TryDrainVisibleDuringScroll()
    {
        // 고속 스크롤 중에는 drain 하지 않음
        if (_scrollVelocity > FastScrollThreshold) return;
        
        try
        {
            if (ViewModel.Images.Count == 0) return;
            var set = new HashSet<ImageMetadata>();
            int count = Math.Min(_viewEndIndex - _viewStartIndex + 1, 30); // 30개로 축소
            for (int i = _viewStartIndex; i <= _viewEndIndex && i < ViewModel.Images.Count && set.Count < count; i++)
            {
                if (i >= 0) set.Add(ViewModel.Images[i]);
            }
            if (set.Count > 0) _service.DrainVisible(set);
        }
        catch { }
    }

    private sealed class IdleUiSnapshot
    {
        public int DesiredWidth = 256;
        public List<ImageMetadata> MissingRealized = new();
        public List<ImageMetadata> MissingVisible = new();
        public List<ImageMetadata> BufferCandidates = new();
        public List<ImageMetadata> RemainingOrdered = new();
        public bool HasImages;
    }

    private Task<IdleUiSnapshot?> CaptureIdleSnapshotAsync(CancellationToken token)
    {
        var tcs = new TaskCompletionSource<IdleUiSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (DispatcherQueue == null)
        {
            tcs.TrySetResult(null); return tcs.Task;
        }
        DispatcherQueue.TryEnqueue(() =>
        {
            if (token.IsCancellationRequested) { tcs.TrySetResult(null); return; }
            try
            {
                var images = ViewModel.Images;
                int imageCount = images.Count;
                if (_scrollViewer == null || imageCount == 0)
                { tcs.TrySetResult(new IdleUiSnapshot { HasImages = false }); return; }

                var snap = new IdleUiSnapshot { HasImages = true, DesiredWidth = GetDesiredDecodeWidth() };

                // Realized tiles - limit iteration
                try
                {
                    var host = GetItemsHost();
                    if (host != null && host.Children.Count > 0)
                    {
                        int maxCheck = Math.Min(host.Children.Count, 100); // 100개로 축소
                        for (int c = 0; c < maxCheck; c++)
                        {
                            if (host.Children[c] is FrameworkElement fe && fe.DataContext is ImageMetadata meta)
                            {
                                int cur = meta.ThumbnailPixelWidth ?? 0;
                                if (meta.Thumbnail == null || cur + 32 < snap.DesiredWidth)
                                    snap.MissingRealized.Add(meta);
                            }
                        }
                    }
                }
                catch { }

                // Clamp view indices defensively
                int viewStart = _viewStartIndex;
                int viewEnd = _viewEndIndex;
                if (viewStart < 0) viewStart = 0;
                if (viewEnd >= imageCount) viewEnd = imageCount - 1;
                if (viewEnd < viewStart) { viewStart = 0; viewEnd = -1; }

                // Visible logical range - limit to reasonable count
                int maxVisible = 60; // 60개로 축소
                if (viewStart <= viewEnd && viewStart >= 0 && viewEnd < imageCount)
                {
                    int count = 0;
                    for (int i = viewStart; i <= viewEnd && count < maxVisible; i++)
                    {
                        if (i < 0 || i >= imageCount) break;
                        var m = images[i];
                        int cur = m.ThumbnailPixelWidth ?? 0;
                        if (m.Thumbnail == null || cur + 32 < snap.DesiredWidth)
                        {
                            snap.MissingVisible.Add(m);
                            count++;
                        }
                    }
                }

                // Buffer prefetch indices (direction-biased) - limit candidates
                int bufStartIndex = 0, bufEndIndex = -1;
                try
                {
                    double viewport = _scrollViewer.ViewportHeight;
                    double offset = _scrollViewer.VerticalOffset;
                    double bufferTopExtent = viewport * PrefetchBufferFactor * (_scrollingUp ? 2.0 : 1.0);
                    double bufferBottomExtent = viewport * PrefetchBufferFactor * (_scrollingUp ? 1.0 : 2.0);
                    double bufferTop = Math.Max(0, offset - bufferTopExtent);
                    double bufferBottom = offset + viewport + bufferBottomExtent;
                    int cols = Math.Max(1, (int)((GalleryView?.ActualWidth > 0 ? GalleryView.ActualWidth : ActualWidth) / Math.Max(1, _baseItemSize)));
                    double itemH = _baseItemSize;
                    int bufStartRow = Math.Max(0, (int)(bufferTop / itemH));
                    int bufEndRow = Math.Max(bufStartRow, (int)(bufferBottom / itemH));
                    bufStartIndex = Math.Max(0, bufStartRow * cols);
                    bufEndIndex = Math.Min((bufEndRow + 1) * cols - 1, imageCount - 1);
                    
                    int maxBuffer = 100; // 100개로 축소
                    int bufferCount = 0;
                    if (bufStartIndex < imageCount && bufEndIndex >= 0)
                    {
                        for (int i = bufStartIndex; i <= bufEndIndex && bufferCount < maxBuffer; i++)
                        {
                            if (i >= imageCount) break;
                            if (i >= viewStart && i <= viewEnd) continue;
                            if (i < 0) continue;
                            var m = images[i];
                            int cur = m.ThumbnailPixelWidth ?? 0;
                            if (m.Thumbnail == null || cur + 32 < snap.DesiredWidth)
                            {
                                snap.BufferCandidates.Add(m);
                                bufferCount++;
                            }
                        }
                    }
                }
                catch { }

                // Remaining outside buffer - limit for performance
                try
                {
                    int total = imageCount;
                    int maxRemaining = 200; // 200개로 축소
                    if (total > 0 && viewEnd >= viewStart && viewStart >= 0)
                    {
                        int left = viewStart - 1;
                        int right = viewEnd + 1;
                        while ((left >= 0 || right < total) && snap.RemainingOrdered.Count < maxRemaining)
                        {
                            bool addedAny = false;
                            if (left >= 0 && (left < bufStartIndex || left > bufEndIndex))
                            {
                                if (left < total)
                                {
                                    var m = images[left];
                                    int cur = m.ThumbnailPixelWidth ?? 0;
                                    if (m.Thumbnail == null || cur + 32 < snap.DesiredWidth)
                                        snap.RemainingOrdered.Add(m);
                                }
                                left--; addedAny = true;
                            }
                            if (right < total && (right < bufStartIndex || right > bufEndIndex) && snap.RemainingOrdered.Count < maxRemaining)
                            {
                                if (right >= 0)
                                {
                                    var m = images[right];
                                    int cur = m.ThumbnailPixelWidth ?? 0;
                                    if (m.Thumbnail == null || cur + 32 < snap.DesiredWidth)
                                        snap.RemainingOrdered.Add(m);
                                }
                                right++; addedAny = true;
                            }
                            if (!addedAny) break;
                        }
                    }
                }
                catch { }

                tcs.TrySetResult(snap);
            }
            catch { tcs.TrySetResult(null); }
        });
        return tcs.Task;
    }

    private void StartIdleFill()
    {
        try { _idleFillCts?.Cancel(); } catch { }
        _idleFillCts = new CancellationTokenSource();
        var token = _idleFillCts.Token;
        int idleCyclesWithoutWork = 0;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                IdleUiSnapshot? snap = null;
                try { snap = await CaptureIdleSnapshotAsync(token).ConfigureAwait(false); } catch { }
                if (token.IsCancellationRequested) break;
                if (snap == null || !snap.HasImages) { try { await Task.Delay(150, token); } catch { } continue; }

                bool work = false;
                int desiredWidth = snap.DesiredWidth;
                try
                {
                    if (snap.MissingRealized.Count > 0)
                    {
                        (_service as ImageIndexService)?.BoostVisible(snap.MissingRealized, desiredWidth);
                        work = true;
                    }
                    if (snap.MissingVisible.Count > 0)
                    {
                        (_service as ImageIndexService)?.BoostVisible(snap.MissingVisible, desiredWidth);
                        work = true;
                    }
                    if (snap.BufferCandidates.Count > 0)
                    {
                        // 배치 크기 축소
                        int batchSize = Math.Min(snap.BufferCandidates.Count, 32);
                        for (int i = 0; i < batchSize; i++)
                            (_service as ImageIndexService)?.Schedule(snap.BufferCandidates[i], desiredWidth, false);
                        work = true;
                    }
                    // Progressive backfill
                    if (!work && snap.RemainingOrdered.Count > 0)
                    {
                        int cursor = _idleBackfillCursor;
                        if (cursor < snap.RemainingOrdered.Count)
                        {
                            int batch = Math.Min(32, snap.RemainingOrdered.Count - cursor);
                            for (int i = 0; i < batch; i++)
                            {
                                var m = snap.RemainingOrdered[cursor + i];
                                (_service as ImageIndexService)?.Schedule(m, desiredWidth, false);
                            }
                            _idleBackfillCursor = cursor + batch;
                            work = true;
                        }
                    }
                    if (work)
                    {
                        _service.SetApplySuspended(false);
                        _service.FlushApplyQueue();
                    }
                }
                catch { }

                if (!work) idleCyclesWithoutWork++; else idleCyclesWithoutWork = 0;

                // idle fill 주기
                int delay = idleCyclesWithoutWork switch
                {
                    < 2 => 50,   // 약간 증가
                    < 5 => 80,
                    < 15 => 150,
                    _ => 300
                };
                try { await Task.Delay(delay, token); } catch { break; }
            }
        }, token);
    }

    private void RequestReflow(int delayMs = 60)
    {
        try { _reflowCts?.Cancel(); } catch { }
        _reflowCts = new CancellationTokenSource();
        var ct = _reflowCts.Token;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(delayMs, ct); } catch { return; }
            if (ct.IsCancellationRequested) return;
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    GalleryView?.InvalidateMeasure();
                    GalleryView?.InvalidateArrange();
                    EnqueueVisibleStrict();
                    UpdateSchedulerViewportInternal();
                    StartIdleFill();
                }
                catch { }
            });
        });
    }

    private void UpdateSchedulerViewportInternal()
    {
        try
        {
            if (_scrollViewer == null || ViewModel.Images.Count == 0) return;
            double viewport = _scrollViewer.ViewportHeight;
            double offset = _scrollViewer.VerticalOffset;
            double bufferTopExtent = viewport * PrefetchBufferFactor * (_scrollingUp ? 2.0 : 1.0);
            double bufferBottomExtent = viewport * PrefetchBufferFactor * (_scrollingUp ? 1.0 : 2.0);

            int cols = Math.Max(1, (int)((GalleryView?.ActualWidth > 0 ? GalleryView.ActualWidth : ActualWidth) / Math.Max(1, _baseItemSize)));
            double itemH = _baseItemSize;
            int startRow = Math.Max(0, (int)(offset / itemH));
            int endRow = Math.Max(startRow, (int)((offset + viewport) / itemH));

            int startIndex = Math.Max(0, startRow * cols);
            int endIndex = Math.Min((endRow + 1) * cols - 1, ViewModel.Images.Count - 1);

            if (startIndex >= ViewModel.Images.Count || endIndex < startIndex)
            {
                return;
            }

            int visibleCount = Math.Min(endIndex - startIndex + 1, 100); // 100개로 축소
            var orderedVisible = new List<ImageMetadata>(visibleCount);
            if (_scrollingUp)
            {
                for (int i = startIndex; i <= endIndex && orderedVisible.Count < visibleCount; i++) 
                    orderedVisible.Add(ViewModel.Images[i]);
            }
            else
            {
                int mid = (startIndex + endIndex) / 2; int lo = mid; int hi = mid + 1;
                while ((lo >= startIndex || hi <= endIndex) && orderedVisible.Count < visibleCount)
                {
                    if (lo >= startIndex) { orderedVisible.Add(ViewModel.Images[lo]); lo--; }
                    if (hi <= endIndex && orderedVisible.Count < visibleCount) { orderedVisible.Add(ViewModel.Images[hi]); hi++; }
                }
            }

            double bufferTop = Math.Max(0, offset - bufferTopExtent);
            double bufferBottom = offset + viewport + bufferBottomExtent;
            int bufStartRow = Math.Max(0, (int)(bufferTop / itemH));
            int bufEndRow = Math.Max(bufStartRow, (int)(bufferBottom / itemH));
            int bufStartIndex = Math.Max(0, bufStartRow * cols);
            int bufEndIndex = Math.Min((bufEndRow + 1) * cols - 1, ViewModel.Images.Count - 1);
            
            int maxBuffer = 120; // 120개로 축소
            var bufferList = new List<ImageMetadata>(Math.Min(bufEndIndex - bufStartIndex + 1, maxBuffer));
            for (int i = bufStartIndex; i <= bufEndIndex && bufferList.Count < maxBuffer; i++)
            {
                if (i >= startIndex && i <= endIndex) continue;
                bufferList.Add(ViewModel.Images[i]);
            }

            int desiredWidth = GetDesiredDecodeWidth();
            (_service as ImageIndexService)?.UpdateViewport(orderedVisible, bufferList, desiredWidth);
            _idleBackfillCursor = 0;
        }
        catch { }
    }

    // Wrapper for external calls
    private void UpdateSchedulerViewport() => UpdateSchedulerViewportInternal();
}
