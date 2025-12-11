using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NAIGallery.Models;
using NAIGallery.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Windows.Foundation; // Point

namespace NAIGallery.Views;

public sealed partial class GalleryPage
{
    private Panel? GetItemsHost()
    {
        if (_itemsHost != null) return _itemsHost;
        try
        {
            // Ensure we only walk the visual tree on the UI thread to avoid COMException
            if (DispatcherQueue?.HasThreadAccess != true)
            {
                return _itemsHost; // return cached (likely null) and try again later on UI thread
            }
            if (GalleryView == null) return null;
            int count = VisualTreeHelper.GetChildrenCount(GalleryView);
            for (int i = 0; i < count; i++)
            {
                if (VisualTreeHelper.GetChild(GalleryView, i) is Panel p) { _itemsHost = p; break; }
            }
        }
        catch { }
        return _itemsHost;
    }

    private void EnqueueVisibleStrict()
    {
        bool realized = EnqueueFromRealized(highPriority: true, extraBuffer: 0);
        if (!realized)
        {
            realized = EnqueueFromRealized(highPriority: true, extraBuffer: 48);
        }

        // Safeguard: if realized children unusually low compared to expected rows, request reflow.
        try
        {
            var host = GetItemsHost();
            if (host != null && _scrollViewer != null)
            {
                int cols = Math.Max(1, (int)((GalleryView?.ActualWidth > 0 ? GalleryView.ActualWidth : ActualWidth) / Math.Max(1, _baseItemSize)));
                double itemH = _baseItemSize;
                double viewport = _scrollViewer.ViewportHeight;
                int expectedRows = Math.Max(1, (int)Math.Ceiling(viewport / Math.Max(1, itemH)) + 1);
                int expectedMinChildren = Math.Min(ViewModel.Images.Count, expectedRows * cols / 2);
                if (host.Children.Count < expectedMinChildren)
                {
                    Debug.WriteLine($"[Layout] Sparse realized children ({host.Children.Count} < {expectedMinChildren}), forcing reflow");
                    GalleryView?.InvalidateMeasure();
                    GalleryView?.InvalidateArrange();
                }
            }
        }
        catch { }

        if (realized) return;

        if (ViewModel.Images.Count == 0 || _scrollViewer == null) return;

        // Compute full logical visible range strictly from scroll metrics
        int cols2 = Math.Max(1, (int)((GalleryView?.ActualWidth > 0 ? GalleryView.ActualWidth : ActualWidth) / Math.Max(1, _baseItemSize)));
        double itemH2 = _baseItemSize;
        double offset2 = _scrollViewer.VerticalOffset;
        double viewport2 = _scrollViewer.ViewportHeight;
        int startRow2 = Math.Max(0, (int)(offset2 / itemH2));
        int endRow2 = Math.Max(startRow2, (int)((offset2 + viewport2) / itemH2));
        _viewStartIndex = Math.Max(0, startRow2 * cols2);
        _viewEndIndex = Math.Min((endRow2 + 1) * cols2 - 1, Math.Max(0, ViewModel.Images.Count - 1));
        int desiredWidth = GetDesiredDecodeWidth();
        // When scrolling up, we want to prioritize from top to bottom so blanks at top fill immediately.
        if (_scrollingUp)
        {
            for (int i = _viewStartIndex; i <= _viewEndIndex; i++)
            {
                var m = ViewModel.Images[i]; var cur = m.ThumbnailPixelWidth ?? 0; if (m.Thumbnail == null || cur + 32 < desiredWidth) EnqueueMeta(m, desiredWidth, highPriority: true);
            }
        }
        else
        {
            // Center-out order for downward / neutral scroll
            int start = _viewStartIndex; int end = _viewEndIndex; int mid = (start + end) / 2;
            int lo = mid, hi = mid + 1;
            while (lo >= start || hi <= end)
            {
                if (lo >= start)
                { var m = ViewModel.Images[lo]; var cur = m.ThumbnailPixelWidth ?? 0; if (m.Thumbnail == null || cur + 32 < desiredWidth) EnqueueMeta(m, desiredWidth, highPriority: true); lo--; }
                if (hi <= end)
                { var m = ViewModel.Images[hi]; var cur = m.ThumbnailPixelWidth ?? 0; if (m.Thumbnail == null || cur + 32 < desiredWidth) EnqueueMeta(m, desiredWidth, highPriority: true); hi++; }
            }
        }
        BoostCurrentVisible(desiredWidth);
    }

    private void BoostCurrentVisible(int desiredWidth)
    {
        try
        {
            if (ViewModel.Images.Count == 0) return;
            if (_viewEndIndex < _viewStartIndex) return;
            var slice = new List<ImageMetadata>();
            for (int i = _viewStartIndex; i <= Math.Min(_viewEndIndex, ViewModel.Images.Count - 1); i++)
            {
                var m = ViewModel.Images[i];
                if ((m.ThumbnailPixelWidth ?? 0) < desiredWidth) slice.Add(m);
            }
            if (slice.Count > 0) (_service as ImageIndexService)?.BoostVisible(slice, desiredWidth);
        }
        catch { }
    }

    private bool EnqueueFromRealized(bool highPriority = true, double extraBuffer = 64)
    {
        bool result = InternalEnqueueFromRealized(highPriority, extraBuffer);
        if (result) BoostCurrentVisible(GetDesiredDecodeWidth());
        return result;
    }

    private bool InternalEnqueueFromRealized(bool highPriority, double extraBuffer)
    {
        if (ViewModel.Images.Count == 0) return false;
        var host = GetItemsHost();
        if (host == null || host.Children.Count == 0) return false;

        double viewportHeight = _scrollViewer?.ViewportHeight > 0 ? _scrollViewer!.ViewportHeight : (RepeaterScroll?.ActualHeight > 0 ? RepeaterScroll.ActualHeight : ActualHeight);
        double centerLine = _scrollingUp ? viewportHeight * 0.35 : viewportHeight * 0.5;

        double top, bottom;
        if (_scrollViewer != null)
        {
            top = -extraBuffer;
            bottom = viewportHeight + extraBuffer;
        }
        else
        {
            top = 0;
            bottom = viewportHeight + extraBuffer;
        }

        int minIdx = int.MaxValue; int maxIdx = -1; int desiredWidth = GetDesiredDecodeWidth();
        Dictionary<ImageMetadata,int>? indexMap = null;
        try { indexMap = new Dictionary<ImageMetadata,int>(ViewModel.Images.Count); for (int i = 0; i < ViewModel.Images.Count; i++) indexMap[ViewModel.Images[i]] = i; } catch { indexMap = null; }

        var candidates = new List<(ImageMetadata meta, int idx, double dist, int curWidth, double y)>();
        for (int c = 0; c < host.Children.Count; c++)
        {
            if (host.Children[c] is not FrameworkElement fe) continue;
            if (fe.DataContext is not ImageMetadata meta) continue;
            double y; double h;
            try
            {
                if (_scrollViewer != null)
                {
                    var t = fe.TransformToVisual(_scrollViewer);
                    var p = t.TransformPoint(new Point(0,0));
                    y = p.Y;
                }
                else
                {
                    var t = fe.TransformToVisual(GalleryView);
                    var p = t.TransformPoint(new Point(0,0));
                    y = p.Y;
                }
                h = fe.ActualHeight > 0 ? fe.ActualHeight : TileLineHeight;
            }
            catch { continue; }
            if (y > bottom || (y + h) < top) continue;
            int idx = (indexMap != null && indexMap.TryGetValue(meta, out var mapped)) ? mapped : ViewModel.Images.IndexOf(meta);
            if (idx < 0) continue;
            if (idx < minIdx) minIdx = idx; if (idx > maxIdx) maxIdx = idx;
            double tileCenter = y + h / 2.0;
            double dist = Math.Abs(tileCenter - centerLine);
            candidates.Add((meta, idx, dist, meta.ThumbnailPixelWidth ?? 0, y));
        }

        if (candidates.Count == 0) return false;
        if (_scrollingUp)
        {
            // Strongly prefer items above the visual center to fill blanks on upward scroll.
            for (int i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].y < centerLine)
                {
                    var ctuple = candidates[i];
                    ctuple.dist *= 0.5; // stronger bias than before
                    candidates[i] = ctuple;
                }
            }
        }

        foreach (var entry in candidates.OrderBy(c => c.dist).ThenBy(c => c.idx))
        {
            var (meta, _, _, cur, _) = entry;
            if (meta.Thumbnail == null || cur + 32 < desiredWidth)
            {
                EnqueueMeta(meta, desiredWidth, highPriority: highPriority);
            }
        }

        if (minIdx <= maxIdx) { _viewStartIndex = minIdx; _viewEndIndex = maxIdx; }
        return true;
    }
}
