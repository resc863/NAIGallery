Folder: Services/Thumbnails

Purpose
- Progressive thumbnail decoding, caching, and UI application for images.

Files
- `IThumbnailPipeline`: interface for scheduling and cache control.
- `ThumbnailPipeline`: concrete implementation with LRU cache, deduplicated scheduling (high/normal), concurrency gates, and UI batching.

Key flows
- `EnsureThumbnailAsync(meta, width, ct, allowDownscale)`: ensures a thumbnail at least `width` is available; may stage a smaller priming decode.
- `PreloadAsync(items, width, ct, maxParallelism)`: background preload for upcoming items.
- Scheduling API: `Schedule`, `BoostVisible`, `UpdateViewport` used by gallery to prioritize visible items.

Implementation notes
- Decoding uses `BitmapDecoder` + `SoftwareBitmap` -> `WriteableBitmap` on UI thread.
- Cache stores raw BGRA pixel bytes using `ArrayPool<byte>`; key includes file last-write ticks and requested width to invalidate on file change.
- Concurrency: `_decodeGate` limits parallel decoding; `_uiGate` serializes `WriteableBitmap` creation; worker pool size adapts to backlog.

Tuning
- Capacity via `CacheCapacity` (bytes); service exposes `ThumbnailCacheCapacity`.
- Adjust target decode width from views based on tile size and `RasterizationScale`.