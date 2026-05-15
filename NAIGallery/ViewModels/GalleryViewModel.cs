using System.Collections.ObjectModel;
using System.Threading.Tasks;
using NAIGallery.Models;
using NAIGallery.Services;
using System.Linq;
using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.UI.Dispatching;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace NAIGallery.ViewModels;

public enum GallerySortField { Name, Date }
public enum GallerySortDirection { Asc, Desc }

/// <summary>
/// ViewModel for the gallery page.
/// </summary>
public sealed class GalleryViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IImageIndexService _indexService;
    private readonly AsyncCommand<string> _indexFolderCommand;
    private DispatcherQueue? _dispatcherQueue;

    private string _searchQuery = string.Empty;
    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
            {
                _ = ApplySearchAsync(throttle: true);
                UpdateSuggestions();
            }
        }
    }

    private GallerySortField _sortField = GallerySortField.Name;
    public GallerySortField SortField
    {
        get => _sortField;
        set
        {
            if (SetProperty(ref _sortField, value))
                ApplySortOnly();
        }
    }

    private GallerySortDirection _sortDirection = GallerySortDirection.Asc;
    public GallerySortDirection SortDirection
    {
        get => _sortDirection;
        set
        {
            if (SetProperty(ref _sortDirection, value))
                ApplySortOnly();
        }
    }

    private bool _isIndexing;
    public bool IsIndexing
    {
        get => _isIndexing;
        private set
        {
            if (SetProperty(ref _isIndexing, value) && !value)
                UpdateSuggestions();
        }
    }

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

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? ImagesChanged;
    public event EventHandler? BeforeCollectionRefresh;
    public event EventHandler? AfterCollectionRefresh;

    public GalleryViewModel(IImageIndexService service)
    {
        _indexService = service;
        _indexFolderCommand = new AsyncCommand<string>(IndexFolderAsync);
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

    private Task RunOnDispatcherDeferredAsync(Action action, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);

        if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                tcs.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                action();
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }))
        {
            tcs.TrySetCanceled(cancellationToken);
        }

        return tcs.Task;
    }

    public ICommand IndexFolderCommand => _indexFolderCommand;

    public async Task IndexFolderAsync(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || IsIndexing) return;
        
        IsIndexing = true;
        var completed = false;
        try
        {
            await _indexService.IndexFolderAsync(folder);
            await ApplySearchAsync();
            completed = true;
        }
        finally
        {
            IsIndexing = false;

            if (completed)
            {
                // 인덱싱 완료 후 ImagesChanged 이벤트 발생
                RunOnDispatcher(() => ImagesChanged?.Invoke(this, EventArgs.Empty));
            }
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
            
            await RunOnDispatcherDeferredAsync(() => UpdateImagesCollection(sorted), cts.Token);
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

    public void Dispose()
    {
        _indexService.IndexChanged -= OnIndexChanged;
        var cts = Interlocked.Exchange(ref _searchCts, null);
        cts?.Cancel();
        cts?.Dispose();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
