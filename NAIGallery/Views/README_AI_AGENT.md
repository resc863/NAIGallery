# Folder: Views

## Purpose
- XAML pages and code-behind responsible for gallery grid, detail page, settings, and related UI behaviors.

## Structure

### GalleryPage - Partial Class (8 files)

The gallery page is split into multiple files by responsibility:

| File | Responsibility |
|------|---------------|
| `GalleryPage.xaml` | XAML layout with ItemsRepeater, search box, sort controls |
| `GalleryPage.xaml.cs` | Main entry, constructor, dependency properties, event handlers, folder selection |
| `GalleryPage.Layout.cs` | `EnqueueVisibleStrict`, `EnqueueFromRealized`, viewport range calculation, item scheduling |
| `GalleryPage.Thumbnails.cs` | `GetDesiredDecodeWidth`, `EnqueueMeta`, `OnThumbnailApplied` - decode width and pipeline interaction |
| `GalleryPage.Interaction.cs` | Hover/tap handlers, zoom helpers, visual tree utilities |
| `GalleryPage.Animations.cs` | Implicit offset animations, zoom scale effects, composition setup |
| `GalleryPage.Navigation.cs` | Connected animations (forward/back), path management between pages |
| `GalleryPage.Infrastructure.cs` | Scroll handling, `HookScroll`, realization helpers, idle fill |
| `GalleryPage.ZoomPrime.cs` | `PrimeInitialAsync`, preload logic, zoom-prime coordination |

### ImageDetailPage
- Single file: `ImageDetailPage.xaml.cs` (~520 lines)
- Organized by regions: Fields, Constructor, Lifecycle, Image Loading, Metadata, Zoom & Pan, Connected Animation, Navigation, Splitter, Helpers
- Shows selected image with full resolution, metadata panel, zoom/pan controls
- Connected animation target for smooth transitions

### SettingsPage
- Controls cache size and reindexing options.
- Simple settings UI.

## Key Patterns

### Thumbnail Loading Flow
1. `GalleryPage` calculates visible viewport indices
2. Calls `EnqueueVisibleStrict()` to schedule visible items
3. `GetDesiredDecodeWidth()` computes pixel width based on tile size and `RasterizationScale`
4. `EnqueueMeta()` delegates to `ImageIndexService.Schedule()`
5. `OnThumbnailApplied()` handles pipeline callback to update container images

### Connected Animation Flow
1. Gallery: `PrepareToAnimate("ForwardConnectedAnimation", element)` on navigation
2. Detail: `RequestStartForwardConnectedAnimation()` after image loads
3. Back: `PrepareToAnimate("BackConnectedAnimation", DetailImage)` ¡æ gallery receives

### Viewport Tracking
- `_viewStartIndex`, `_viewEndIndex` track visible item range
- `_scrollingUp` flag adjusts priority direction
- `UpdateSchedulerViewport()` informs pipeline of current viewport

## Guidance
- Avoid long-running work on the UI thread; call into services for IO/decoding.
- When returning to gallery, resume pipeline and flush apply queue so blanks fill quickly.
- Use `DispatcherQueue.TryEnqueue()` for UI updates from background threads.
- Keep file sizes manageable (~200-400 lines) by using partial classes.