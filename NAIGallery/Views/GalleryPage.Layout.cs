using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NAIGallery.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation; // Point

namespace NAIGallery.Views;

public sealed partial class GalleryPage
{
    private Panel? GetItemsHost()
    {
        if (_itemsHost != null) return _itemsHost;
        try
        {
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

    private bool EnqueueFromRealized(bool highPriority = true, double extraBuffer = 64)
    {
        if (ViewModel.Images.Count == 0) return false;
        var host = GetItemsHost();
        if (host == null || host.Children.Count == 0) return false;

        double top, bottom;
        if (_scrollViewer != null)
        {
            top = _scrollViewer.VerticalOffset - extraBuffer; if (top < 0) top = 0;
            bottom = _scrollViewer.VerticalOffset + _scrollViewer.ViewportHeight + extraBuffer;
        }
        else
        {
            top = 0;
            bottom = RepeaterScroll?.ActualHeight > 0 ? RepeaterScroll.ActualHeight + extraBuffer : ActualHeight + extraBuffer;
        }

        int minIdx = int.MaxValue; int maxIdx = -1; int desiredWidth = GetDesiredDecodeWidth(); int enqueued = 0;
        Dictionary<ImageMetadata,int>? indexMap = null;
        try { indexMap = new Dictionary<ImageMetadata,int>(ViewModel.Images.Count); for (int i = 0; i < ViewModel.Images.Count; i++) indexMap[ViewModel.Images[i]] = i; } catch { indexMap = null; }

        for (int c = 0; c < host.Children.Count; c++)
        {
            if (host.Children[c] is not FrameworkElement fe) continue;
            if (fe.DataContext is not ImageMetadata meta) continue;
            double y; double h;
            try { var t = fe.TransformToVisual(GalleryView); var p = t.TransformPoint(new Point(0,0)); y = p.Y; h = fe.ActualHeight > 0 ? fe.ActualHeight : TileLineHeight; } catch { continue; }
            if (y > bottom || (y + h) < top) continue;
            int idx = (indexMap != null && indexMap.TryGetValue(meta, out var mapped)) ? mapped : ViewModel.Images.IndexOf(meta);
            if (idx < 0) continue;
            if (idx < minIdx) minIdx = idx; if (idx > maxIdx) maxIdx = idx;
            var cur = meta.ThumbnailPixelWidth ?? 0;
            if (meta.Thumbnail == null || cur + 32 < desiredWidth)
            {
                EnqueueMeta(meta, desiredWidth, highPriority: highPriority);
                enqueued++;
            }
        }
        if (minIdx <= maxIdx) { _viewStartIndex = minIdx; _viewEndIndex = maxIdx; }
        return enqueued > 0;
    }

    private void EnqueueVisibleStrict()
    {
        if (EnqueueFromRealized(highPriority: true)) return;
        if (ViewModel.Images.Count == 0 || _scrollViewer == null) return;
        int cols = Math.Max(1, (int)((GalleryView?.ActualWidth > 0 ? GalleryView.ActualWidth : ActualWidth) / Math.Max(1, _baseItemSize)));
        double itemH = _baseItemSize;
        double offset = _scrollViewer.VerticalOffset;
        double viewport = _scrollViewer.ViewportHeight;
        int startRow = Math.Max(0, (int)(offset / itemH));
        int endRow = Math.Max(startRow, (int)((offset + viewport) / itemH));
        _viewStartIndex = Math.Max(0, startRow * cols);
        _viewEndIndex = Math.Min((endRow + 1) * cols - 1, Math.Max(0, ViewModel.Images.Count - 1));
        int desiredWidth = GetDesiredDecodeWidth();
        for (int i = _viewStartIndex; i <= _viewEndIndex; i++)
        {
            var m = ViewModel.Images[i]; var cur = m.ThumbnailPixelWidth ?? 0; if (m.Thumbnail == null || cur + 32 < desiredWidth) EnqueueMeta(m, desiredWidth, highPriority: true);
        }
    }

    private void ForceEnqueueVisibleGapsStrict()
    {
        if (EnqueueFromRealized(highPriority: true)) return;
        if (ViewModel.Images.Count == 0) return; int desiredWidth = GetDesiredDecodeWidth();
        for (int i = _viewStartIndex; i <= Math.Min(_viewEndIndex, ViewModel.Images.Count - 1); i++)
        { var m = ViewModel.Images[i]; if (m.Thumbnail == null) EnqueueMeta(m, desiredWidth, highPriority: true); }
    }

    private void EnqueueVisibleRange(bool centerOut = false, bool preferBottom = false)
    {
        if (EnqueueFromRealized(highPriority: true)) return;
        if (ViewModel.Images.Count == 0 || _scrollViewer == null) return;
        int cols = Math.Max(1, (int)((GalleryView?.ActualWidth > 0 ? GalleryView.ActualWidth : ActualWidth) / Math.Max(1, _baseItemSize)));
        double itemH = _baseItemSize;
        double offset = _scrollViewer.VerticalOffset;
        double viewport = _scrollViewer.ViewportHeight;
        int startRow = Math.Max(0, (int)(offset / itemH));
        int endRow = Math.Max(startRow, (int)((offset + viewport) / itemH));
        int buf = centerOut ? 1 : 2; startRow = Math.Max(0, startRow - buf); endRow = endRow + buf;
        _viewStartIndex = Math.Max(0, startRow * cols);
        _viewEndIndex = Math.Min((endRow + 1) * cols - 1, Math.Max(0, ViewModel.Images.Count - 1));
        int desiredWidth = GetDesiredDecodeWidth();
        for (int i = _viewStartIndex; i <= _viewEndIndex; i++)
        { var m = ViewModel.Images[i]; var cur = m.ThumbnailPixelWidth ?? 0; if (m.Thumbnail == null || cur + 32 < desiredWidth) EnqueueMeta(m, desiredWidth, highPriority: true); }
    }
}
