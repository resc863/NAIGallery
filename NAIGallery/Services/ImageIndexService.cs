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

public partial class ImageIndexService : IImageIndexService, IDisposable
{
    #region Fields
    
    private readonly ConcurrentDictionary<string, ImageMetadata> _index = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _tagSet = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _tagCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _tagLock = new();
    private const string IndexFileName = "nai_index.json";

    private DispatcherQueue? _dispatcherQueue;
    private readonly ILogger<ImageIndexService>? _logger;

    private readonly ITokenSearchIndex _searchIndex;
    private readonly IThumbnailPipeline _thumbPipeline;
    private readonly IMetadataExtractor _metadataExtractor;
    private readonly TagTrie _tagTrie = new();

    private int _thumbCapacity = AppDefaults.DefaultThumbnailCapacityBytes;
    
    private volatile List<ImageMetadata>? _sortedCache;
    private readonly object _sortedLock = new();
    private bool _disposed;
    
    #endregion

    #region Events
    
    public event Action<int>? ThumbnailCacheCapacityChanged;
    public event EventHandler? IndexChanged;
    public event Action<ImageMetadata>? ThumbnailApplied;
    
    #endregion

    #region Constructor
    
    public ImageIndexService(ITokenSearchIndex searchIndex, IThumbnailPipeline thumbPipeline, IMetadataExtractor extractor, ILogger<ImageIndexService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(searchIndex);
        ArgumentNullException.ThrowIfNull(thumbPipeline);
        ArgumentNullException.ThrowIfNull(extractor);

        _searchIndex = searchIndex;
        _thumbPipeline = thumbPipeline;
        _metadataExtractor = extractor;
        _logger = logger;
        
        _thumbPipeline.ThumbnailApplied += HandleThumbnailApplied;
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

    public void ScheduleThumbnail(ImageMetadata meta, int width, bool highPriority = false) 
        => _thumbPipeline.Schedule(meta, width, highPriority);
    
    public void BoostVisible(IReadOnlyList<ImageMetadata> metas, int width) 
        => _thumbPipeline.BoostVisible(metas, width);
    
    public void UpdateViewport(IReadOnlyList<ImageMetadata> orderedVisible, IReadOnlyList<ImageMetadata> bufferItems, int width) 
        => _thumbPipeline.UpdateViewport(orderedVisible, bufferItems, width);
    
    public void ResetPendingState() => _thumbPipeline.ResetPendingState();
    
    #endregion

    #region Private Helpers
    
    private void InvalidateSorted(bool notify = true)
    {
        _sortedCache = null;
        if (notify)
            IndexChanged?.Invoke(this, EventArgs.Empty);
    }

    private void PrepareMetadata(ImageMetadata meta, string folder)
    {
        NormalizeFilePath(meta, folder);
        meta.Tags ??= [];
        meta.SearchText = SearchTextBuilder.BuildSearchText(meta);
        meta.TokenSet = SearchTextBuilder.BuildFrozenTokenSet(meta);
    }

    private void UpsertMetadata(ImageMetadata meta, string folder, bool notify = false)
    {
        PrepareMetadata(meta, folder);

        if (string.IsNullOrWhiteSpace(meta.FilePath))
            return;

        if (_index.TryGetValue(meta.FilePath, out var oldMeta))
        {
            _searchIndex.Remove(oldMeta);
            RemoveTags(oldMeta.Tags);
        }

        _index[meta.FilePath] = meta;
        _searchIndex.Index(meta);
        AddTags(meta.Tags);

        InvalidateSorted(notify);
    }

    private void AddTags(IEnumerable<string>? tags)
    {
        if (tags == null)
            return;

        lock (_tagLock)
        {
            foreach (var tag in tags)
            {
                if (string.IsNullOrWhiteSpace(tag))
                    continue;

                if (_tagCounts.TryGetValue(tag, out var count))
                {
                    _tagCounts[tag] = count + 1;
                    continue;
                }

                _tagCounts[tag] = 1;
                _tagSet.Add(tag);
                _tagTrie.Add(tag);
            }
        }
    }

    private void RemoveTags(IEnumerable<string>? tags)
    {
        if (tags == null)
            return;

        lock (_tagLock)
        {
            bool requiresRebuild = false;

            foreach (var tag in tags)
            {
                if (string.IsNullOrWhiteSpace(tag) || !_tagCounts.TryGetValue(tag, out var count))
                    continue;

                if (count > 1)
                {
                    _tagCounts[tag] = count - 1;
                    continue;
                }

                _tagCounts.Remove(tag);
                _tagSet.Remove(tag);
                requiresRebuild = true;
            }

            if (requiresRebuild)
            {
                _tagTrie.Clear();
                foreach (var tag in _tagSet)
                    _tagTrie.Add(tag);
            }
        }
    }

    private void HandleThumbnailApplied(ImageMetadata meta) => ThumbnailApplied?.Invoke(meta);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _thumbPipeline.ThumbnailApplied -= HandleThumbnailApplied;

        if (_thumbPipeline is IDisposable disposablePipeline)
            disposablePipeline.Dispose();
    }
    
    #endregion
}
