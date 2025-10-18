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

    private void HookScroll()
    {
        try { _scrollViewer = RepeaterScroll; } catch { _scrollViewer = null; }
        if (_scrollViewer == null) return;
        _lastScrollOffset = _scrollViewer.VerticalOffset;
        _lastOffsetForDir = _lastScrollOffset;
        _scrollViewer.ViewChanged += (s, args) =>
        {
            if (!_isLoaded) return;
            double current = _scrollViewer.VerticalOffset;
            _scrollingUp = current < _lastOffsetForDir - 0.5; // small hysteresis
            _lastOffsetForDir = current;
            bool intermediate = args.IsIntermediate || Math.Abs(current - _lastScrollOffset) > 0.5;
            _isScrollBubbling = intermediate;

            if (intermediate)
            {
                _service.SetApplySuspended(true);
                _scrollIdleCts?.Cancel();
                _scrollIdleCts = new CancellationTokenSource();
                _idleFillCts?.Cancel();
                EnqueueVisibleStrict();
                // Keep viewport scheduling updated during active scroll so upward blanks fill sooner.
                UpdateSchedulerViewport();
                TryDrainVisibleDuringScroll();
            }
            else
            {
                EnqueueVisibleStrict();
                UpdateSchedulerViewport();
            }
            _ = ProcessQueueAsync();

            if (!intermediate)
            {
                _scrollIdleCts?.Cancel();
                _scrollIdleCts = new CancellationTokenSource();
                var ct = _scrollIdleCts.Token;
                _ = Task.Run(async () =>
                {
                    try { await Task.Delay(90, ct); } catch { return; }
                    if (ct.IsCancellationRequested) return;
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        _service.SetApplySuspended(false);
                        _service.FlushApplyQueue();
                        StartIdleFill();
                    });
                });
            }

            _lastScrollOffset = current;
        };
    }

    private void TryDrainVisibleDuringScroll()
    {
        try
        {
            if (ViewModel.Images.Count == 0) return;
            var set = new HashSet<ImageMetadata>();
            for (int i = _viewStartIndex; i <= _viewEndIndex && i < ViewModel.Images.Count; i++)
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
                var images = ViewModel.Images; // local snapshot reference
                int imageCount = images.Count;
                if (_scrollViewer == null || imageCount == 0)
                { tcs.TrySetResult(new IdleUiSnapshot { HasImages = false }); return; }

                var snap = new IdleUiSnapshot { HasImages = true, DesiredWidth = GetDesiredDecodeWidth() };

                // Realized tiles
                try
                {
                    var host = GetItemsHost();
                    if (host != null && host.Children.Count > 0)
                    {
                        for (int c = 0; c < host.Children.Count; c++)
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

                // Visible logical range
                if (viewStart <= viewEnd && viewStart >= 0 && viewEnd < imageCount)
                {
                    for (int i = viewStart; i <= viewEnd; i++)
                    {
                        if (i < 0 || i >= imageCount) break;
                        var m = images[i];
                        int cur = m.ThumbnailPixelWidth ?? 0;
                        if (m.Thumbnail == null || cur + 32 < snap.DesiredWidth)
                            snap.MissingVisible.Add(m);
                    }
                }

                // Buffer prefetch indices (direction-biased)
                int bufStartIndex = 0, bufEndIndex = -1; // capture for remaining ordering
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
                    if (bufStartIndex < imageCount && bufEndIndex >= 0)
                    {
                        for (int i = bufStartIndex; i <= bufEndIndex; i++)
                        {
                            if (i >= imageCount) break;
                            if (i >= viewStart && i <= viewEnd) continue;
                            if (i < 0) continue;
                            var m = images[i];
                            int cur = m.ThumbnailPixelWidth ?? 0;
                            if (m.Thumbnail == null || cur + 32 < snap.DesiredWidth)
                                snap.BufferCandidates.Add(m);
                        }
                    }
                }
                catch { }

                // Remaining outside buffer (progressive backfill radiating outwards from viewport)
                try
                {
                    int total = imageCount;
                    if (total > 0 && viewEnd >= viewStart && viewStart >= 0)
                    {
                        int left = viewStart - 1;
                        int right = viewEnd + 1;
                        while (left >= 0 || right < total)
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
                            if (right < total && (right < bufStartIndex || right > bufEndIndex))
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
                if (snap == null || !snap.HasImages) { try { await Task.Delay(400, token); } catch { } continue; }

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
                        foreach (var m in snap.BufferCandidates)
                            (_service as ImageIndexService)?.Schedule(m, desiredWidth, false);
                        work = true;
                    }
                    // Progressive backfill: schedule remaining (chunked) so eventually everything gets a thumbnail
                    if (!work && snap.RemainingOrdered.Count > 0)
                    {
                        int cursor = _idleBackfillCursor;
                        if (cursor < snap.RemainingOrdered.Count)
                        {
                            int batch = Math.Min(48, snap.RemainingOrdered.Count - cursor);
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

                int delay = idleCyclesWithoutWork switch
                {
                    < 2 => 140,
                    < 5 => 250,
                    < 15 => 500,
                    _ => 1000
                };
                try { await Task.Delay(delay, token); } catch { break; }
            }
        }, token);
    }

    private void RequestReflow(int delayMs = 80)
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
                    UpdateSchedulerViewport();
                    StartIdleFill();
                }
                catch { }
            });
        });
    }

    private void UpdateSchedulerViewport()
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

            // Guard: if math overshoots (e.g. offset near extent end) startIndex can exceed endIndex -> negative capacity
            if (startIndex >= ViewModel.Images.Count || endIndex < startIndex)
            {
                return; // nothing visible / safe early exit
            }

            int visibleCount = endIndex - startIndex + 1;
            var orderedVisible = new List<ImageMetadata>(visibleCount);
            if (_scrollingUp)
            {
                // When scrolling up, prioritize from top to bottom to fill blanks at top quickly.
                for (int i = startIndex; i <= endIndex; i++) orderedVisible.Add(ViewModel.Images[i]);
            }
            else
            {
                int mid = (startIndex + endIndex) / 2; int lo = mid; int hi = mid + 1;
                while (lo >= startIndex || hi <= endIndex)
                {
                    if (lo >= startIndex) { orderedVisible.Add(ViewModel.Images[lo]); lo--; }
                    if (hi <= endIndex) { orderedVisible.Add(ViewModel.Images[hi]); hi++; }
                }
            }

            double bufferTop = Math.Max(0, offset - bufferTopExtent);
            double bufferBottom = offset + viewport + bufferBottomExtent;
            int bufStartRow = Math.Max(0, (int)(bufferTop / itemH));
            int bufEndRow = Math.Max(bufStartRow, (int)(bufferBottom / itemH));
            int bufStartIndex = Math.Max(0, bufStartRow * cols);
            int bufEndIndex = Math.Min((bufEndRow + 1) * cols - 1, ViewModel.Images.Count - 1);
            var bufferList = new List<ImageMetadata>();
            for (int i = bufStartIndex; i <= bufEndIndex; i++)
            {
                if (i >= startIndex && i <= endIndex) continue;
                bufferList.Add(ViewModel.Images[i]);
            }

            int desiredWidth = GetDesiredDecodeWidth();
            (_service as ImageIndexService)?.UpdateViewport(orderedVisible, bufferList, desiredWidth);
            // Reset backfill cursor when viewport changes significantly
            _idleBackfillCursor = 0;
        }
        catch { }
    }
}
