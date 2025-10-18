Folder: ViewModels

Purpose
- MVVM layer that exposes bindable state and commands for views.

Files
- `GalleryViewModel`: search, sorting, and indexing command; maintains observable collection of `ImageMetadata`.

Notes
- Uses CommunityToolkit.Mvvm source generators (`[ObservableProperty]`).
- Debounced search applies filters and reorders images; view listens via `ImagesChanged`.