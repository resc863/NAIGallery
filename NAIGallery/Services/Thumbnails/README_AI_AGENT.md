# Folder: Services/Thumbnails

## Purpose
- Progressive thumbnail decoding, caching, and UI application for images.

## Files

### Interface
- `IThumbnailPipeline`: interface for scheduling and cache control.

### ThumbnailPipeline - Partial Class (4 files)

The pipeline is split into multiple files for maintainability:

| File | Purpose | ~Lines |
|------|---------|--------|
| `ThumbnailPipeline.cs` | Fields, events, constructor, initialization, public API (Schedule, BoostVisible, UpdateViewport, EnsureThumbnailAsync, PreloadAsync) | 280 |
| `ThumbnailPipeline.Decoding.cs` | `LoadDecodeAsync`, `DecodeAsync`, COM error handling, BitmapDecoder usage | 200 |
| `ThumbnailPipeline.Apply.cs` | `EnqueueApply`, `ScheduleDrain`, `TryApplyFromEntryAsync` - WriteableBitmap creation on UI thread | 160 |
| `ThumbnailPipeline.Workers.cs` | `WorkerLoopAsync`, `EnsureWorkers`, `UpdateWorkerTarget`, `CheckMemoryPressure` | 130 |

### Supporting Classes
- `ThumbnailCache`: LRU cache for decoded pixel data with `ArrayPool<byte>`.
- `PixelEntry`: Cached pixel data container (W, H, Pixels, Rented).
- `ThumbnailDecoder`: (if present) Additional decoding utilities.
- `DedicatedThreadPool`: (if present) Custom thread pool for decoding.

## Key Flows

### EnsureThumbnailAsync(meta, width, ct, allowDownscale)
- Ensures a thumbnail at least `width` is available.
- May stage a smaller priming decode for progressive loading.
- Uses cache key: `{filePath}|{lastWriteTicks}|w{width}`.

### PreloadAsync(items, width, ct, maxParallelism)
- Background preload for upcoming items.
- Respects memory pressure and UI busy state.

### Scheduling API
- `Schedule(meta, width, highPriority)`: Add to queue.
- `BoostVisible(metas, width)`: Prioritize visible items.
- `UpdateViewport(orderedVisible, bufferItems, width)`: Update with new epoch.

## Implementation Notes

### Decoding Pipeline
1. Check cache ¡æ return if hit
2. Add to inflight set ¡æ prevent duplicates
3. Decode via `BitmapDecoder` + `SoftwareBitmap`
4. Copy to `ArrayPool<byte>` buffer
5. Add to cache
6. Enqueue for UI apply

### UI Apply
- Batched application via `DispatcherQueue.TryEnqueue`
- Creates `WriteableBitmap` and assigns to `meta.Thumbnail`
- Fires `ThumbnailApplied` event

### Concurrency Controls
- `_decodeGate`: SemaphoreSlim limiting parallel decoding (CPU count based)
- `_bitmapCreationGate`: Limits concurrent WriteableBitmap creation
- Worker count adapts to backlog and memory pressure

### Memory Management
- `CheckMemoryPressure()`: Monitors GC memory info
- Reduces cache capacity and worker count under pressure
- Uses `ArrayPool<byte>.Shared` for pixel buffers

## Tuning
- Capacity via `CacheCapacity` (bytes); service exposes `ThumbnailCacheCapacity`.
- Adjust target decode width from views based on tile size and `RasterizationScale`.
- `AppDefaults` contains threshold constants.