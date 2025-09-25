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
                ResetQueueForScroll();
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
                if (_scrollViewer == null || ViewModel.Images.Count == 0)
                { tcs.TrySetResult(new IdleUiSnapshot{ HasImages = false }); return; }

                var snap = new IdleUiSnapshot{ HasImages = true };
                snap.DesiredWidth = GetDesiredDecodeWidth();

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

                // Visible logical range
                if (_viewStartIndex <= _viewEndIndex && _viewStartIndex >= 0 && _viewEndIndex < ViewModel.Images.Count)
                {
                    for (int i = _viewStartIndex; i <= _viewEndIndex && i < ViewModel.Images.Count; i++)
                    {
                        var m = ViewModel.Images[i];
                        int cur = m.ThumbnailPixelWidth ?? 0;
                        if (m.Thumbnail == null || cur + 32 < snap.DesiredWidth)
                            snap.MissingVisible.Add(m);
                    }
                }

                // Buffer prefetch indices (direction-biased)
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
                    int bufStartIndex = Math.Max(0, bufStartRow * cols);
                    int bufEndIndex = Math.Min((bufEndRow + 1) * cols - 1, ViewModel.Images.Count - 1);
                    for (int i = bufStartIndex; i <= bufEndIndex; i++)
                    {
                        if (i >= _viewStartIndex && i <= _viewEndIndex) continue;
                        var m = ViewModel.Images[i];
                        int cur = m.ThumbnailPixelWidth ?? 0;
                        if (m.Thumbnail == null || cur + 32 < snap.DesiredWidth)
                            snap.BufferCandidates.Add(m);
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
                        // Prefetch (normal priority)
                        foreach (var m in snap.BufferCandidates)
                            (_service as ImageIndexService)?.Schedule(m, desiredWidth, false);
                        work = true;
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

    private void ResetQueueForScroll() { }

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
            var orderedVisible = new List<ImageMetadata>(endIndex - startIndex + 1);
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
        }
        catch { }
    }
}
