# Folder: Services/Thumbnails

## Purpose
- Progressive thumbnail decoding, caching, and UI application for images.

## Files

### Interface
- `IThumbnailPipeline`: interface for scheduling and cache control.

### ThumbnailPipeline (Single File - Consolidated)

The pipeline is consolidated into a single file for simplicity:

| File | Contents | ~Lines |
|------|----------|--------|
| `ThumbnailPipeline.cs` | All pipeline logic: fields, events, decoding, apply queue, workers, public API | 350 |

**Key sections within the file:**
- Fields and initialization
- Worker loop and queue management
- Image decoding with `BitmapDecoder`
- UI apply queue with `DispatcherQueue`
- Public scheduling API

### Supporting Classes
- `ThumbnailCache`: LRU cache for decoded pixel data with `ArrayPool<byte>`.
- `PixelData`: Cached pixel data container (Width, Height, Pixels, ByteCount).

## Key Flows

### EnsureThumbnailAsync(meta, width, ct, allowDownscale)
- Ensures a thumbnail at least `width` is available.
- May stage a smaller priming decode for progressive loading.
- Uses cache key: `{filePath}|{lastWriteTicks}|{width}`.

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
2. Add to processing set ¡æ prevent duplicates
3. Acquire decode gate semaphore
4. Decode via `BitmapDecoder` + `SoftwareBitmap`
5. Copy to `ArrayPool<byte>` buffer
6. Add to cache
7. Enqueue for UI apply

### UI Apply
- Batched application via `DispatcherQueue.TryEnqueue`
- Creates `WriteableBitmap` and assigns to `meta.Thumbnail`
- Fires `ThumbnailApplied` event

### Concurrency Controls
- `_decodeGate`: SemaphoreSlim limiting parallel decoding (CPU count based, max 8)
- `_applyGate`: Limits concurrent WriteableBitmap creation (4 concurrent)
- Worker count adapts to queue backlog (2-8 workers)

### Queue Structure
- `_highQueue`: ConcurrentQueue for high priority requests (visible items)
- `_normalQueue`: ConcurrentQueue for normal priority requests (buffer items)
- `_applyQueue`: ConcurrentQueue for UI thread apply operations

### Memory Management
- LRU cache with configurable capacity
- Uses `ArrayPool<byte>.Shared` for pixel buffers
- Automatic eviction when capacity exceeded

## Tuning
- Capacity via `CacheCapacity` (bytes); service exposes `ThumbnailCacheCapacity`.
- Adjust target decode width from views based on tile size and `RasterizationScale`.
- `AppDefaults` contains threshold constants.