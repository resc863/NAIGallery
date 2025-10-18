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
        // No longer hide grid; we only run CA if possible
        int index = -1;
        try { for (int i = 0; i < ViewModel.Images.Count; i++) if (string.Equals(ViewModel.Images[i].FilePath, path, StringComparison.OrdinalIgnoreCase)) { index = i; break; } }
        catch { }
        if (index < 0) { return; }
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
            _pendingBackPath = null; return;
        }
    }

    private void TryNavigateWithCA(FrameworkElement fe, string path)
    {
        try { Application.Current.Resources["BackPath"] = path; } catch { }
        UIElement? source = fe.FindName("connectedElement") as UIElement ?? fe;
        try
        {
            var cas = ConnectedAnimationService.GetForCurrentView();
            cas.PrepareToAnimate("ForwardConnectedAnimation", source);
            // Configuration을 Gravity로 변경 (더 자연스러운 곡선)
            var anim = cas.GetAnimation("ForwardConnectedAnimation");
            if (anim != null) anim.Configuration = new GravityConnectedAnimationConfiguration();
            Application.Current.Resources["ForwardCAStarted"] = false;
        }
        catch { }
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

    // Legacy methods kept for compatibility but now no-op
    private void HideGridForBackCA() { }
    private void ShowGridAfterBackCA() { }
}
