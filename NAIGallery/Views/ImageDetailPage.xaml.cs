using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;
using NAIGallery.Services;
using System;
using System.Linq;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using System.IO;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using NAIGallery.Models;
using NAIGallery.ViewModels;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Media.Animation;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

namespace NAIGallery.Views;

public sealed partial class ImageDetailPage : Page
{
    #region Fields
    
    private readonly IImageIndexService _service;
    
    // Zoom/Pan state
    private double _currentScale = 1.0;
    private bool _initializedZoom = false;
    private bool _isPanning = false;
    private Point _panStartPoint;
    private double _startTranslateX;
    private double _startTranslateY;
    
    // Navigation state
    private string? _pendingPath;
    private string? _currentPath;
    private int _loadSeq = 0;
    
    // Animation state
    private bool _isForwardConnectedAnimating = false;
    private bool _forwardStarted = false;
    private bool _pendingForwardCA = false;
    private bool _imageOpened = false;
    private RectangleGeometry? _savedHostClip = null;
    
    // Splitter state
    private bool _resizing = false;
    private double _initialMetaWidth;
    private double _initialPointerX;
    private const double MetaMinWidth = 200;
    private const double MetaMaxWidth = 1000;
    private readonly Brush _splitterBaseBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(4, 0, 0, 0));
    private readonly Brush _splitterHoverBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 0, 0, 0));
    
    private CancellationTokenSource? _thumbCts;
    
    #endregion

    #region Constructor
    
    public ImageDetailPage()
    {
        InitializeComponent();
        _service = ((App)Application.Current).Services.GetService(typeof(IImageIndexService)) as IImageIndexService
                    ?? new ImageIndexService();
        
        PointerWheelChanged += ImageDetailPage_PointerWheelChanged;
        SizeChanged += ImageDetailPage_SizeChanged;
        Loaded += ImageDetailPage_Loaded;
        Unloaded += ImageDetailPage_Unloaded;
    }
    
    #endregion

    #region Lifecycle
    
    private void ImageDetailPage_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateClip();
        HideAllUIForCA();

        if (_pendingPath != null)
        {
            var p = _pendingPath;
            _pendingPath = null;
            LoadImage(p);
        }

        _pendingForwardCA = true;
        RequestStartForwardConnectedAnimation();

        if (SplitterBorder != null)
            SplitterBorder.Background = _splitterBaseBrush;
    }

    private void ImageDetailPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _forwardStarted = false;
        _isForwardConnectedAnimating = false;
        try { _thumbCts?.Cancel(); _thumbCts?.Dispose(); } catch { }
        _thumbCts = null;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string path)
        {
            if (DetailImage == null)
                _pendingPath = path;
            else
                LoadImage(path);
        }

        _pendingForwardCA = true;
        RequestStartForwardConnectedAnimation();
    }

    protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        base.OnNavigatingFrom(e);
        try
        {
            var cas = ConnectedAnimationService.GetForCurrentView();

            if (e.NavigationMode == NavigationMode.Back)
            {
                if (!string.IsNullOrEmpty(_currentPath))
                    Application.Current.Resources["BackPath"] = _currentPath;
                cas.PrepareToAnimate("BackConnectedAnimation", DetailImage);
            }
            else
            {
                cas.GetAnimation("ForwardConnectedAnimation")?.Cancel();
                cas.GetAnimation("BackConnectedAnimation")?.Cancel();
            }
        }
        catch { }
    }
    
    #endregion

    #region Image Loading
    
    private void LoadImage(string path)
    {
        try
        {
            path = Path.GetFullPath(path);
            if (!File.Exists(path))
            {
                _ = ShowErrorAsync("이미지 파일을 찾을 수 없습니다:\n" + path);
                return;
            }

            _imageOpened = false;
            _initializedZoom = false;
            _currentPath = path;
            unchecked { _loadSeq++; }
            try { Application.Current.Resources["CurrentDetailPath"] = path; } catch { }

            try { _thumbCts?.Cancel(); _thumbCts?.Dispose(); } catch { }
            _thumbCts = new CancellationTokenSource();

            ResetTransforms();
            PopulateMetadata(path);

            LoadThumbnailPreview(path);
            LoadFullImage(path);
        }
        catch (Exception ex)
        {
            _ = ShowErrorAsync("이미지를 여는 중 오류가 발생했습니다:\n" + ex.Message);
        }
    }

    private void LoadThumbnailPreview(string path)
    {
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
                            try
                            {
                                if (DetailImage?.Source is not BitmapImage && meta.Thumbnail != null)
                                    DetailImage.Source = meta.Thumbnail;
                            }
                            catch { }
                        });
                    }
                    catch { }
                });
            }
        }
    }

    private void LoadFullImage(string path)
    {
        var bmp = new BitmapImage(new Uri(path));
        try { bmp.DecodePixelWidth = ComputeDesiredDetailWidth(); } catch { }
        
        bmp.ImageOpened += (s, _) =>
        {
            _imageOpened = true;
            RequestStartForwardConnectedAnimation();
            if (!_forwardStarted || !_isForwardConnectedAnimating)
            {
                EnsureInitialZoom();
                CenterImage();
            }
        };
        
        DetailImage.Source = bmp;
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
    
    #endregion

    #region Metadata
    
    private void PopulateMetadata(string path)
    {
        try
        {
            if (_service.TryGet(path, out var meta) && meta != null)
            {
                _service.RefreshMetadata(meta);

                bool hasBase = !string.IsNullOrWhiteSpace(meta.BasePrompt) || !string.IsNullOrWhiteSpace(meta.BaseNegativePrompt);
                bool hasChars = meta.CharacterPrompts != null && meta.CharacterPrompts.Count > 0;
                bool hasV4 = hasBase || hasChars;

                SetupBasePromptUI(meta, hasBase);
                SetupCharacterPromptsUI(meta, hasChars);
                SetupLegacyPromptsUI(meta, hasV4);
                SetupParametersUI(meta);
            }
            else
            {
                ClearMetadataUI();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Detail][Meta] Exception {ex.Message}");
        }
    }

    private void SetupBasePromptUI(ImageMetadata meta, bool hasBase)
    {
        if (hasBase)
        {
            BasePromptSection.Visibility = Visibility.Visible;
            BasePromptText.Text = meta.BasePrompt ?? string.Empty;
            BaseNegativePromptText.Text = meta.BaseNegativePrompt ?? string.Empty;
        }
        else
        {
            BasePromptSection.Visibility = Visibility.Collapsed;
            BasePromptText.Text = string.Empty;
            BaseNegativePromptText.Text = string.Empty;
        }
    }

    private void SetupCharacterPromptsUI(ImageMetadata meta, bool hasChars)
    {
        if (hasChars)
        {
            CharacterPromptSection.Visibility = Visibility.Visible;
            CharacterPromptsRepeater.ItemsSource = meta.CharacterPrompts;
        }
        else
        {
            CharacterPromptSection.Visibility = Visibility.Collapsed;
            CharacterPromptsRepeater.ItemsSource = null;
        }
    }

    private void SetupLegacyPromptsUI(ImageMetadata meta, bool hasV4)
    {
        var legacyVis = hasV4 ? Visibility.Collapsed : Visibility.Visible;
        if (PromptLabel != null) PromptLabel.Visibility = legacyVis;
        if (PromptText != null) PromptText.Visibility = legacyVis;
        if (NegativePromptLabel != null) NegativePromptLabel.Visibility = legacyVis;
        if (NegativePromptText != null) NegativePromptText.Visibility = legacyVis;

        PromptText.Text = meta.Prompt ?? string.Empty;
        NegativePromptText.Text = meta.NegativePrompt ?? string.Empty;
    }

    private void SetupParametersUI(ImageMetadata meta)
    {
        if (meta.Parameters != null && meta.Parameters.Count > 0)
        {
            ParamsRepeater.ItemsSource = meta.Parameters
                .Select(kv => new ParamEntry { Key = kv.Key, Value = kv.Value })
                .ToList();
        }
        else
        {
            ParamsRepeater.ItemsSource = null;
        }
    }

    private void ClearMetadataUI()
    {
        BasePromptSection.Visibility = Visibility.Collapsed;
        CharacterPromptSection.Visibility = Visibility.Collapsed;
        CharacterPromptsRepeater.ItemsSource = null;
        PromptText.Text = string.Empty;
        NegativePromptText.Text = string.Empty;
        if (PromptLabel != null) PromptLabel.Visibility = Visibility.Visible;
        if (PromptText != null) PromptText.Visibility = Visibility.Visible;
        if (NegativePromptLabel != null) NegativePromptLabel.Visibility = Visibility.Visible;
        if (NegativePromptText != null) NegativePromptText.Visibility = Visibility.Visible;
        ParamsRepeater.ItemsSource = null;
    }
    
    #endregion

    #region Zoom & Pan
    
    private void EnsureInitialZoom()
    {
        if (_initializedZoom) return;
        _initializedZoom = true;
        _currentScale = 1.0;
        ZoomSlider.Value = 1.0;
        ApplyScale();
    }

    private void ApplyScale(bool animate = false, Point? zoomCenter = null)
    {
        if (DetailImage == null) return;
        _currentScale = Math.Clamp(_currentScale, ZoomSlider.Minimum, ZoomSlider.Maximum);
        var (scale, translate) = GetTransforms();

        if (zoomCenter.HasValue)
        {
            var center = zoomCenter.Value;
            var beforeX = (center.X - translate.X) / scale.ScaleX;
            var beforeY = (center.Y - translate.Y) / scale.ScaleY;
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
    {
        _currentScale = e.NewValue;
        ApplyScale();
    }

    private void ImageDetailPage_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (ZoomSlider == null) return;
        var point = e.GetCurrentPoint(ImageHost);
        var delta = point.Properties.MouseWheelDelta;
        _currentScale *= delta > 0 ? 1.1 : 0.9;
        _currentScale = Math.Clamp(_currentScale, ZoomSlider.Minimum, ZoomSlider.Maximum);
        ZoomSlider.Value = _currentScale;
        ApplyScale(zoomCenter: point.Position);
        e.Handled = true;
    }

    private void CenterImage()
    {
        if (DetailImage?.Source is BitmapImage bmp && ImageHost != null)
        {
            double contentW = bmp.PixelWidth * _currentScale;
            double contentH = bmp.PixelHeight * _currentScale;
            double left = (ImageHost.ActualWidth - contentW) / 2.0;
            double top = (ImageHost.ActualHeight - contentH) / 2.0;
            try { Canvas.SetLeft(DetailImage, left); } catch { }
            try { Canvas.SetTop(DetailImage, top); } catch { }
        }
    }

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
        {
            _isPanning = false;
            PanCanvas?.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }
    }

    private void UpdateClip()
    {
        if (HostClip != null && ImageHost != null)
            HostClip.Rect = new Rect(0, 0, ImageHost.ActualWidth, ImageHost.ActualHeight);
    }

    private void ImageHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateClip();
        CenterImage();
    }

    private void ImageDetailPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateClip();
        CenterImage();
    }
    
    #endregion

    #region Connected Animation
    
    private void HideAllUIForCA()
    {
        try { if (TopBar != null) TopBar.Opacity = 0; } catch { }
        try { if (MetaScroll != null) MetaScroll.Opacity = 0; } catch { }
        try { if (SplitterBorder != null) SplitterBorder.Opacity = 0; } catch { }
        try { if (DetailImage != null) DetailImage.Opacity = 0; } catch { }
        try { if (PrevButton != null) PrevButton.Opacity = 0; } catch { }
        try { if (NextButton != null) NextButton.Opacity = 0; } catch { }
    }

    private async void RequestStartForwardConnectedAnimation()
    {
        if (_forwardStarted || !_pendingForwardCA) return;

        var cas = ConnectedAnimationService.GetForCurrentView();
        var forward = cas.GetAnimation("ForwardConnectedAnimation");
        if (forward == null)
        {
            _isForwardConnectedAnimating = false;
            try { Application.Current.Resources["ForwardCAStarted"] = false; } catch { }
            FadeInAllUI();
            return;
        }

        if (DetailImage == null || !_imageOpened) return;

        int seqAtRequest = _loadSeq;

        try
        {
            await WaitForHostStableAsync();
            if (seqAtRequest != _loadSeq || DetailImage.Source is not BitmapImage)
            {
                try { Application.Current.Resources["ForwardCAStarted"] = false; } catch { }
                FadeInAllUI();
                return;
            }

            _pendingForwardCA = false;
            _forwardStarted = true;
            _isForwardConnectedAnimating = true;

            ResetTransforms();
            EnsureInitialZoom();
            CenterImage();

            _savedHostClip = ImageHost?.Clip as RectangleGeometry;
            if (ImageHost != null) ImageHost.Clip = null;

            forward.Configuration = new GravityConnectedAnimationConfiguration();

            var coordinated = new List<UIElement>();
            if (TopBar != null) coordinated.Add(TopBar);
            if (SplitterBorder != null) coordinated.Add(SplitterBorder);
            if (MetaScroll != null) coordinated.Add(MetaScroll);
            foreach (var el in coordinated) { try { el.Opacity = 0.1; } catch { } }

            bool started = coordinated.Count > 0
                ? forward.TryStart(DetailImage, coordinated)
                : forward.TryStart(DetailImage);
            try { Application.Current.Resources["ForwardCAStarted"] = started; } catch { }

            if (started)
            {
                try { if (DetailImage != null) DetailImage.Opacity = 1; } catch { }
            }

            forward.Completed += (s, _) =>
            {
                _isForwardConnectedAnimating = false;
                if (ImageHost != null)
                {
                    try { ImageHost.Clip = _savedHostClip ?? HostClip; } catch { }
                }
                FadeInAllUI();
            };

            if (!started)
                FadeInAllUI();
        }
        catch
        {
            _isForwardConnectedAnimating = false;
            try { Application.Current.Resources["ForwardCAStarted"] = false; } catch { }
            FadeInAllUI();
        }
    }

    private void FadeInAllUI()
    {
        FadeInElement(TopBar, 200);
        FadeInElement(MetaScroll, 200);
        FadeInElement(SplitterBorder, 200);
        try { if (PrevButton != null) PrevButton.Opacity = 0.7; } catch { }
        try { if (NextButton != null) NextButton.Opacity = 0.7; } catch { }
    }

    private static void FadeInElement(UIElement? el, double durationMs = 200)
    {
        if (el == null) return;
        try
        {
            if (el.Opacity >= 0.99) return;
            el.Opacity = 0.1;
            var anim = new DoubleAnimation
            {
                To = 1.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
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

    private async Task WaitForHostStableAsync()
    {
        if (ImageHost == null)
        {
            await Task.Yield();
            return;
        }

        var tcs = new TaskCompletionSource<bool>();
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
                stable = 0;
                lw = w;
                lh = h;
            }
        }

        CompositionTarget.Rendering += OnRender;
        try { await tcs.Task; }
        finally { CompositionTarget.Rendering -= OnRender; }
    }
    
    #endregion

    #region Navigation
    
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
                return vm.Images.ToList();
        }
        catch { }
        return _service.All.OrderBy(m => m.FilePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

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
                    if (string.Equals(list[i].FilePath, currentPath, StringComparison.OrdinalIgnoreCase))
                    {
                        current = i;
                        break;
                    }
                }
                if (current >= 0)
                {
                    int next = current + delta;
                    if (next >= 0 && next < list.Count)
                        LoadImage(list[next].FilePath);
                }
            }
        }
        catch { }
    }

    private void PrevButton_Click(object sender, RoutedEventArgs e) => NavigateRelative(-1);
    private void NextButton_Click(object sender, RoutedEventArgs e) => NavigateRelative(1);

    private void NavButton_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button btn) btn.Opacity = 1.0;
    }

    private void NavButton_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button btn) btn.Opacity = 0.7;
    }
    
    #endregion

    #region Splitter
    
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
        var delta = currentX - _initialPointerX;
        var newWidth = _initialMetaWidth - delta;
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
    {
        if (sender is Border b && !_resizing)
            b.Background = _splitterHoverBrush;
    }

    private void Splitter_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border b && !_resizing)
            b.Background = _splitterBaseBrush;
    }
    
    #endregion

    #region Helpers
    
    private async Task ShowErrorAsync(string message)
    {
        try
        {
            if (this.XamlRoot == null) return;
            var dlg = new ContentDialog
            {
                Title = "오류",
                Content = message,
                CloseButtonText = "확인",
                XamlRoot = this.XamlRoot
            };
            await dlg.ShowAsync();
        }
        catch { }
    }
    
    #endregion
}
