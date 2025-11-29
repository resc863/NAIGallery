using System.Collections.ObjectModel;
using System.Threading.Tasks;
using NAIGallery.Models;
using NAIGallery.Services;
using System.Linq;
using System;
using System.Collections.Generic;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.WinUI.Collections; // AdvancedCollectionView
using System.Collections.Specialized;
using Microsoft.UI.Dispatching;

namespace NAIGallery.ViewModels;

public enum GallerySortField { Name, Date }
public enum GallerySortDirection { Asc, Desc }

/// <summary>
/// ViewModel for the gallery page using CommunityToolkit.Mvvm to reduce boilerplate.
/// </summary>
public partial class GalleryViewModel : ObservableObject
{
    private readonly IImageIndexService _indexService;
    private readonly DispatcherQueue? _dispatcherQueue;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private GallerySortField _sortField = GallerySortField.Name;

    [ObservableProperty]
    private GallerySortDirection _sortDirection = GallerySortDirection.Asc;

    [ObservableProperty]
    private bool _isIndexing;

    // Manual search mode properties (avoid partial method generator issues)
    private bool _searchAndMode;
    public bool SearchAndMode
    {
        get => _searchAndMode;
        set
        {
            if (_searchAndMode != value)
            {
                _searchAndMode = value;
                OnPropertyChanged();
                _ = ApplySearchAsync();
            }
        }
    }

    private bool _searchPartialMode = true;
    public bool SearchPartialMode
    {
        get => _searchPartialMode;
        set
        {
            if (_searchPartialMode != value)
            {
                _searchPartialMode = value;
                OnPropertyChanged();
                _ = ApplySearchAsync();
            }
        }
    }

    private CancellationTokenSource? _searchCts; // debounce token
    private List<ImageMetadata> _lastSearch = new();

    // Backing source (kept for compatibility with existing view code)
    public ObservableCollection<ImageMetadata> Images { get; } = new();
    // Optional view (for future use or consumers that prefer ACV APIs)
    public AdvancedCollectionView ImagesView { get; }

    public ObservableCollection<string> TagSuggestions { get; } = new();

    public event EventHandler? ImagesChanged;
    
    // 스크롤 위치 보존을 위한 이벤트
    public event EventHandler? BeforeCollectionRefresh;
    public event EventHandler? AfterCollectionRefresh;

    public GalleryViewModel(IImageIndexService service)
    {
        _indexService = service;
        
        // UI 스레드의 DispatcherQueue 캡처
        try
        {
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        }
        catch
        {
            _dispatcherQueue = null;
        }
        
        ImagesView = new AdvancedCollectionView(Images, false);
        ApplySortToView();
        _ = ApplySearchAsync();
        
        // 인덱스 변경 이벤트 구독 (실시간 UI 업데이트)
        _indexService.IndexChanged += OnIndexChanged;
    }

    private void OnIndexChanged(object? sender, EventArgs e)
    {
        // 인덱스가 변경되면 즉시 UI 업데이트 (폴링 제거)
        if (_dispatcherQueue != null && !_dispatcherQueue.HasThreadAccess)
        {
            _dispatcherQueue.TryEnqueue(() => _ = ApplySearchAsync(throttle: false));
        }
        else
        {
            _ = ApplySearchAsync(throttle: false);
        }
    }

    public IAsyncRelayCommand<string> IndexFolderCommand => new AsyncRelayCommand<string>(IndexFolderAsync);

    partial void OnSearchQueryChanged(string value)
    {
        _ = ApplySearchAsync(throttle: true);
        UpdateSuggestions();
    }

    partial void OnSortFieldChanged(GallerySortField value)
    {
        ApplySortToView();
        ApplySortOnly();
    }

    partial void OnSortDirectionChanged(GallerySortDirection value)
    {
        ApplySortToView();
        ApplySortOnly();
    }

    private void ApplySortToView()
    {
        if (ImagesView == null) return;
        
        try
        {
            ImagesView.SortDescriptions.Clear();
            var dir = _sortDirection == GallerySortDirection.Asc ? CommunityToolkit.WinUI.Collections.SortDirection.Ascending : CommunityToolkit.WinUI.Collections.SortDirection.Descending;
            switch (_sortField)
            {
                case GallerySortField.Date:
                    ImagesView.SortDescriptions.Add(new CommunityToolkit.WinUI.Collections.SortDescription(nameof(ImageMetadata.LastWriteTimeTicks), dir));
                    break;
                default:
                    ImagesView.SortDescriptions.Add(new CommunityToolkit.WinUI.Collections.SortDescription(nameof(ImageMetadata.FilePath), dir));
                    break;
            }
            ImagesView.RefreshSorting();
        }
        catch
        {
            // UI 컬렉션 조작 중 예외 무시
        }
    }

    public async Task IndexFolderAsync(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        if (IsIndexing) return;
        IsIndexing = true;
        try
        {
            await _indexService.IndexFolderAsync(folder);
            await ApplySearchAsync();
            
            // 인덱싱 완료 후 컬렉션 새로고침 (스크롤 위치 보존을 위해 이벤트 발생)
            if (_dispatcherQueue != null)
            {
                _dispatcherQueue.TryEnqueue(DispatcherQueuePriority.High, () =>
                {
                    try
                    {
                        // 새로고침 전 이벤트 발생 (GalleryPage가 스크롤 위치 저장)
                        BeforeCollectionRefresh?.Invoke(this, EventArgs.Empty);
                        
                        // Images 컬렉션 강제 갱신 (x:Bind 업데이트 트리거)
                        var currentItems = Images.ToList();
                        Images.Clear();
                        
                        // 잠시 대기 후 복원 (UI 스레드가 Clear를 처리할 시간 제공)
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(10);
                            _dispatcherQueue.TryEnqueue(() =>
                            {
                                foreach (var item in currentItems)
                                {
                                    Images.Add(item);
                                }
                                ImagesView.RefreshSorting();
                                ImagesChanged?.Invoke(this, EventArgs.Empty);
                                
                                // 새로고침 후 이벤트 발생 (GalleryPage가 스크롤 위치 복원)
                                _ = Task.Delay(50).ContinueWith(_ =>
                                {
                                    _dispatcherQueue.TryEnqueue(() => 
                                        AfterCollectionRefresh?.Invoke(this, EventArgs.Empty));
                                });
                            });
                        });
                    }
                    catch { }
                });
            }
        }
        finally
        {
            IsIndexing = false;
        }
    }

    private IEnumerable<ImageMetadata> Sort(IEnumerable<ImageMetadata> src)
    {
        return _sortField switch
        {
            GallerySortField.Date => _sortDirection == GallerySortDirection.Asc ?
                src.OrderBy(m => m.LastWriteTimeTicks ?? 0L) :
                src.OrderByDescending(m => m.LastWriteTimeTicks ?? 0L),
            _ => _sortDirection == GallerySortDirection.Asc ?
                src.OrderBy(m => m.FilePath, StringComparer.OrdinalIgnoreCase) :
                src.OrderByDescending(m => m.FilePath, StringComparer.OrdinalIgnoreCase)
        };
    }

    private void ApplySortOnly()
    {
        if (_lastSearch.Count == 0) return;
        
        try
        {
            var sorted = Sort(_lastSearch).ToList();
            Images.Clear();
            foreach (var m in sorted) Images.Add(m);
            ImagesView.RefreshSorting();
            ImagesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // UI 컬렉션 조작 중 예외 무시
        }
    }

    private async Task ApplySearchAsync(bool throttle = false)
    {
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        try
        {
            if (throttle)
                await Task.Delay(250, cts.Token);
            var results = _indexService.Search(_searchQuery, _searchAndMode, _searchPartialMode).ToList();
            _lastSearch = results;
            if (cts.IsCancellationRequested) return;
            var sorted = Sort(results).ToList();
            
            // UI 스레드에서 컬렉션 업데이트
            void UpdateAction()
            {
                try
                {
                    // 점진적 업데이트로 UI 응답성 향상
                    if (Images.Count == 0 || Math.Abs(Images.Count - sorted.Count) > 100)
                    {
                        // 큰 변경 시 일괄 교체
                        Images.Clear();
                        foreach (var m in sorted) Images.Add(m);
                    }
                    else
                    {
                        // 작은 변경 시 점진적 업데이트
                        UpdateCollectionIncrementally(sorted);
                    }
                    
                    ImagesView.RefreshSorting();
                    ImagesChanged?.Invoke(this, EventArgs.Empty);
                }
                catch
                {
                    // UI 컬렉션 조작 중 예외 무시
                }
            }
            
            if (_dispatcherQueue != null && !_dispatcherQueue.HasThreadAccess)
            {
                _dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, UpdateAction);
            }
            else
            {
                UpdateAction();
            }
        }
        catch (TaskCanceledException) { }
        catch
        {
            // 기타 예외 무시
        }
    }
    
    private void UpdateCollectionIncrementally(List<ImageMetadata> newItems)
    {
        var currentPaths = new HashSet<string>(Images.Select(m => m.FilePath));
        var newPaths = new HashSet<string>(newItems.Select(m => m.FilePath));
        
        // 제거할 항목
        for (int i = Images.Count - 1; i >= 0; i--)
        {
            if (!newPaths.Contains(Images[i].FilePath))
                Images.RemoveAt(i);
        }
        
        // 추가할 항목
        foreach (var item in newItems)
        {
            if (!currentPaths.Contains(item.FilePath))
                Images.Add(item);
        }
    }

    private void UpdateSuggestions()
    {
        try
        {
            TagSuggestions.Clear();
            if (string.IsNullOrWhiteSpace(_searchQuery)) return;
            // Use last token for suggestion
            var parts = _searchQuery.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var prefix = parts.Length == 0 ? _searchQuery : parts[^1];
            foreach (var s in _indexService.SuggestTags(prefix)) TagSuggestions.Add(s);
        }
        catch
        {
            // 제안 업데이트 중 예외 무시
        }
    }
}
