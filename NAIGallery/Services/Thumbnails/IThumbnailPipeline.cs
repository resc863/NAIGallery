using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using NAIGallery.Models;

namespace NAIGallery.Services;

/// <summary>
/// Encapsulates thumbnail decode, caching, UI apply batching & related controls.
/// Internal to service layer; ImageIndexService delegates to this.
/// </summary>
internal interface IThumbnailPipeline
{
    void InitializeDispatcher(DispatcherQueue dispatcherQueue);
    Task EnsureThumbnailAsync(ImageMetadata meta, int decodeWidth, CancellationToken ct, bool allowDownscale);
    Task PreloadAsync(IEnumerable<ImageMetadata> items, int decodeWidth, CancellationToken ct, int maxParallelism);
    void SetApplySuspended(bool suspended);
    void FlushApplyQueue();
    void DrainVisible(HashSet<ImageMetadata> visible);
    void ClearCache();
    int CacheCapacity { get; set; }

    // New scheduling APIs (integrated former ThumbnailSchedulerService logic)
    void Schedule(ImageMetadata meta, int width, bool highPriority = false);
    void BoostVisible(IEnumerable<ImageMetadata> metas, int width);
    void UpdateViewport(IReadOnlyList<ImageMetadata> orderedVisible, IReadOnlyList<ImageMetadata> bufferItems, int width);
    
    /// <summary>
    /// Clears pending/inflight tracking state to allow re-scheduling of previously attempted items.
    /// Call this when user explicitly requests a refresh.
    /// </summary>
    void ResetPendingState();
    
    /// <summary>
    /// Raised when a thumbnail is successfully applied to an ImageMetadata.
    /// Subscribers can use this to force UI refresh for virtualized containers.
    /// </summary>
    event Action<ImageMetadata>? ThumbnailApplied;
}
