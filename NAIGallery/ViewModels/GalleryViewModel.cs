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
using Microsoft.UI.Dispatching;

namespace NAIGallery.ViewModels;

public enum GallerySortField { Name, Date }
public enum GallerySortDirection { Asc, Desc }

/// <summary>
/// ViewModel for the gallery page using CommunityToolkit.Mvvm to reduce boilerplate.
/// </summary>
public partial class GalleryViewModel : ObservableObject, IDisposable
{
    private readonly IImageIndexService _indexService;
    private readonly IAsyncRelayCommand<string> _indexFolderCommand;
    private DispatcherQueue? _dispatcherQueue;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private GallerySortField _sortField = GallerySortField.Name;

    [ObservableProperty]
    private GallerySortDirection _sortDirection = GallerySortDirection.Asc;

    [ObservableProperty]
    private bool _isIndexing;

    private bool _searchAndMode;
    public bool SearchAndMode
    {
        get => _searchAndMode;
        set
        {
            if (SetProperty(ref _searchAndMode, value))
                _ = ApplySearchAsync();
        }
    }

    private bool _searchPartialMode = true;
    public bool SearchPartialMode
    {
        get => _searchPartialMode;
        set
        {
            if (SetProperty(ref _searchPartialMode, value))
                _ = ApplySearchAsync();
        }
    }

    private CancellationTokenSource? _searchCts;
    private List<ImageMetadata> _lastSearch = [];

    public ObservableCollection<ImageMetadata> Images { get; } = [];
    public ObservableCollection<string> TagSuggestions { get; } = [];

    public event EventHandler? ImagesChanged;
    public event EventHandler? BeforeCollectionRefresh;
    public event EventHandler? AfterCollectionRefresh;

    public GalleryViewModel(IImageIndexService service)
    {
        _indexService = service;
        _indexFolderCommand = new AsyncRelayCommand<string>(IndexFolderAsync);
        LoadSettings();
        
        _indexService.IndexChanged += OnIndexChanged;
    }

    /// <summary>
    /// DispatcherQueue를 외부에서 설정합니다. Page에서 호출해야 합니다.
    /// </summary>
    public void SetDispatcherQueue(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
    }

    private void OnIndexChanged(object? sender, EventArgs e)
    {
        RunOnDispatcher(() => _ = ApplySearchAsync(throttle: false));
    }

    private void RunOnDispatcher(Action action)
    {
        if (_dispatcherQueue is null)
        {
            // DispatcherQueue가 없으면 직접 실행 (UI 스레드에서 호출된 것으로 가정)
            action();
            return;
        }
        
        if (_dispatcherQueue.HasThreadAccess)
            action();
        else
            _dispatcherQueue.TryEnqueue(() => action());
    }

    /// <summary>
    /// Enqueues an action with Low priority to avoid layout conflicts.
    /// </summary>
    private void RunOnDispatcherDeferred(Action action)
    {
        if (_dispatcherQueue is null)
        {
            action();
            return;
        }
        
        _dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => action());
    }

    public IAsyncRelayCommand<string> IndexFolderCommand => _indexFolderCommand;

    partial void OnSearchQueryChanged(string value)
    {
        _ = ApplySearchAsync(throttle: true);
        UpdateSuggestions();
    }

    partial void OnSortFieldChanged(GallerySortField value)
    {
        ApplySortOnly();
    }

    partial void OnSortDirectionChanged(GallerySortDirection value)
    {
        ApplySortOnly();
    }

    public async Task IndexFolderAsync(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || IsIndexing) return;
        
        IsIndexing = true;
        try
        {
            await _indexService.IndexFolderAsync(folder);
            await ApplySearchAsync();
            
            // 인덱싱 완료 후 ImagesChanged 이벤트 발생
            RunOnDispatcher(() => ImagesChanged?.Invoke(this, EventArgs.Empty));
        }
        finally
        {
            IsIndexing = false;
        }
    }

    private IEnumerable<ImageMetadata> Sort(IEnumerable<ImageMetadata> source)
    {
        return _sortField switch
        {
            GallerySortField.Date => _sortDirection == GallerySortDirection.Asc
                ? source.OrderBy(m => m.LastWriteTimeTicks ?? 0L)
                : source.OrderByDescending(m => m.LastWriteTimeTicks ?? 0L),
            _ => _sortDirection == GallerySortDirection.Asc
                ? source.OrderBy(m => m.FilePath, StringComparer.OrdinalIgnoreCase)
                : source.OrderByDescending(m => m.FilePath, StringComparer.OrdinalIgnoreCase)
        };
    }

    private void ApplySortOnly()
    {
        if (_lastSearch.Count == 0) return;
        
        RunOnDispatcherDeferred(() =>
        {
            try
            {
                BeforeCollectionRefresh?.Invoke(this, EventArgs.Empty);
                
                var sorted = Sort(_lastSearch).ToList();
                ReplaceImages(sorted);
                
                AfterCollectionRefresh?.Invoke(this, EventArgs.Empty);
                ImagesChanged?.Invoke(this, EventArgs.Empty);
            }
            catch { /* UI collection manipulation exception */ }
        });
    }

    private async Task ApplySearchAsync(bool throttle = false)
    {
        // 기존 CTS 취소 및 새 CTS 생성
        var cts = new CancellationTokenSource();
        var oldCts = Interlocked.Exchange(ref _searchCts, cts);
        oldCts?.Cancel();
        oldCts?.Dispose();
        
        try
        {
            if (throttle)
                await Task.Delay(250, cts.Token);
            
            if (cts.IsCancellationRequested) return;
            
            var results = _indexService.Search(_searchQuery, _searchAndMode, _searchPartialMode).ToList();
            _lastSearch = results;
            
            if (cts.IsCancellationRequested) return;
            
            var sorted = Sort(results).ToList();
            
            RunOnDispatcherDeferred(() => UpdateImagesCollection(sorted));
        }
        catch (OperationCanceledException) { }
    }

    private void UpdateImagesCollection(List<ImageMetadata> sorted)
    {
        try
        {
            BeforeCollectionRefresh?.Invoke(this, EventArgs.Empty);
            
            ReplaceImages(sorted);
            
            AfterCollectionRefresh?.Invoke(this, EventArgs.Empty);
            ImagesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch { /* UI collection manipulation exception */ }
    }

    private void UpdateSuggestions()
    {
        try
        {
            TagSuggestions.Clear();
            if (string.IsNullOrWhiteSpace(_searchQuery)) return;
            
            var parts = _searchQuery.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var prefix = parts.Length == 0 ? _searchQuery : parts[^1];
            
            foreach (var s in _indexService.SuggestTags(prefix)) 
                TagSuggestions.Add(s);
        }
        catch { /* Suggestion update exception */ }
    }

    private void ReplaceImages(IEnumerable<ImageMetadata> items)
    {
        Images.Clear();
        foreach (var item in items)
            Images.Add(item);
    }

    private void LoadSettings()
    {
        try
        {
            var settings = AppSettings.Load();
            _searchAndMode = settings.SearchAndMode;
            _searchPartialMode = settings.SearchPartialMode;
        }
        catch { }
    }

    partial void OnIsIndexingChanged(bool value)
    {
        if (!value)
            UpdateSuggestions();
    }

    public void Dispose()
    {
        _indexService.IndexChanged -= OnIndexChanged;
        var cts = Interlocked.Exchange(ref _searchCts, null);
        cts?.Cancel();
        cts?.Dispose();
    }
}
