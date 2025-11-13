using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NAIGallery.Services;
using NAIGallery.ViewModels;
using NAIGallery.Models;
using Windows.Storage.Pickers;
using WinRT.Interop;
using System;
using Windows.System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Diagnostics;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Microsoft.UI.Dispatching;

namespace NAIGallery.Views;

public sealed partial class GalleryPage : Page
{
    public GalleryViewModel ViewModel { get; }
    private readonly IImageIndexService _service;

    private double _baseItemSize = 200;
    private const double _minSize = 80;
    private const double _maxSize = 600;

    // Converted to DP so x:Bind DesiredColumnWidth updates when zoom changes (Method A)
    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }
    public static readonly DependencyProperty MinItemWidthProperty =
        DependencyProperty.Register(nameof(MinItemWidth), typeof(double), typeof(GalleryPage), new PropertyMetadata(200.0, OnMinItemWidthChanged));
    private static void OnMinItemWidthChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is GalleryPage p)
        {
            try { p.GalleryView?.InvalidateMeasure(); p.GalleryView?.InvalidateArrange(); } catch { }
        }
    }

    public double TileLineHeight
    {
        get => (double)GetValue(TileLineHeightProperty);
        set => SetValue(TileLineHeightProperty, value);
    }
    public static readonly DependencyProperty TileLineHeightProperty =
        DependencyProperty.Register(nameof(TileLineHeight), typeof(double), typeof(GalleryPage), new PropertyMetadata(200.0));

    private bool _isLoaded = false;

    // Viewport tracking
    private volatile int _viewStartIndex = 0;
    private volatile int _viewEndIndex = -1;

    // Initial prime/preload
    private bool _initialPrimed = false;
    private readonly SemaphoreSlim _primeGate = new(1,1);
    private CancellationTokenSource? _preloadCts;

    // Composition / animation state
    private Microsoft.UI.Composition.Compositor? _compositor;
    private Microsoft.UI.Composition.ImplicitAnimationCollection? _implicitOffsetAnimations;
    private bool _isForwardFading = false;
    private bool _isBackAnimating = false;
    private string? _pendingBackPath;
    private bool _suppressImplicitDuringBack = false;
    private CancellationTokenSource? _backRevealCts;

    // Scroll / idle
    private double _lastScrollOffset = 0;
    private bool _isScrollBubbling = false;
    private CancellationTokenSource? _idleFillCts;

    // Zoom / input helpers
    private CancellationTokenSource? _preRealizeCts;
    private CancellationTokenSource? _postZoomSuppressCts;

    // Resize debounce
    private CancellationTokenSource? _sizeChangedDebounceCts;

    // Realization helpers
    private ScrollViewer? _scrollViewer;
    private Panel? _itemsHost;
    private CancellationTokenSource? _reflowCts;

    private DateTime _lastTapAt = DateTime.MinValue;

    public GalleryPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Enabled;

        _service = ((App)Application.Current).Services.GetService(typeof(IImageIndexService)) as IImageIndexService ?? new ImageIndexService();
        if (Application.Current.Resources.TryGetValue("GlobalGalleryVM", out var existing) && existing is GalleryViewModel vm)
            ViewModel = vm;
        else
        {
            ViewModel = new GalleryViewModel(_service);
            Application.Current.Resources["GlobalGalleryVM"] = ViewModel;
        }
        try { _service.InitializeDispatcher(global::Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()); } catch { }
        DataContext = ViewModel;

        TileLineHeight = _baseItemSize;
        MinItemWidth = _baseItemSize; // initialize DP

        ViewModel.ImagesChanged += OnImagesChanged;
        Loaded += GalleryPage_Loaded;
        Unloaded += GalleryPage_Unloaded;
        SizeChanged += Gallery_SizeChanged;
        AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(Root_PointerWheelChanged), true);
    }

    private void Gallery_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        EnqueueVisibleStrict();
        _ = ProcessQueueAsync();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        try
        {
            // Reset hover/navigation transient state so tile hover scale works again after returning from detail
            ResetHoverNavigationState();
        }
        catch { }
        try
        {
            _isForwardFading = false;
            if (GalleryView != null) ResetSubtreeOpacity(GalleryView);
            var tb = GetTopBar(); if (tb != null) ResetSubtreeOpacity(tb);
            EnqueueVisibleStrict();
            _ = ProcessQueueAsync();
            // Resume pipeline immediately so blanks are filled without waiting for first scroll idle
            _service.SetApplySuspended(false);
            _service.FlushApplyQueue();
            UpdateSchedulerViewport();
            StartIdleFill();
            var cas = ConnectedAnimationService.GetForCurrentView();
            var back = cas.GetAnimation("BackConnectedAnimation");
            if (back != null && Application.Current.Resources.TryGetValue("BackPath", out var p) && p is string path && !string.IsNullOrWhiteSpace(path))
            {
                _pendingBackPath = path;
                _ = TryStartBackCAAsync(path, back);
            }
        }
        catch { }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        try { _service.SetApplySuspended(true); } catch { }
        try { _preloadCts?.Cancel(); } catch { }
    }

    private void GalleryPage_Loaded(object? sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        ApplyItemSize();
        InitComposition();
        HookScroll();
        EnqueueVisibleStrict();
        _ = ProcessQueueAsync();
        try { _service.SetApplySuspended(false); _service.FlushApplyQueue(); StartIdleFill(); UpdateSchedulerViewport(); } catch { }
    }

    private void GalleryPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        try { _preloadCts?.Cancel(); } catch { }
        try { _idleFillCts?.Cancel(); } catch { }
        try { _service.SetApplySuspended(true); } catch { }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => _ = OpenFolderAsync();
    private async Task OpenFolderAsync()
    {
        var picker = new FolderPicker();
        var hwnd = WindowNative.GetWindowHandle(((App)Application.Current).MainWindow);
        InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeFilter.Add("*");
        try
        {
            var folder = await picker.PickSingleFolderAsync();
            if (folder != null)
            {
                _initialPrimed = false;
                await ViewModel.IndexFolderAsync(folder.Path);
                await PrimeInitialAsync();
            }
        }
        catch (COMException) { }
    }

    private void SortDirectionToggle_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SortDirection = ViewModel.SortDirection == GallerySortDirection.Asc ? GallerySortDirection.Desc : GallerySortDirection.Asc;
        _initialPrimed = false;
        _ = PrimeInitialAsync();
    }

    private void SortFieldCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo || combo.SelectedItem is not ComboBoxItem item) return;
        var tag = item.Tag as string;
        if (tag == "Name") ViewModel.SortField = GallerySortField.Name;
        else if (tag == "Date") ViewModel.SortField = GallerySortField.Date;
        _initialPrimed = false;
        _ = PrimeInitialAsync();
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e) { }
    private void Root_KeyUp(object sender, KeyRoutedEventArgs e) { }

    private void OnImagesChanged(object? sender, EventArgs e)
    {
        _initialPrimed = false;
        _ = PrimeInitialAsync();
    }

    private UIElement? GetTopBar()
    {
        try { return FindName("TopBar") as UIElement; } catch { return null; }
    }

    private static int ComputeDecodeParallelism()
    {
        try
        {
            int lp = Environment.ProcessorCount;
            int half = Math.Max(1, lp / 2);
            return Math.Clamp(half, 2, 12);
        }
        catch { return 8; }
    }

    // AutoSuggestBox handlers
    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        // Suggestions driven by ViewModel OnSearchQueryChanged; no extra logic needed except manual refresh for non-user changes
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            // ViewModel already updates suggestions via property change hook
        }
    }

    private void SearchBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is string s && ViewModel != null)
        {
            // Replace last token with chosen suggestion
            var parts = (ViewModel.SearchQuery ?? string.Empty)
                .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            if (parts.Count > 0) parts[^1] = s; else parts.Add(s);
            ViewModel.SearchQuery = string.Join(" ", parts);
        }
    }
}
