using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using NAIGallery.Models;
using NAIGallery.Services;
using System.Linq;
using System;
using System.Collections.Generic;
using System.Threading;

namespace NAIGallery.ViewModels;

/// <summary>
/// Fields available for sorting the gallery list.
/// </summary>
public enum GallerySortField { Name, Date }

/// <summary>
/// Sort direction used by the gallery list.
/// </summary>
public enum GallerySortDirection { Asc, Desc }

/// <summary>
/// ViewModel for the gallery page: exposes indexed <see cref="ImageMetadata"/> items,
/// search, sorting and indexing orchestration.
/// </summary>
public class GalleryViewModel : INotifyPropertyChanged
{
    private readonly ImageIndexService _indexService;
    private string _searchQuery = string.Empty;
    private bool _isIndexing;
    private GallerySortField _sortField = GallerySortField.Name;
    private GallerySortDirection _sortDirection = GallerySortDirection.Asc;

    private CancellationTokenSource? _searchCts; // for throttling
    private List<ImageMetadata> _lastSearchResult = new(); // cache results before sorting

    /// <summary>
    /// Sequence bound to the UI with the current search+sort view.
    /// </summary>
    public ObservableCollection<ImageMetadata> Images { get; } = new();

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Creates a new view model bound to the provided index service.
    /// </summary>
    public GalleryViewModel(ImageIndexService service)
    {
        _indexService = service;
        _ = ApplySearchAsync();
    }

    /// <summary>Current search query. Setting triggers a debounced search.</summary>
    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (_searchQuery != value)
            {
                _searchQuery = value;
                OnPropertyChanged();
                _ = ApplySearchAsync(throttle:true);
            }
        }
    }

    /// <summary>Active sort field.</summary>
    public GallerySortField SortField
    {
        get => _sortField;
        set { if (_sortField != value) { _sortField = value; OnPropertyChanged(); ApplySortOnly(); } }
    }

    /// <summary>Active sort direction.</summary>
    public GallerySortDirection SortDirection
    {
        get => _sortDirection;
        set { if (_sortDirection != value) { _sortDirection = value; OnPropertyChanged(); ApplySortOnly(); } }
    }

    /// <summary>True while indexing is in progress.</summary>
    public bool IsIndexing
    {
        get => _isIndexing;
        private set { if (_isIndexing != value) { _isIndexing = value; OnPropertyChanged(); } }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>
    /// Indexes a folder recursively and then refreshes the current search results.
    /// </summary>
    public async Task IndexFolderAsync(string folder)
    {
        IsIndexing = true;
        try
        {
            await _indexService.IndexFolderAsync(folder);
            await ApplySearchAsync();
        }
        finally
        {
            IsIndexing = false;
        }
    }

    /// <summary>
    /// Applies current sort settings to an enumerable.
    /// </summary>
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

    /// <summary>
    /// Re-applies sorting to the last search results and updates the bound collection.
    /// </summary>
    private void ApplySortOnly()
    {
        if (_lastSearchResult.Count == 0) return;
        var sorted = Sort(_lastSearchResult).ToList();
        Images.Clear();
        foreach (var m in sorted) Images.Add(m);
        ImagesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Raised whenever <see cref="Images"/> is refreshed in bulk (for view coordination).
    /// </summary>
    public event EventHandler? ImagesChanged;

    /// <summary>
    /// Executes a search via the service and updates the bound collection.
    /// When throttled, introduces a short delay for typing debounce.
    /// </summary>
    private async Task ApplySearchAsync(bool throttle=false)
    {
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        try
        {
            if (throttle)
                await Task.Delay(250, cts.Token); // debounce typing
            var results = _indexService.SearchByTag(_searchQuery).ToList();
            _lastSearchResult = results;
            if (cts.IsCancellationRequested) return;
            var sorted = Sort(results).ToList();
            Images.Clear();
            foreach (var m in sorted) Images.Add(m);
            ImagesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (TaskCanceledException) { }
    }
}
