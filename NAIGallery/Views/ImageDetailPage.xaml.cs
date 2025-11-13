using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using NAIGallery.Services;
using System;
using System.Linq;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Controls.Primitives;
using System.IO;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using NAIGallery.Models;
using NAIGallery.ViewModels; // added
using System.Collections.Generic; // added
using Microsoft.UI.Xaml.Media.Animation;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics; // debug

namespace NAIGallery.Views;

public sealed partial class ImageDetailPage : Page
{
    private readonly IImageIndexService _service;
    private double _currentScale = 1.0;
    private string? _pendingPath;
    private bool _initializedZoom = false;
    private bool _isPanning = false;
    private Point _panStartPoint;
    private double _startTranslateX;
    private double _startTranslateY;
    private bool _isForwardConnectedAnimating = false;
    private bool _forwardStarted = false;
    private bool _pendingForwardCA = false;
    private bool _imageOpened = false;
    private int _loadSeq = 0;
    private string? _currentPath;
    private RectangleGeometry? _savedHostClip = null;
    private bool _resizing = false;
    private double _initialMetaWidth;
    private double _initialPointerX;
    private const double MetaMinWidth = 200;
    private const double MetaMaxWidth = 1000;
    private readonly Brush _splitterBaseBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(4,0,0,0));
    private readonly Brush _splitterHoverBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(40,0,0,0));
    private CancellationTokenSource? _thumbCts;

    public ImageDetailPage()
    {
        InitializeComponent();
        _service = ((App)Application.Current).Services.GetService(typeof(IImageIndexService)) as IImageIndexService
                    ?? ((App)Application.Current).Services.GetService(typeof(ImageIndexService)) as IImageIndexService
                    ?? new ImageIndexService();
        PointerWheelChanged += ImageDetailPage_PointerWheelChanged;
        SizeChanged += ImageDetailPage_SizeChanged;
        Loaded += ImageDetailPage_Loaded;
        Unloaded += ImageDetailPage_Unloaded;
    }

    private void ImageDetailPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _forwardStarted = false;
        _isForwardConnectedAnimating = false;
        try { _thumbCts?.Cancel(); _thumbCts?.Dispose(); } catch { }
        _thumbCts = null;
    }

    private void ImageDetailPage_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateClip();

        // Hide non-image UI until CA completes
        try { if (TopBar != null) TopBar.Opacity = 0; } catch { }
        try { if (MetaScroll != null) MetaScroll.Opacity = 0; } catch { }
        try { if (SplitterBorder != null) SplitterBorder.Opacity = 0; } catch { }
        try { if (DetailImage != null) DetailImage.Opacity = 0; } catch { } // 이미지 숨김

        // If navigation parameter was deferred, load now
        if (_pendingPath != null) { var p = _pendingPath; _pendingPath = null; LoadImage(p); }

        // Request forward CA; it will start only after ImageOpened
        _pendingForwardCA = true;
        RequestStartForwardConnectedAnimation();

        if (SplitterBorder != null) SplitterBorder.Background = _splitterBaseBrush;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string path)
        {
            if (DetailImage == null) { _pendingPath = path; }
            else { LoadImage(path); }
        }

        // Mark that we want to start CA when possible (after image is opened)
        _pendingForwardCA = true;
        RequestStartForwardConnectedAnimation();
    }

    /// <summary>
    /// Attempts to start the forward connected animation once the image has opened and host layout is stable.
    /// </summary>
    private async void RequestStartForwardConnectedAnimation()
    {
        if (_forwardStarted) { System.Diagnostics.Debug.WriteLine("[Detail][CA] already started"); return; }
        if (!_pendingForwardCA) { System.Diagnostics.Debug.WriteLine("[Detail][CA] not pending"); return; }

        var cas = ConnectedAnimationService.GetForCurrentView();
        var forward = cas.GetAnimation("ForwardConnectedAnimation");
        if (forward == null)
        {
            _isForwardConnectedAnimating = false;
            try { Application.Current.Resources["ForwardCAStarted"] = false; } catch { }
            System.Diagnostics.Debug.WriteLine("[Detail][CA] no forward animation available");
            FadeInAllUI();
            return;
        }

        if (DetailImage == null || !_imageOpened)
        {
            System.Diagnostics.Debug.WriteLine($"[Detail][CA] wait imageOpened={_imageOpened}");
            return;
        }

        int seqAtRequest = _loadSeq;

        try
        {
            await WaitForHostStableAsync();
            if (seqAtRequest != _loadSeq || DetailImage.Source is not BitmapImage)
            {
                try { Application.Current.Resources["ForwardCAStarted"] = false; } catch { }
                System.Diagnostics.Debug.WriteLine("[Detail][CA] sequence changed or no BitmapImage");
                FadeInAllUI();
                return;
            }

            _pendingForwardCA = false;
            _forwardStarted = true;
            _isForwardConnectedAnimating = true;
            System.Diagnostics.Debug.WriteLine("[Detail][CA] TryStart");

            ResetTransforms();
            EnsureInitialZoom();
            CenterImage();

            _savedHostClip = ImageHost?.Clip as RectangleGeometry;
            if (ImageHost != null) ImageHost.Clip = null;

            forward.Configuration = new GravityConnectedAnimationConfiguration(); // Gravity 사용

            var coordinated = new List<UIElement>();
            if (TopBar != null) coordinated.Add(TopBar);
            if (SplitterBorder != null) coordinated.Add(SplitterBorder);
            if (MetaScroll != null) coordinated.Add(MetaScroll);
            foreach (var el in coordinated) { try { el.Opacity = 0.1; } catch { } } // 낮은 opacity로 시작

            bool started = coordinated.Count > 0 ? forward.TryStart(DetailImage, coordinated) : forward.TryStart(DetailImage);
            try { Application.Current.Resources["ForwardCAStarted"] = started; } catch { }
            System.Diagnostics.Debug.WriteLine($"[Detail][CA] started={started}");

            if (started)
            {
                try { if (DetailImage != null) DetailImage.Opacity = 1; } catch { } // CA 시작 시 이미지 표시
            }

            forward.Completed += (s, _) =>
            {
                _isForwardConnectedAnimating = false;
                if (ImageHost != null)
                {
                    try { ImageHost.Clip = _savedHostClip ?? HostClip; } catch { }
                }
                System.Diagnostics.Debug.WriteLine("[Detail][CA] completed");
                FadeInAllUI();
            };

            if (!started)
            {
                System.Diagnostics.Debug.WriteLine("[Detail][CA] start refused");
                FadeInAllUI();
            }
        }
        catch (Exception ex)
        {
            _isForwardConnectedAnimating = false;
            try { Application.Current.Resources["ForwardCAStarted"] = false; } catch { }
            System.Diagnostics.Debug.WriteLine($"[Detail][CA] exception {ex.Message}");
            FadeInAllUI();
        }
    }

    private void FadeInAllUI()
    {
        try { FadeInElement(TopBar, 200); } catch { if (TopBar != null) TopBar.Opacity = 1; }
        try { FadeInElement(MetaScroll, 200); } catch { if (MetaScroll != null) MetaScroll.Opacity = 1; }
        try { FadeInElement(SplitterBorder, 200); } catch { if (SplitterBorder != null) SplitterBorder.Opacity = 1; }
    }

    private void FadeInElement(UIElement? el, double durationMs = 200)
    {
        if (el == null) return;
        try
        {
            if (el.Opacity >= 0.99) return;
            el.Opacity = 0.1; // 낮은 시작 opacity
            var anim = new DoubleAnimation
            {
                To = 1.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, // Easing 추가
                EnableDependentAnimation = true
            };
            var sb = new Storyboard();
            Storyboard.SetTarget(anim, el);
            Storyboard.SetTargetProperty(anim, "Opacity");
            sb.Children.Add(anim);
            sb.Begin();
        }
        catch
        {
            el.Opacity = 1.0;
        }
    }

    /// <summary>
    /// Waits a couple of frames for the host to stabilize in size prior to starting CA or centering.
    /// </summary>
    private async System.Threading.Tasks.Task WaitForHostStableAsync()
    {
        if (ImageHost == null)
        {
            await System.Threading.Tasks.Task.Yield();
            return;
        }
        var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
        int stable = 0;
        double lw = -1, lh = -1;
        void OnRender(object? s, object e)
        {
            double w = ImageHost.ActualWidth;
            double h = ImageHost.ActualHeight;
            if (Math.Abs(w - lw) < 0.5 && Math.Abs(h - lh) < 0.5)
            {
                stable++;
                if (stable >= 2)
                {
                    CompositionTarget.Rendering -= OnRender;
                    tcs.TrySetResult(true);
                }
            }
            else
            {
                stable = 0; lw = w; lh = h;
            }
        }
        CompositionTarget.Rendering += OnRender;
        try { await tcs.Task; }
        finally { CompositionTarget.Rendering -= OnRender; }
    }

    private void ResetTransforms()
    {
        try
        {
            EnsureTransformGroup();
            var (scale, translate) = GetTransforms();
            scale.ScaleX = scale.ScaleY = 1.0;
            translate.X = 0;
            translate.Y = 0;
            try { Canvas.SetLeft(DetailImage, 0); } catch { }
            try { Canvas.SetTop(DetailImage, 0); } catch { }
        }
        catch { }
    }

    protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        base.OnNavigatingFrom(e);
        try
        {
            var cas = ConnectedAnimationService.GetForCurrentView();

            if (e.NavigationMode == NavigationMode.Back)
            {
                // Only prepare back CA when actually navigating back (e.g., to Gallery)
                if (!string.IsNullOrEmpty(_currentPath))
                    Application.Current.Resources["BackPath"] = _currentPath;
                cas.PrepareToAnimate("BackConnectedAnimation", DetailImage);
            }
            else
            {
                // Navigating to a new page (e.g., Settings): cancel any pending animations to avoid lingering visuals
                cas.GetAnimation("ForwardConnectedAnimation")?.Cancel();
                cas.GetAnimation("BackConnectedAnimation")?.Cancel();
            }
        }
        catch { }
    }

    // Handle keyboard navigation
    private void Page_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Left)
        {
            NavigateRelative(-1);
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Right)
        {
            NavigateRelative(1);
            e.Handled = true;
        }
    }

    private IReadOnlyList<ImageMetadata> GetNavigationList()
    {
        try
        {
            if (Application.Current.Resources.TryGetValue("GlobalGalleryVM", out var vmObj) && vmObj is GalleryViewModel vm)
            {
                return vm.Images.ToList();
            }
        }
        catch { }
        // Fallback: deterministic name ordering from All
        return _service.All.OrderBy(m => m.FilePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Navigates to previous/next image relative to current, following current gallery view order.
    /// </summary>
    private void NavigateRelative(int delta)
    {
        try
        {
            var list = GetNavigationList();
            if (DetailImage?.Source is BitmapImage bmp)
            {
                int current = -1;
                string currentPath = bmp.UriSource.LocalPath;
                for (int i = 0; i < list.Count; i++)
                {
                    if (string.Equals(list[i].FilePath, currentPath, StringComparison.OrdinalIgnoreCase)) { current = i; break; }
                }
                if (current >= 0)
                {
                    int next = current + delta;
                    if (next >= 0 && next < list.Count)
                    {
                        LoadImage(list[next].FilePath);
                    }
                }
            }
        }
        catch { }
    }

    private int ComputeDesiredDetailWidth()
    {
        try
        {
            double hostW = ImageHost?.ActualWidth > 0 ? ImageHost.ActualWidth : 1024;
            double scale = XamlRoot?.RasterizationScale ?? 1.0;
            int px = (int)Math.Round(hostW * scale);
            return (int)Math.Clamp(Math.Round(px / 128.0) * 128.0, 256, 4096);
        }
        catch { return 1024; }
    }

    private void LoadImage(string path)
    {
        try
        {
            path = System.IO.Path.GetFullPath(path);
            if (!System.IO.File.Exists(path)) { _ = ShowErrorAsync("이미지 파일을 찾을 수 없습니다:\n" + path); return; }

            _imageOpened = false;
            _initializedZoom = false;
            _currentPath = path;
            unchecked { _loadSeq++; }
            try { Application.Current.Resources["CurrentDetailPath"] = path; } catch { }
            System.Diagnostics.Debug.WriteLine($"[Detail] Load {path}");

            // Cancel any pending thumb priming
            try { _thumbCts?.Cancel(); _thumbCts?.Dispose(); } catch { }
            _thumbCts = new CancellationTokenSource();

            ResetTransforms();
            PopulateMetadata(path);

            // Show a cached thumbnail quickly if available, otherwise prime a small one
            if (_service.TryGet(path, out var meta) && meta != null)
            {
                if (meta.Thumbnail != null)
                {
                    try { DetailImage.Source = meta.Thumbnail; } catch { }
                }
                else
                {
                    int desired = ComputeDesiredDetailWidth();
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _service.EnsureThumbnailAsync(meta, desired, _thumbCts!.Token, allowDownscale: true).ConfigureAwait(false);
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                try { if (DetailImage?.Source is not BitmapImage && meta.Thumbnail != null) DetailImage.Source = meta.Thumbnail; } catch { }
                            });
                        }
                        catch { }
                    });
                }
            }
            else
            {
                Debug.WriteLine($"[Detail][Warn] Metadata not found in service for {path}. IndexedCount={_service.All.Count()}");
            }

            // Now load the full image (hinted to screen width to reduce memory)
            var bmp = new BitmapImage(new Uri(path));
            try { bmp.DecodePixelWidth = ComputeDesiredDetailWidth(); } catch { }
            bmp.ImageOpened += (s, _) =>
            {
                _imageOpened = true;
                System.Diagnostics.Debug.WriteLine("[Detail] ImageOpened");
                RequestStartForwardConnectedAnimation();
                if (_forwardStarted && _isForwardConnectedAnimating) { }
                else { EnsureInitialZoom(); CenterImage(); }
            };
            DetailImage.Source = bmp;
        }
        catch (Exception ex) { _ = ShowErrorAsync("이미지를 여는 중 오류가 발생했습니다:\n" + ex.Message); }
    }

    private void PopulateMetadata(string path)
    {
        try
        {
            if (_service.TryGet(path, out var meta) && meta != null)
            {
                Debug.WriteLine($"[Detail][Meta] File={Path.GetFileName(path)} PromptLen={(meta.Prompt?.Length ?? 0)} BasePrompt={(string.IsNullOrWhiteSpace(meta.BasePrompt)?"-":meta.BasePrompt[..Math.Min(30, meta.BasePrompt.Length)])} Tags={meta.Tags.Count} Params={(meta.Parameters?.Count ?? 0)}");
                // Try to enrich with v4 fields if missing
                _service.RefreshMetadata(meta);

                // Base v4 prompts
                if (!string.IsNullOrWhiteSpace(meta.BasePrompt) || !string.IsNullOrWhiteSpace(meta.BaseNegativePrompt))
                {
                    BasePromptSection.Visibility = Visibility.Visible;
                    BasePromptText.Text = meta.BasePrompt ?? string.Empty;
                    BaseNegativePromptText.Text = meta.BaseNegativePrompt ?? string.Empty;
                }
                else
                {
                    BasePromptSection.Visibility = Visibility.Collapsed;
                }

                // Character prompts
                if (meta.CharacterPrompts != null && meta.CharacterPrompts.Count > 0)
                {
                    CharacterPromptSection.Visibility = Visibility.Visible;
                    CharacterPromptsRepeater.ItemsSource = meta.CharacterPrompts;
                }
                else
                {
                    CharacterPromptSection.Visibility = Visibility.Collapsed;
                    CharacterPromptsRepeater.ItemsSource = null;
                }

                // Legacy prompts
                PromptText.Text = meta.Prompt ?? string.Empty;
                NegativePromptText.Text = meta.NegativePrompt ?? string.Empty;

                // Parameters
                if (meta.Parameters != null && meta.Parameters.Count > 0)
                {
                    ParamsRepeater.ItemsSource = meta.Parameters.Select(kv => new KeyValuePair<string,string>(kv.Key, kv.Value)).ToList();
                }
                else
                {
                    ParamsRepeater.ItemsSource = null;
                }
            }
            else
            {
                Debug.WriteLine($"[Detail][Meta] NO metadata for {path}");
                // Clear UI if absent
                BasePromptSection.Visibility = Visibility.Collapsed;
                CharacterPromptSection.Visibility = Visibility.Collapsed;
                CharacterPromptsRepeater.ItemsSource = null;
                PromptText.Text = string.Empty;
                NegativePromptText.Text = string.Empty;
                ParamsRepeater.ItemsSource = null;
            }
        }
        catch (Exception ex) { Debug.WriteLine($"[Detail][Meta] Exception {ex.Message}"); }
    }

    private async System.Threading.Tasks.Task ShowErrorAsync(string message)
    { try { if (this.XamlRoot == null) return; var dlg = new ContentDialog { Title = "오류", Content = message, CloseButtonText = "확인", XamlRoot = this.XamlRoot }; await dlg.ShowAsync(); } catch { } }

    private void EnsureInitialZoom()
    { if (_initializedZoom) return; _initializedZoom = true; _currentScale = 1.0; ZoomSlider.Value = 1.0; ApplyScale(); }

    private void UpdateClip() { if (HostClip != null && ImageHost != null) HostClip.Rect = new Windows.Foundation.Rect(0,0, ImageHost.ActualWidth, ImageHost.ActualHeight); }

    private void EnsureTransformGroup()
    {
        if (DetailImage.RenderTransform is not TransformGroup tg)
        {
            tg = new TransformGroup();
            tg.Children.Add(new ScaleTransform { ScaleX = _currentScale, ScaleY = _currentScale });
            tg.Children.Add(new TranslateTransform());
            DetailImage.RenderTransform = tg;
        }
    }

    private (ScaleTransform scale, TranslateTransform translate) GetTransforms()
    {
        EnsureTransformGroup();
        var tg = (TransformGroup)DetailImage.RenderTransform;
        return ((ScaleTransform)tg.Children[0], (TranslateTransform)tg.Children[1]);
    }

    private void ApplyScale(bool animate=false, Point? zoomCenter=null)
    {
        if (DetailImage == null) return;
        _currentScale = Math.Clamp(_currentScale, ZoomSlider.Minimum, ZoomSlider.Maximum);
        var (scale, translate) = GetTransforms();
        if (zoomCenter.HasValue)
        {
            var center = zoomCenter.Value;
            var beforeX = (center.X - translate.X)/scale.ScaleX;
            var beforeY = (center.Y - translate.Y)/scale.ScaleY;
            scale.ScaleX = scale.ScaleY = _currentScale;
            var afterX = beforeX * scale.ScaleX;
            var afterY = beforeY * scale.ScaleY;
            translate.X += center.X - afterX - translate.X;
            translate.Y += center.Y - afterY - translate.Y;
        }
        else
        {
            scale.ScaleX = scale.ScaleY = _currentScale;
        }
        ZoomValueText.Text = _currentScale.ToString("0.00") + "x";
    }

    private void ZoomSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    { _currentScale = e.NewValue; ApplyScale(); }

    private void ImageDetailPage_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (ZoomSlider == null) return;
        var point = e.GetCurrentPoint(ImageHost);
        var delta = point.Properties.MouseWheelDelta;
        _currentScale *= (delta > 0 ? 1.1 : 0.9);
        _currentScale = Math.Clamp(_currentScale, ZoomSlider.Minimum, ZoomSlider.Maximum);
        ZoomSlider.Value = _currentScale;
        ApplyScale(zoomCenter: point.Position);
        e.Handled = true;
    }

    private void CenterImage()
    {
        if (DetailImage?.Source is BitmapImage bmp && ImageHost != null)
        {
            // Center by layout in Canvas to avoid CA fighting with RenderTransform translation
            double contentW = bmp.PixelWidth * _currentScale;
            double contentH = bmp.PixelHeight * _currentScale;
            double left = (ImageHost.ActualWidth - contentW) / 2.0;
            double top = (ImageHost.ActualHeight - contentH) / 2.0;
            try { Canvas.SetLeft(DetailImage, left); } catch { }
            try { Canvas.SetTop(DetailImage, top); } catch { }
        }
    }

    private void PanCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (DetailImage?.RenderTransform is TransformGroup)
        {
            _isPanning = true;
            var (_, translate) = GetTransforms();
            _startTranslateX = translate.X;
            _startTranslateY = translate.Y;
            _panStartPoint = e.GetCurrentPoint(ImageHost).Position;
            PanCanvas?.CapturePointer(e.Pointer);
            e.Handled = true;
        }
    }

    private void PanCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isPanning) return;
        var (_, translate) = GetTransforms();
        var pos = e.GetCurrentPoint(ImageHost).Position;
        translate.X = _startTranslateX + (pos.X - _panStartPoint.X);
        translate.Y = _startTranslateY + (pos.Y - _panStartPoint.Y);
    }

    private void PanCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isPanning)
        { _isPanning = false; PanCanvas?.ReleasePointerCapture(e.Pointer); e.Handled = true; }
    }

    private void ImageHost_SizeChanged(object sender, SizeChangedEventArgs e)
    { UpdateClip(); CenterImage(); }

    private void ImageDetailPage_SizeChanged(object sender, SizeChangedEventArgs e)
    { UpdateClip(); CenterImage(); }

    // Splitter handlers
    private void Splitter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _resizing = true;
        _initialPointerX = e.GetCurrentPoint(this).Position.X;
        _initialMetaWidth = MetaColumn.ActualWidth;
        if (sender is Border b) b.Background = _splitterHoverBrush;
        (sender as FrameworkElement)?.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Splitter_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_resizing) return;
        var currentX = e.GetCurrentPoint(this).Position.X;
        var delta = currentX - _initialPointerX; // drag right increases meta width
        var newWidth = _initialMetaWidth - delta; // meta panel on right
        newWidth = Math.Clamp(newWidth, MetaMinWidth, MetaMaxWidth);
        MetaColumn.Width = new GridLength(newWidth, GridUnitType.Pixel);
        e.Handled = true;
    }

    private void Splitter_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_resizing)
        {
            _resizing = false;
            (sender as FrameworkElement)?.ReleasePointerCapture(e.Pointer);
            if (sender is Border b) b.Background = _splitterBaseBrush;
            e.Handled = true;
        }
    }

    private void Splitter_PointerEntered(object sender, PointerRoutedEventArgs e)
    { if (sender is Border b && !_resizing) b.Background = _splitterHoverBrush; }

    private void Splitter_PointerExited(object sender, PointerRoutedEventArgs e)
    { if (sender is Border b && !_resizing) b.Background = _splitterBaseBrush; }
}
