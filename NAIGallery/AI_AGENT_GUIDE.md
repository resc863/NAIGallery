AI Agent Guide - NAIGallery (root)

Overview
- NAIGallery is a WinUI 3 desktop app (.NET 10, Windows App SDK) that indexes PNG images, extracts prompts/parameters, builds a token-based search index, and renders a virtualized gallery with progressive thumbnails and connected animations.
- Core layers:
  - Models: shared data contracts bound to UI.
  - Services: image indexing, metadata extraction, search, tags, thumbnails.
  - ViewModels: MVVM state and commands for pages.
  - Views: XAML pages and partial code-behind for interaction, layout, animations, and navigation.
  - Controls/Converters: small UI helpers.

Key entry points
- `App.xaml.cs`: wires DI and logging; registers `IMetadataExtractor` and `IImageIndexService`. Applies persisted settings such as thumbnail cache capacity.
- `MainWindow.xaml.cs`: hosts navigation and cancels connected animations when switching sections.
- `Views/GalleryPage*`: virtualized image grid with viewport-aware thumbnail scheduling and smooth zoom/CA.
- `Views/ImageDetailPage`: shows a single image with connected animation, zoom/pan, and metadata.

Threading model
- Background: indexing, decoding, file IO, search indexing, and scheduling use TPL and channels/semaphores.
- UI: all XAML-bound updates (image sources, layout invalidation, animations) are marshaled via `DispatcherQueue`.

Extensibility
- Metadata formats: add a new `IMetadataExtractor` implementation and register it in `App` composite.
- Thumbnails: use `IImageIndexService` to schedule/preload; pipeline supports progressive upgrade and cache tuning.
- Search: extend tokenization if you add new fields to `ImageMetadata`.

Performance notes
- Thumbnails: LRU cache backed by `ArrayPool<byte>`, decode gate limits concurrency, and UI gate serializes `WriteableBitmap` creation.
- Indexing: multi-consumer channel and coarse-grained progress throttling.

Conventions
- Case-insensitive text processing for tokens/tags.
- Avoid blocking the UI thread; always use the service abstractions.

Common gotchas
- Initialize the dispatcher once per process: `IImageIndexService.InitializeDispatcher(DispatcherQueue.GetForCurrentThread())`.
- When navigating, cancel connected animations before switching sections to avoid visual artifacts.
- Use `allowDownscale: true` only where a temporary smaller thumbnail is acceptable (e.g., detail page warm-up).