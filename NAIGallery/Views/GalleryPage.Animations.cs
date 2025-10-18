using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using System;
using System.Numerics;
using System.Threading.Tasks;

namespace NAIGallery.Views;

public sealed partial class GalleryPage
{
    private void InitComposition()
    {
        try
        {
            _compositor ??= ElementCompositionPreview.GetElementVisual(this).Compositor;
            if (_compositor == null) return;
            var offsetAnim = _compositor.CreateVector3KeyFrameAnimation();
            offsetAnim.Duration = TimeSpan.FromMilliseconds(160);
            offsetAnim.Target = "Offset";
            offsetAnim.InsertExpressionKeyFrame(1.0f, "this.FinalValue", _compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0f), new Vector2(0f, 1f)));
            var group = _compositor.CreateAnimationGroup();
            group.Add(offsetAnim);
            _implicitOffsetAnimations = _compositor.CreateImplicitAnimationCollection();
            _implicitOffsetAnimations["Offset"] = group;
        }
        catch { }
    }

    private void StartForwardFadeOutExcluding(UIElement? source)
    {
        // New minimal implementation: fade ONLY the source tile out quickly to avoid double-visual flash
        if (_isForwardFading) return; _isForwardFading = true;
        try
        {
            if (source == null) return;
            _compositor ??= ElementCompositionPreview.GetElementVisual(this).Compositor;
            if (_compositor == null)
            {
                source.Opacity = 0; return;
            }
            var visual = ElementCompositionPreview.GetElementVisual(source);
            var anim = _compositor.CreateScalarKeyFrameAnimation();
            anim.Duration = TimeSpan.FromMilliseconds(120); // 늘린 시간
            anim.InsertKeyFrame(1f, 0f, _compositor.CreateCubicBezierEasingFunction(new Vector2(0.25f, 0.1f), new Vector2(0.25f, 1f))); // Easing 추가
            anim.Target = "Opacity";
            visual.StartAnimation("Opacity", anim);
            source.Opacity = 0; // ensure final state
        }
        catch { }
    }

    private void AnimateZoomTiles(double oldSize, double newSize, Windows.Foundation.Point pointerPosOnPage)
    {
        try
        {
            if (GalleryView == null) return;
            _compositor ??= ElementCompositionPreview.GetElementVisual(this).Compositor;
            if (_compositor == null) return;
            var rootVisual = ElementCompositionPreview.GetElementVisual(GalleryView);
            float fromScale = (float)(oldSize / Math.Max(1.0, newSize)); if (Math.Abs(fromScale - 1f) < 0.01f) return;
            Windows.Foundation.Point center;
            try { var t = this.TransformToVisual(GalleryView); center = t.TransformPoint(pointerPosOnPage); } catch { center = new Windows.Foundation.Point(GalleryView.ActualWidth/2, GalleryView.ActualHeight/2); }
            var easing = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f,0f), new Vector2(0f,1f));
            var anim = _compositor.CreateVector3KeyFrameAnimation(); anim.Target = "Scale"; anim.Duration = TimeSpan.FromMilliseconds(180); anim.InsertKeyFrame(1f, new Vector3(1f,1f,1f), easing);
            rootVisual.CenterPoint = new Vector3((float)center.X,(float)center.Y,0f); rootVisual.Scale = new Vector3(fromScale, fromScale,1f); rootVisual.StartAnimation("Scale", anim);
        }
        catch { }
    }
}
