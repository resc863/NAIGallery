# Folder: ViewModels

## Purpose
- MVVM layer that exposes bindable state and commands for views.

## Files

### GalleryViewModel
Main ViewModel for the gallery page providing search, sorting, and indexing functionality.

**Observable Properties:**
- `SearchQuery`: Current search text
- `SortField`: Sort by `Name` or `Date` (enum `GallerySortField`)
- `SortDirection`: `Asc` or `Desc` (enum `GallerySortDirection`)
- `IsIndexing`: Indicates folder indexing in progress
- `SearchAndMode`: AND vs OR search logic
- `SearchPartialMode`: Partial vs exact token matching

**Collections:**
- `Images`: ObservableCollection of `ImageMetadata`
- `TagSuggestions`: ObservableCollection for autocomplete

**Events:**
- `ImagesChanged`: Fired when image list changes
- `BeforeCollectionRefresh`: Before collection update (for scroll position save)
- `AfterCollectionRefresh`: After collection update (for scroll position restore)

**Commands:**
- `IndexFolderCommand`: small in-repo async `ICommand` implementation to avoid CommunityToolkit in NativeAOT builds

**Key methods:**
- `SetDispatcherQueue(DispatcherQueue)`: Must be called from UI thread
- `IndexFolderAsync(string)`: Triggers folder indexing

## Notes
- Uses manual `INotifyPropertyChanged` and `SetProperty` so the ViewModel has no MVVM Toolkit dependency in NativeAOT builds.
- Debounced search (250ms) applies filters and reorders images.
- Sort changes reorder `Images` through the ViewModel's in-memory result list.
- Uses `DispatcherQueue.TryEnqueue()` for UI-safe updates from background threads.
