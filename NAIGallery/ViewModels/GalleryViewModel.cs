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

namespace NAIGallery.ViewModels;

public enum GallerySortField { Name, Date }
public enum GallerySortDirection { Asc, Desc }

/// <summary>
/// ViewModel for the gallery page using CommunityToolkit.Mvvm to reduce boilerplate.
/// </summary>
public partial class GalleryViewModel : ObservableObject
{
    private readonly IImageIndexService _indexService;

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

    public GalleryViewModel(IImageIndexService service)
    {
        _indexService = service;
        ImagesView = new AdvancedCollectionView(Images, false);
        ApplySortToView();
        _ = ApplySearchAsync();
    }

    public IAsyncRelayCommand<string> IndexFolderCommand => new AsyncRelayCommand<string>(IndexFolderAsync);

    partial void OnSearchQueryChanged(string value)
    {
        _ = ApplySearchAsync(throttle: true);
        UpdateSuggestions();
    }

    partial void OnSortFieldChanged(GallerySortField value) => ApplySortToView();
    partial void OnSortDirectionChanged(GallerySortDirection value) => ApplySortToView();

    private void ApplySortToView()
    {
        if (ImagesView == null) return;
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

    public async Task IndexFolderAsync(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        if (IsIndexing) return;
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
        var sorted = Sort(_lastSearch).ToList();
        Images.Clear();
        foreach (var m in sorted) Images.Add(m);
        ImagesView.RefreshSorting();
        ImagesChanged?.Invoke(this, EventArgs.Empty);
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
            Images.Clear();
            foreach (var m in sorted) Images.Add(m);
            ImagesView.RefreshSorting();
            ImagesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (TaskCanceledException) { }
    }

    private void UpdateSuggestions()
    {
        TagSuggestions.Clear();
        if (string.IsNullOrWhiteSpace(_searchQuery)) return;
        // Use last token for suggestion
        var parts = _searchQuery.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var prefix = parts.Length == 0 ? _searchQuery : parts[^1];
        foreach (var s in _indexService.SuggestTags(prefix)) TagSuggestions.Add(s);
    }
}
