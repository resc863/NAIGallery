using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using NAIGallery.Models;
using System;
using System.Threading.Tasks;

namespace NAIGallery.Views;

public sealed partial class GalleryPage
{
    private async Task TryStartBackCAAsync(string path, ConnectedAnimation back)
    {
        if (GalleryView == null) return;
        HideGridForBackCA();
        int index = -1;
        try { for (int i = 0; i < ViewModel.Images.Count; i++) if (string.Equals(ViewModel.Images[i].FilePath, path, StringComparison.OrdinalIgnoreCase)) { index = i; break; } }
        catch { }
        if (index < 0) { ShowGridAfterBackCA(); return; }
        try
        {
            double colWidth = _baseItemSize;
            int cols = Math.Max(1, (int)(GalleryView.ActualWidth / Math.Max(1, colWidth)));
            int row = Math.Max(0, cols > 0 ? index / cols : 0);
            double targetOffset = row * colWidth; _ = _scrollViewer?.ChangeView(null, targetOffset, null, true);
        }
        catch { }
        await Task.Yield();
        var target = FindTargetElementForPath(GalleryView, path);
        if (target != null)
        {
            try { back.Configuration = new DirectConnectedAnimationConfiguration(); back.TryStart(target); } catch { }
            _pendingBackPath = null; ShowGridAfterBackCA(); return;
        }
        ShowGridAfterBackCA();
    }

    private void TryNavigateWithCA(FrameworkElement fe, string path)
    {
        try { Application.Current.Resources["BackPath"] = path; } catch { }
        UIElement? source = fe.FindName("connectedElement") as UIElement ?? fe;
        try
        {
            var cas = ConnectedAnimationService.GetForCurrentView();
            cas.PrepareToAnimate("ForwardConnectedAnimation", source);
            Application.Current.Resources["ForwardCAStarted"] = false;
        }
        catch { }
        StartForwardFadeOutExcluding(source);
        _ = DispatcherQueue.TryEnqueue(() => Frame.Navigate(typeof(ImageDetailPage), path, new SuppressNavigationTransitionInfo()));
    }

    private static FrameworkElement? FindTargetElementForPath(DependencyObject root, string path)
    {
        var q = new System.Collections.Generic.Queue<DependencyObject>(); q.Enqueue(root);
        while (q.Count > 0)
        {
            var d = q.Dequeue();
            if (d is FrameworkElement fe && fe.Tag is string t && string.Equals(t, path, StringComparison.OrdinalIgnoreCase)) return fe;
            int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < count; i++) q.Enqueue(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(d, i));
        }
        return null;
    }

    private void HideGridForBackCA()
    {
        if (_isBackAnimating) return; _isBackAnimating = true; _suppressImplicitDuringBack = true;
        try { _service.SetApplySuspended(true); } catch { }
        if (GalleryView != null) GalleryView.Opacity = 0;
        var tb = GetTopBar(); if (tb != null) tb.Opacity = 0;
    }

    private void ShowGridAfterBackCA()
    {
        _suppressImplicitDuringBack = false; _isBackAnimating = false;
        if (GalleryView != null) GalleryView.Opacity = 1.0;
        var tb = GetTopBar(); if (tb != null) tb.Opacity = 1.0;
        try { _service.SetApplySuspended(false); _service.FlushApplyQueue(); EnqueueVisibleStrict(); UpdateSchedulerViewport(); StartIdleFill(); } catch { }
    }
}
