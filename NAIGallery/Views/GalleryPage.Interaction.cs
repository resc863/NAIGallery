using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NAIGallery.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media.Animation; // ensure animation types

namespace NAIGallery.Views;

public sealed partial class GalleryPage
{
    private void ApplyItemSize()
    {
        TileLineHeight = _baseItemSize;
        MinItemWidth = _baseItemSize; // ensure columns resize
        try { GalleryView?.InvalidateMeasure(); GalleryView?.InvalidateArrange(); } catch { }
    }

    private void SuppressImplicitBriefly(int ms = 250)
    {
        try { _postZoomSuppressCts?.Cancel(); } catch { }
        _postZoomSuppressCts = new CancellationTokenSource(); var ct = _postZoomSuppressCts.Token;
        _isScrollBubbling = true;
        _ = Task.Run(async () => { try { await Task.Delay(ms, ct); } catch { } if (!ct.IsCancellationRequested) DispatcherQueue.TryEnqueue(() => _isScrollBubbling = false); });
    }

    private void AdjustScrollForZoom(double oldSize, double newSize, PointerRoutedEventArgs e)
    {
        if (_scrollViewer == null) return;
        var factor = newSize / Math.Max(1.0, oldSize); if (Math.Abs(factor - 1.0) < 0.001) return;
        var p = e.GetCurrentPoint(_scrollViewer).Position; var current = _scrollViewer.VerticalOffset;
        var target = Math.Max(0, (current + p.Y) * factor - p.Y); _ = _scrollViewer.ChangeView(null, target, null, true);
    }

    private void ResetSubtreeOpacity(UIElement root)
    {
        try
        {
            var q = new Queue<DependencyObject>(); q.Enqueue(root);
            while (q.Count > 0)
            {
                var d = q.Dequeue();
                if (d is UIElement el)
                {
                    try
                    {
                        el.Opacity = 1.0;
                        var v = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(el);
                        v.StopAnimation("Opacity"); v.Opacity = 1.0f;
                    }
                    catch { el.Opacity = 1.0; }
                }
                int n = VisualTreeHelper.GetChildrenCount(d);
                for (int i = 0; i < n; i++) q.Enqueue(VisualTreeHelper.GetChild(d, i));
            }
        }
        catch { }
    }

    private UIElement? _hoverScaledTile; // track current hover tile to keep scale when clicking for CA
    private const double HoverScale = 1.06; // subtle emphasis
    private const int HoverAnimMs = 120;
    private bool _navigatingViaTap = false; // suppress hover restore during navigation

    // Called after navigation back (hooked in OnNavigatedTo in another partial)
    private void ResetHoverNavigationState()
    {
        _navigatingViaTap = false;
        _hoverScaledTile = null;
        TryResetAllTileScales();
    }

    private void TryResetAllTileScales()
    {
        try
        {
            if (GalleryView == null) return;
            var q = new Queue<DependencyObject>();
            q.Enqueue(GalleryView);
            int inspected = 0;
            while (q.Count > 0 && inspected < 2000) // safety cap
            {
                inspected++;
                var d = q.Dequeue();
                if (d is FrameworkElement fe)
                {
                    if (fe is Image img && img.Name == "connectedElement" && img.RenderTransform is ScaleTransform st)
                    {
                        if (st.ScaleX != 1.0 || st.ScaleY != 1.0)
                        {
                            st.ScaleX = st.ScaleY = 1.0;
                        }
                    }
                }
                int n = VisualTreeHelper.GetChildrenCount(d);
                for (int i = 0; i < n; i++) q.Enqueue(VisualTreeHelper.GetChild(d, i));
            }
        }
        catch { }
    }

    private void Tile_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        try
        {
            if (_navigatingViaTap) return;
            if (sender is not FrameworkElement fe) return;
            var img = fe.FindName("connectedElement") as Image;
            if (img?.RenderTransform is ScaleTransform st)
            {
                // If already scaled (from previous hover), skip
                if (Math.Abs(st.ScaleX - HoverScale) < 0.01) return;
                _hoverScaledTile = fe; // remember container, used when tapped to start CA from scaled state
                AnimateScale(st, st.ScaleX, HoverScale, HoverAnimMs);
            }
        }
        catch { }
    }

    private void Tile_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        try
        {
            if (_navigatingViaTap) return; // keep scale until nav completes
            if (sender is not FrameworkElement fe) return;
            // If leaving the tile that is not currently being pressed/tapped, restore
            if (_hoverScaledTile == fe)
            {
                var img = fe.FindName("connectedElement") as Image;
                if (img?.RenderTransform is ScaleTransform st)
                {
                    AnimateScale(st, st.ScaleX, 1.0, HoverAnimMs);
                }
                _hoverScaledTile = null;
            }
        }
        catch { }
    }

    private void AnimateScale(ScaleTransform st, double from, double to, int durationMs)
    {
        try
        {
            // Simple imperative animation (avoid storyboard allocation churn) using Composition where possible
            var element = st as DependencyObject;
            // Fallback to storyboard (lightweight for single double)
            var animX = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
                EnableDependentAnimation = true
            };
            var animY = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
                EnableDependentAnimation = true
            };
            var sb = new Storyboard();
            Storyboard.SetTarget(animX, st); Storyboard.SetTargetProperty(animX, "ScaleX");
            Storyboard.SetTarget(animY, st); Storyboard.SetTargetProperty(animY, "ScaleY");
            sb.Children.Add(animX); sb.Children.Add(animY); sb.Begin();
        }
        catch
        {
            st.ScaleX = st.ScaleY = to;
        }
    }

    private void Item_Tapped(object sender, TappedRoutedEventArgs e)
    {
        var now = DateTime.UtcNow;
        if (_isScrollBubbling) { e.Handled = true; return; }
        if ((now - _lastTapAt).TotalMilliseconds < 150) { e.Handled = true; return; }
        _lastTapAt = now;
        if (!TryResolveTap(sender, e, out var itemRoot, out var meta, out var path) || string.IsNullOrEmpty(path)) return;
        e.Handled = true;

        // Ensure the connected element remains at hover scale during CA capture
        try
        {
            if (itemRoot != null)
            {
                var img = itemRoot.FindName("connectedElement") as Image;
                if (img?.RenderTransform is ScaleTransform st)
                {
                    // Keep current scale (likely HoverScale); CA will snapshot this visual
                    st.ScaleX = st.ScaleY = HoverScale; // enforce final to avoid mid-animation capture
                }
            }
        }
        catch { }

        _navigatingViaTap = true; // prevent hover shrink
        TryNavigateWithCA(itemRoot!, path!);
    }

    private void Tile_ImageOpened(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not Image img) return;
            var root = FindAncestorWithDataContext(img) ?? img.Parent as FrameworkElement;
            FrameworkElement? container = null;
            if (root is FrameworkElement fe)
            {
                container = fe.FindName("LoadingText") as FrameworkElement;
                if (container == null) container = FindChildByName(fe, "LoadingText");
            }
            if (container != null) container.Visibility = Visibility.Collapsed;
            if (root?.DataContext is ImageMetadata meta && img.Source is BitmapSource bs)
            {
                double w = bs.PixelWidth, h = bs.PixelHeight;
                if (w > 0 && h > 0)
                {
                    var ar = Math.Clamp(w / h, 0.1, 10.0);
                    if (Math.Abs(ar - meta.AspectRatio) > 0.001) { meta.AspectRatio = ar; RequestReflow(60); }
                }
            }
        }
        catch { }
    }

    private static FrameworkElement? FindAncestorWithDataContext(DependencyObject start)
    {
        try
        {
            DependencyObject? cur = start;
            while (cur != null)
            {
                if (cur is FrameworkElement fe && fe.DataContext is ImageMetadata) return fe;
                cur = VisualTreeHelper.GetParent(cur);
            }
        }
        catch { }
        return null;
    }

    private static string? FindAncestorTagPath(DependencyObject start)
    {
        try
        {
            DependencyObject? cur = start;
            while (cur != null)
            {
                if (cur is FrameworkElement fe && fe.Tag is string s && !string.IsNullOrWhiteSpace(s)) return s;
                cur = VisualTreeHelper.GetParent(cur);
            }
        }
        catch { }
        return null;
    }

    private bool TryResolveTap(object sender, TappedRoutedEventArgs e, out FrameworkElement? itemRoot, out ImageMetadata? meta, out string? path)
    {
        itemRoot = null; meta = null; path = null;
        try
        {
            if (sender is FrameworkElement fe)
            {
                itemRoot = FindAncestorWithDataContext(fe) ?? fe;
                if (itemRoot.DataContext is ImageMetadata im) meta = im;
                if (meta == null)
                {
                    var fromTag = FindAncestorTagPath(fe);
                    if (!string.IsNullOrEmpty(fromTag)) path = fromTag;
                }
            }
            if (meta == null && path == null && e.OriginalSource is DependencyObject d)
            {
                var fe2 = FindAncestorWithDataContext(d);
                if (fe2 != null) { itemRoot = fe2; meta = fe2.DataContext as ImageMetadata; }
                if (meta == null)
                {
                    var p2 = FindAncestorTagPath(d);
                    if (!string.IsNullOrEmpty(p2)) { path = p2; itemRoot ??= fe2 as FrameworkElement; }
                }
            }
            if (meta == null && !string.IsNullOrEmpty(path))
            {
                if (_service.TryGet(path!, out var m) && m != null) meta = m;
            }
            if (path == null && meta != null) path = meta.FilePath;
        }
        catch { }
        return itemRoot != null && (!string.IsNullOrEmpty(path) || meta != null);
    }

    // Helper from original single-file version
    private static FrameworkElement? FindChildByName(DependencyObject parent, string name)
    {
        try
        {
            var q = new Queue<DependencyObject>(); q.Enqueue(parent);
            while (q.Count > 0)
            {
                var d = q.Dequeue();
                if (d is FrameworkElement fe && fe.Name == name) return fe;
                int n = VisualTreeHelper.GetChildrenCount(d);
                for (int i = 0; i < n; i++) q.Enqueue(VisualTreeHelper.GetChild(d, i));
            }
        }
        catch { }
        return null;
    }
}
