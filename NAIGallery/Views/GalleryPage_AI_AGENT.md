GalleryPage notes for AI agents

Responsibilities
- Display a grid of images with virtualization and connected animations.
- Schedule thumbnails according to viewport priority and current zoom.

Key helpers
- `GetDesiredDecodeWidth()`: computes decode width using `_baseItemSize` and `RasterizationScale`.
- `EnqueueVisibleStrict()` and `InternalEnqueueFromRealized()`: identify visible items and push decode requests with priority, updating `_viewStartIndex/_viewEndIndex`.
- `BoostCurrentVisible()`: boosts scheduling priority for currently visible items.
- Navigation and animations are in `Navigation.cs` and `Animations.cs` parts.

When editing
- Avoid accessing `XamlRoot` or visual tree off the UI thread; methods already guard with `DispatcherQueue` checks.
- Prefer calling `ImageIndexService.Schedule/BoostVisible/UpdateViewport` instead of directly touching the pipeline.