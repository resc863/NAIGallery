using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NAIGallery.Services;
using NAIGallery.ViewModels;
using NAIGallery.Models;
using Microsoft.Windows.Storage.Pickers;
using WinRT.Interop;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Diagnostics;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI; // Added for Win32Interop
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;

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
    private Dictionary<ImageMetadata, int>? _imageIndexMap;

    private DateTime _lastTapAt = DateTime.MinValue;

    // 스크롤 위치 보존
    private double _savedScrollOffset = 0;

    public GalleryPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Enabled;

        var app = (App)Application.Current;
        _service = app.GetRequiredService<IImageIndexService>();
        ViewModel = app.GetRequiredService<GalleryViewModel>();
        
        // DispatcherQueue 설정 - UI 스레드에서 호출되므로 항상 유효
        var dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        ViewModel.SetDispatcherQueue(dispatcherQueue);
        
        try { _service.InitializeDispatcher(dispatcherQueue); } catch { }
        DataContext = ViewModel;

        TileLineHeight = _baseItemSize;
        MinItemWidth = _baseItemSize; // initialize DP

        ViewModel.ImagesChanged += OnImagesChanged;
        ViewModel.BeforeCollectionRefresh += OnBeforeCollectionRefresh;
        ViewModel.AfterCollectionRefresh += OnAfterCollectionRefresh;
        
        // 썸네일 적용 이벤트 구독 - 가상화된 컨테이너 갱신용
        _service.ThumbnailApplied += OnThumbnailApplied;
        
        Loaded += GalleryPage_Loaded;
        Unloaded += GalleryPage_Unloaded;
        SizeChanged += Gallery_SizeChanged;
        AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(Root_PointerWheelChanged), true);
    }

    private IReadOnlyDictionary<ImageMetadata, int> GetImageIndexMap()
    {
        var map = _imageIndexMap;
        if (map != null && map.Count == ViewModel.Images.Count)
            return map;

        return RebuildImageIndexMap();
    }

    private Dictionary<ImageMetadata, int> RebuildImageIndexMap()
    {
        var map = new Dictionary<ImageMetadata, int>(ViewModel.Images.Count);
        for (int i = 0; i < ViewModel.Images.Count; i++)
        {
            map[ViewModel.Images[i]] = i;
        }

        _imageIndexMap = map;
        return map;
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
        try
        {
            var hwnd = WindowNative.GetWindowHandle(((App)Application.Current).MainWindow);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            var picker = new FolderPicker(windowId);
            var result = await picker.PickSingleFolderAsync();
            var path = result?.Path;
            if (!string.IsNullOrWhiteSpace(path))
            {
                _initialPrimed = false;
                await ViewModel.IndexFolderAsync(path);
                await PrimeInitialAsync();
            }
        }
        catch (COMException) { }
    }

    private void SortDirectionToggle_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return; // Add null check

        ViewModel.SortDirection = ViewModel.SortDirection == GallerySortDirection.Asc ? GallerySortDirection.Desc : GallerySortDirection.Asc;

        // Update button text to show current direction
        if (SortDirText != null)
        {
            SortDirText.Text = ViewModel.SortDirection == GallerySortDirection.Asc ? "↑" : "↓";
        }

        _initialPrimed = false;
        _ = PrimeInitialAsync();
    }

    private void RefreshUI_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 현재 스크롤 위치 저장
            double savedScrollOffset = 0;
            if (_scrollViewer != null)
            {
                savedScrollOffset = _scrollViewer.VerticalOffset;
            }
            
            // 1. 파이프라인 일시 중지
            _service.SetApplySuspended(true);
            
            // 2. 스케줄링 상태 초기화 (이전에 실패하거나 대기 중이던 아이템들 다시 시도 가능)
            _service.ResetPendingState();
            
            // 3. 모든 현재 메타데이터의 썸네일 상태 초기화 (강제 재로드를 위해)
            if (ViewModel?.Images != null)
            {
                foreach (var meta in ViewModel.Images)
                {
                    // 썸네일 픽셀 너비만 초기화 (Thumbnail 객체는 유지하여 깜빡임 방지)
                    // 이렇게 하면 파이프라인이 해당 아이템을 다시 스케줄링할 수 있음
                    meta.ThumbnailPixelWidth = 0;
                }
            }
            
            // 4. UI 스레드에서 즉시 실행
            if (ViewModel?.Images != null && GalleryView != null)
            {
                DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.High, async () =>
                {
                    try
                    {
                        // 레이아웃 완전 갱신
                        _itemsHost = null; // 캐시된 ItemsHost 초기화
                        GalleryView.InvalidateMeasure();
                        GalleryView.InvalidateArrange();
                        GalleryView.UpdateLayout();
                        
                        // 잠시 대기 후 스크롤 위치 복원
                        await Task.Delay(50);
                        
                        if (_scrollViewer != null && savedScrollOffset > 0)
                        {
                            _scrollViewer.ChangeView(null, savedScrollOffset, null, disableAnimation: true);
                        }
                        
                        // 5. 파이프라인 재개
                        _service.SetApplySuspended(false);
                        _service.FlushApplyQueue();
                        
                        // 6. 뷰포트 범위 재계산
                        EnqueueVisibleStrict();
                        
                        // 7. 가시 영역 아이템 강제 로드
                        int desiredWidth = GetDesiredDecodeWidth();
                        int startIdx = Math.Max(0, _viewStartIndex);
                        int endIdx = Math.Min(_viewEndIndex, ViewModel.Images.Count - 1);
                        
                        var visibleItems = new System.Collections.Generic.List<ImageMetadata>();
                        for (int i = startIdx; i <= endIdx && i < ViewModel.Images.Count; i++)
                        {
                            visibleItems.Add(ViewModel.Images[i]);
                        }
                        
                        // 강제 우선순위 부스트
                        if (visibleItems.Count > 0)
                        {
                            _service.BoostVisible(visibleItems, desiredWidth);
                        }
                        
                        // 8. 즉시 프리로드 시작
                        _ = ProcessQueueAsync();
                        
                        // 9. 버퍼 영역까지 포함한 프리로드
                        int bufferSize = Math.Max(20, (endIdx - startIdx + 1));
                        var bufferStart = Math.Max(0, startIdx - bufferSize);
                        var bufferEnd = Math.Min(ViewModel.Images.Count - 1, endIdx + bufferSize);
                        
                        var bufferItems = new System.Collections.Generic.List<ImageMetadata>();
                        for (int i = bufferStart; i <= bufferEnd && i < ViewModel.Images.Count; i++)
                        {
                            if (i < startIdx || i > endIdx) // 가시 영역 제외
                                bufferItems.Add(ViewModel.Images[i]);
                        }
                        
                        // Viewport 업데이트 (가시 + 버퍼)
                        _service.UpdateViewport(visibleItems, bufferItems, desiredWidth);
                        
                        // 10. IdleFill 재시작
                        StartIdleFill();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[RefreshUI] Error: {ex.Message}");
                    }
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RefreshUI] Outer Error: {ex.Message}");
        }
    }

    private void SortFieldCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo || combo.SelectedItem is not ComboBoxItem item) return;
        if (ViewModel == null) return; // Add null check
        
        var tag = item.Tag as string;
        if (tag == "Name") ViewModel.SortField = GallerySortField.Name;
        else if (tag == "Date") ViewModel.SortField = GallerySortField.Date;
        
        _initialPrimed = false;
        _ = PrimeInitialAsync();
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e) 
    {
        // F5 키로 새로고침 지원
        if (e.Key == Windows.System.VirtualKey.F5)
        {
            RefreshUI_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }
    
    private void Root_KeyUp(object sender, KeyRoutedEventArgs e) { }

    private void OnImagesChanged(object? sender, EventArgs e)
    {
        _initialPrimed = false;
        
        // UI 스레드에서 실행되도록 보장
        if (DispatcherQueue?.HasThreadAccess != true)
        {
            DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.High, () => OnImagesChangedCore());
            return;
        }
        
        OnImagesChangedCore();
    }
    
    private void OnImagesChangedCore()
    {
        try
        {
            // 이미지 목록이 변경되면 즉시 가시 영역 썸네일 로드
            EnqueueVisibleStrict();
            _ = ProcessQueueAsync();
            UpdateSchedulerViewport();
            
            // UI 강제 갱신 (인덱싱 중에도 새로 추가된 아이템 즉시 표시)
            if (GalleryView != null)
            {
                try
                {
                    // ItemsRepeater 레이아웃 갱신
                    GalleryView.InvalidateMeasure();
                    GalleryView.InvalidateArrange();
                    GalleryView.UpdateLayout();
                }
                catch { }
            }
            
            // 인덱싱 중이 아닐 때만 전체 프라이밍 수행
            if (!ViewModel.IsIndexing)
            {
                _ = PrimeInitialAsync();
            }
            else
            {
                // 인덱싱 중에는 가시 영역만 즉시 로드
                _ = PrimeVisibleOnlyAsync();
            }
        }
        catch { }
    }
    
    private async Task PrimeVisibleOnlyAsync()
    {
        if (_primeGate.CurrentCount == 0) return;
        await _primeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ViewModel?.Images == null || ViewModel.Images.Count == 0) return;
            
            var decodeWidth = GetDesiredDecodeWidth();
            var visible = new System.Collections.Generic.List<ImageMetadata>();
            
            // 가시 영역 아이템 수집
            for (int i = _viewStartIndex; i <= _viewEndIndex && i < ViewModel.Images.Count; i++)
            {
                if (i >= 0)
                {
                    var meta = ViewModel.Images[i];
                    if (meta.Thumbnail == null || (meta.ThumbnailPixelWidth ?? 0) + 32 < decodeWidth)
                        visible.Add(meta);
                }
            }
            
            if (visible.Count > 0)
            {
                await _service.PreloadThumbnailsAsync(
                    visible, 
                    decodeWidth, 
                    default, 
                    maxParallelism: ComputeDecodeParallelism()
                ).ConfigureAwait(false);
            }
        }
        finally
        {
            _primeGate.Release();
        }
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

    private void OnBeforeCollectionRefresh(object? sender, EventArgs e)
    {
        // 컬렉션 새로고침 전 스크롤 위치 저장
        if (_scrollViewer != null)
        {
            _savedScrollOffset = _scrollViewer.VerticalOffset;
        }
    }
    
    private void OnAfterCollectionRefresh(object? sender, EventArgs e)
    {
        // 컬렉션 새로고침 후 스크롤 위치 복원
        if (_scrollViewer != null && _savedScrollOffset > 0)
        {
            _scrollViewer.ChangeView(null, _savedScrollOffset, null, disableAnimation: true);
            _savedScrollOffset = 0; // 복원 후 초기화
        }
    }
}
