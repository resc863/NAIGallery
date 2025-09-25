using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using NAIGallery.Models;

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

    private void StartIdleFill()
    {
        try { _idleFillCts?.Cancel(); } catch { }
        _idleFillCts = new CancellationTokenSource();
        var token = _idleFillCts.Token;
        int passes = 0;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested && passes < 5)
            {
                bool any = false;
                try
                {
                    if (_scrollViewer == null || ViewModel.Images.Count == 0) break;
                    int desiredWidth = GetDesiredDecodeWidth();
                    var missingVisible = new List<ImageMetadata>();
                    for (int i = _viewStartIndex; i <= Math.Min(_viewEndIndex, ViewModel.Images.Count - 1); i++)
                    {
                        var m = ViewModel.Images[i];
                        int cur = m.ThumbnailPixelWidth ?? 0;
                        if (m.Thumbnail == null || cur + 32 < desiredWidth)
                            missingVisible.Add(m);
                    }
                    if (missingVisible.Count > 0)
                    {
                        _thumbScheduler.BoostVisible(missingVisible, desiredWidth);
                        any = true;
                    }

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
                        if (m.Thumbnail == null || cur + 32 < desiredWidth)
                        {
                            _thumbScheduler.Request(m, desiredWidth, highPriority: false);
                            any = true;
                        }
                    }
                }
                catch { }

                if (!any) break;
                passes++;
                try { await Task.Delay(140, token); } catch { break; }
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
            int mid = (startIndex + endIndex) / 2; int lo = mid; int hi = mid + 1;
            while (lo >= startIndex || hi <= endIndex)
            {
                if (lo >= startIndex) { orderedVisible.Add(ViewModel.Images[lo]); lo--; }
                if (hi <= endIndex) { orderedVisible.Add(ViewModel.Images[hi]); hi++; }
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
            _thumbScheduler.UpdateViewport(orderedVisible, bufferList, desiredWidth);
        }
        catch { }
    }
}
