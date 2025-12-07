using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using NAIGallery.Models;
using NAIGallery.Services.Metadata;

namespace NAIGallery.Services;

public partial class ImageIndexService : IImageIndexService
{
    #region Fields
    
    private readonly ConcurrentDictionary<string, ImageMetadata> _index = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _tagSet = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _tagLock = new();
    private const string IndexFileName = "nai_index.json";

    private DispatcherQueue? _dispatcherQueue;
    private readonly ILogger<ImageIndexService>? _logger;

    private readonly ITokenSearchIndex _searchIndex = new TokenSearchIndex();
    private readonly IThumbnailPipeline _thumbPipeline;
    private readonly IMetadataExtractor _metadataExtractor;
    private readonly TagTrie _tagTrie = new();

    private int _thumbCapacity = AppDefaults.DefaultThumbnailCapacityBytes;
    
    private volatile List<ImageMetadata>? _sortedCache;
    private readonly object _sortedLock = new();
    
    #endregion

    #region Events
    
    public event Action<int>? ThumbnailCacheCapacityChanged;
    public event EventHandler? IndexChanged;
    public event Action<ImageMetadata>? ThumbnailApplied;
    
    #endregion

    #region Constructor
    
    public ImageIndexService(ILogger<ImageIndexService>? logger = null, IMetadataExtractor? extractor = null)
    {
        _logger = logger;
        _thumbPipeline = new ThumbnailPipeline(_thumbCapacity, logger);
        _metadataExtractor = extractor ?? new PngMetadataExtractor();
        
        _thumbPipeline.ThumbnailApplied += meta => ThumbnailApplied?.Invoke(meta);
    }
    
    #endregion

    #region Index Access
    
    public IEnumerable<ImageMetadata> All => _index.Values;

    public bool TryGet(string path, out ImageMetadata? meta) => _index.TryGetValue(path, out meta);

    public IReadOnlyList<ImageMetadata> GetSortedByFilePath()
    {
        var snap = _sortedCache;
        if (snap != null) return snap;
        
        lock (_sortedLock)
        {
            snap = _sortedCache;
            if (snap == null)
            {
                snap = _index.Values.OrderBy(m => m.FilePath, StringComparer.OrdinalIgnoreCase).ToList();
                _sortedCache = snap;
            }
            return snap;
        }
    }

    public IEnumerable<ImageMetadata> GetSortedByFilePath(string folder)
        => _index.Values
            .Where(m => m.FilePath.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.FilePath, StringComparer.OrdinalIgnoreCase);
    
    #endregion

    #region Initialization
    
    public void InitializeDispatcher(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
        _thumbPipeline.InitializeDispatcher(dispatcherQueue);
    }
    
    #endregion

    #region Thumbnail Management
    
    public int ThumbnailCacheCapacity
    {
        get => _thumbCapacity;
        set
        {
            int newCap = Math.Max(AppDefaults.MinThumbnailCacheCapacity, value);
            if (newCap == _thumbCapacity) return;
            _thumbCapacity = newCap;
            _thumbPipeline.CacheCapacity = newCap;
            ThumbnailCacheCapacityChanged?.Invoke(newCap);
        }
    }

    public void SetApplySuspended(bool suspended) => _thumbPipeline.SetApplySuspended(suspended);
    public void FlushApplyQueue() => _thumbPipeline.FlushApplyQueue();
    public void DrainVisible(HashSet<ImageMetadata> visible) => _thumbPipeline.DrainVisible(visible);

    public void ClearThumbnailCache()
    {
        _thumbPipeline.ClearCache();
        
        void ResetThumbs()
        {
            foreach (var m in _index.Values)
            {
                if (m.Thumbnail != null)
                {
                    m.Thumbnail = null;
                    m.ThumbnailPixelWidth = null;
                }
            }
        }
        
        if (_dispatcherQueue != null) 
            _dispatcherQueue.TryEnqueue(ResetThumbs); 
        else 
            ResetThumbs();
    }

    public Task EnsureThumbnailAsync(ImageMetadata meta, int decodeWidth = 256, CancellationToken ct = default, bool allowDownscale = false)
        => _thumbPipeline.EnsureThumbnailAsync(meta, decodeWidth, ct, allowDownscale);

    public Task PreloadThumbnailsAsync(IEnumerable<ImageMetadata> items, int decodeWidth = 256, CancellationToken ct = default, int maxParallelism = 0)
        => _thumbPipeline.PreloadAsync(items, decodeWidth, ct, maxParallelism <= 0 ? 0 : maxParallelism);

    public void Schedule(ImageMetadata meta, int width, bool highPriority = false) 
        => _thumbPipeline.Schedule(meta, width, highPriority);
    
    public void BoostVisible(IEnumerable<ImageMetadata> metas, int width) 
        => _thumbPipeline.BoostVisible(metas, width);
    
    public void UpdateViewport(IReadOnlyList<ImageMetadata> orderedVisible, IReadOnlyList<ImageMetadata> bufferItems, int width) 
        => _thumbPipeline.UpdateViewport(orderedVisible, bufferItems, width);
    
    public void ResetPendingState() => _thumbPipeline.ResetPendingState();
    
    #endregion

    #region Private Helpers
    
    private void InvalidateSorted()
    {
        _sortedCache = null;
        IndexChanged?.Invoke(this, EventArgs.Empty);
    }
    
    #endregion
}
