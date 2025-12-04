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

    // Scroll throttling - 더 자주 업데이트하도록 간격 단축
    private long _lastScrollProcessTicks = 0;
    private const long MinScrollProcessIntervalTicks = TimeSpan.TicksPerMillisecond * 5; // ~200fps로 업데이트

    // 연속 스크롤 감지를 위한 타이머
    private long _lastScrollEventTicks = 0;
    private const long ScrollIdleThresholdTicks = TimeSpan.TicksPerMillisecond * 80; // 80ms로 단축

    private void HookScroll()
    {
        try { _scrollViewer = RepeaterScroll; } catch { _scrollViewer = null; }
        if (_scrollViewer == null) return;
        _lastScrollOffset = _scrollViewer.VerticalOffset;
        _lastOffsetForDir = _lastScrollOffset;
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

            if (intermediate)
            {
                // 스크롤 중에도 썸네일 적용 허용 - 절대 중단하지 않음
                _scrollIdleCts?.Cancel();
                _scrollIdleCts = new CancellationTokenSource();
                _idleFillCts?.Cancel();
                
                // 쓰로틀링된 스크롤 처리 - 더 자주 처리
                if (now - _lastScrollProcessTicks >= MinScrollProcessIntervalTicks)
                {
                    _lastScrollProcessTicks = now;
                    EnqueueVisibleStrict();
                    UpdateSchedulerViewportInternal();
                    
                    // 스크롤 중에도 가시 영역의 썸네일 적용 허용
                    _service.SetApplySuspended(false);
                    TryDrainVisibleDuringScroll();
                }
            }
            else
            {
                // 스크롤 종료
                EnqueueVisibleStrict();
                UpdateSchedulerViewportInternal();
                _service.SetApplySuspended(false);
                _service.FlushApplyQueue();
            }
            _ = ProcessQueueAsync();

            // Idle 타이머 시작 - IsIntermediate와 관계없이 스크롤 이벤트가 멈추면 idle 처리
            _scrollIdleCts?.Cancel();
            _scrollIdleCts = new CancellationTokenSource();
            var ct = _scrollIdleCts.Token;
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(60, ct); } catch { return; } // 60ms로 단축
                if (ct.IsCancellationRequested) return;
                
                // 추가로 스크롤 이벤트가 없었는지 확인
                long currentTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                if (currentTicks - _lastScrollEventTicks < ScrollIdleThresholdTicks - TimeSpan.TicksPerMillisecond * 30)
                    return; // 아직 스크롤 중
                
                DispatcherQueue?.TryEnqueue(() =>
                {
                    _service.SetApplySuspended(false);
                    _service.FlushApplyQueue();
                    StartIdleFill();
                });
            });

            _lastScrollOffset = current;
        };
    }

    private void TryDrainVisibleDuringScroll()
    {
        try
        {
            if (ViewModel.Images.Count == 0) return;
            var set = new HashSet<ImageMetadata>();
            int count = Math.Min(_viewEndIndex - _viewStartIndex + 1, 50); // 50개로 증가
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
                        int maxCheck = Math.Min(host.Children.Count, 150); // 150으로 증가
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
                int maxVisible = 80; // 80으로 증가
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
                    
                    int maxBuffer = 150; // 150으로 증가
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
                    int maxRemaining = 300; // 300으로 증가
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
                        // Schedule in larger batches for faster prefetch
                        int batchSize = Math.Min(snap.BufferCandidates.Count, 64); // 64로 증가
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
                            int batch = Math.Min(64, snap.RemainingOrdered.Count - cursor); // 64로 증가
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

                // 더 공격적인 idle fill 간격
                int delay = idleCyclesWithoutWork switch
                {
                    < 2 => 30,   // 더 짧게
                    < 5 => 60,
                    < 15 => 120,
                    _ => 250
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

            int visibleCount = Math.Min(endIndex - startIndex + 1, 150); // 150으로 증가
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
            
            int maxBuffer = 200; // 200으로 증가
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
