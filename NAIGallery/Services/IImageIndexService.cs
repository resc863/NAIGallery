using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using NAIGallery.Models;

namespace NAIGallery.Services;

/// <summary>
/// Abstraction over the image indexing & thumbnail pipeline to decouple UI/pages from the concrete implementation
/// and enable easier unit testing/mocking.
/// </summary>
public interface IImageIndexService
{
    /// <summary>All indexed metadata entries (unordered snapshot view).</summary>
    IEnumerable<ImageMetadata> All { get; }

    /// <summary>Index (or re-index) a folder recursively.</summary>
    Task IndexFolderAsync(string folder, IProgress<double>? progress = null, CancellationToken ct = default);

    /// <summary>Search by tag/prompt tokens.</summary>
    IEnumerable<ImageMetadata> SearchByTag(string query);

    /// <summary>Ensure a thumbnail of at least the given width is available (progressive upgrade allowed).</summary>
    Task EnsureThumbnailAsync(ImageMetadata meta, int decodeWidth = 256, CancellationToken ct = default, bool allowDownscale = false);

    /// <summary>Preload thumbnails for a collection of metadata items.</summary>
    Task PreloadThumbnailsAsync(IEnumerable<ImageMetadata> items, int decodeWidth = 256, CancellationToken ct = default, int maxParallelism = 0);

    /// <summary>Lookup an item by absolute path.</summary>
    bool TryGet(string path, out ImageMetadata? meta);

    /// <summary>Initialize UI dispatcher used for marshaling UI-bound thumbnail work.</summary>
    void InitializeDispatcher(DispatcherQueue dispatcherQueue);

    /// <summary>Suspend/resume applying decoded thumbnails to bound metadata objects.</summary>
    void SetApplySuspended(bool suspended);

    /// <summary>Force draining any queued apply work.</summary>
    void FlushApplyQueue();

    /// <summary>Apply thumbnails immediately for the provided visible set when suspended.</summary>
    void DrainVisible(HashSet<ImageMetadata> visible);

    /// <summary>Reset and clear in-memory thumbnail cache.</summary>
    void ClearThumbnailCache();

    /// <summary>Adjust thumbnail cache capacity (entry count).</summary>
    int ThumbnailCacheCapacity { get; set; }

    /// <summary>Attempt to enrich a metadata object with missing structured fields.</summary>
    void RefreshMetadata(ImageMetadata meta);
}
