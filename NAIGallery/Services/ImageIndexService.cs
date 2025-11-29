using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using NAIGallery; // AppDefaults
using NAIGallery.Models;
using NAIGallery.Services.Metadata;

namespace NAIGallery.Services;

public class ImageIndexService : IImageIndexService
{
    private readonly ConcurrentDictionary<string, ImageMetadata> _index = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _tagSet = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _tagLock = new();
    private const string IndexFileName = "nai_index.json";

    private DispatcherQueue? _dispatcherQueue;
    private readonly ILogger<ImageIndexService>? _logger;

    private readonly ITokenSearchIndex _searchIndex = new TokenSearchIndex();
    private readonly IThumbnailPipeline _thumbPipeline;
    private readonly IMetadataExtractor _metadataExtractor;

    private int _thumbCapacity = AppDefaults.DefaultThumbnailCapacityBytes;
    public event Action<int>? ThumbnailCacheCapacityChanged;
    
    // 인덱스 변경 이벤트 (실시간 UI 업데이트용)
    public event EventHandler? IndexChanged;

    private volatile List<ImageMetadata>? _sortedCache;
    private readonly object _sortedLock = new();
    private void InvalidateSorted() 
    { 
        _sortedCache = null;
        // 인덱스 변경 알림 (UI 즉시 업데이트)
        IndexChanged?.Invoke(this, EventArgs.Empty);
    }

    private readonly TagTrie _tagTrie = new();

    public ImageIndexService(ILogger<ImageIndexService>? logger = null, IMetadataExtractor? extractor = null)
    {
        _logger = logger;
        _thumbPipeline = new ThumbnailPipeline(_thumbCapacity, logger);
        _metadataExtractor = extractor ?? new PngMetadataExtractor();
    }

    public IEnumerable<ImageMetadata> All => _index.Values;

    public void InitializeDispatcher(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
        _thumbPipeline.InitializeDispatcher(dispatcherQueue);
    }

    public bool TryGet(string path, out ImageMetadata? meta) => _index.TryGetValue(path, out meta);

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
        if (_dispatcherQueue != null) _dispatcherQueue.TryEnqueue(ResetThumbs); else ResetThumbs();
    }

    public void DrainVisible(HashSet<ImageMetadata> visible) => _thumbPipeline.DrainVisible(visible);

    public async Task EnsureThumbnailAsync(ImageMetadata meta, int decodeWidth = 256, CancellationToken ct = default, bool allowDownscale = false)
        => await _thumbPipeline.EnsureThumbnailAsync(meta, decodeWidth, ct, allowDownscale).ConfigureAwait(false);

    public async Task PreloadThumbnailsAsync(IEnumerable<ImageMetadata> items, int decodeWidth = 256, CancellationToken ct = default, int maxParallelism = 0)
        => await _thumbPipeline.PreloadAsync(items, decodeWidth, ct, maxParallelism <= 0 ? 0 : maxParallelism).ConfigureAwait(false);

    public async Task IndexFolderAsync(string folder, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        await LoadIndexAsync(folder).ConfigureAwait(false);

        var stale = _index.Keys.Where(k => k.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && !File.Exists(k)).ToList();
        foreach (var p in stale)
            if (_index.TryRemove(p, out var removed))
            {
                _searchIndex.Remove(removed);
                lock (_tagLock) foreach (var t in removed.Tags) _tagSet.Remove(t);
                InvalidateSorted();
            }

        int total = 0;
        try { foreach (var _ in Directory.EnumerateFiles(folder, "*.png", SearchOption.AllDirectories)) total++; } catch { }
        if (total == 0)
        {
            progress?.Report(1);
            await SaveIndexAsync(folder).ConfigureAwait(false);
            return;
        }

        // Phase 1: Quick dimension scan to immediately populate UI placeholders
        var dimChannel = System.Threading.Channels.Channel.CreateBounded<string>(new System.Threading.Channels.BoundedChannelOptions(256) { FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait });
        int workerCount = Math.Max(1, Environment.ProcessorCount - 1);
        long dimensionsScanned = 0;
        int notifyBatch = Math.Max(1, total / 50); // 2% 단위로 UI 업데이트

        var dimProducer = Task.Run(async () =>
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*.png", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    await dimChannel.Writer.WriteAsync(file, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch { }
            finally { dimChannel.Writer.TryComplete(); }
        }, ct);

        var dimConsumers = new List<Task>();
        for (int i = 0; i < workerCount; i++)
        {
            dimConsumers.Add(Task.Run(async () =>
            {
                await foreach (var file in dimChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    bool needsDimensions = false;
                    if (_index.TryGetValue(file, out var existing))
                    {
                        if (!existing.OriginalWidth.HasValue || !existing.OriginalHeight.HasValue)
                            needsDimensions = true;
                    }
                    else
                        needsDimensions = true;

                    if (needsDimensions)
                    {
                        try
                        {
                            var dims = ExtractQuickDimensions(file);
                            if (dims.HasValue)
                            {
                                if (!_index.TryGetValue(file, out var meta))
                                {
                                    meta = new ImageMetadata
                                    {
                                        FilePath = file,
                                        RelativePath = Path.GetRelativePath(folder, file),
                                        OriginalWidth = dims.Value.width,
                                        OriginalHeight = dims.Value.height,
                                        Tags = new List<string>(),
                                        LastWriteTimeTicks = new FileInfo(file).LastWriteTimeUtc.Ticks
                                    };
                                    _index[file] = meta;
                                    
                                    // 일정 수량마다 UI 업데이트 알림
                                    long scanned = Interlocked.Increment(ref dimensionsScanned);
                                    if (scanned % notifyBatch == 0 || scanned == total)
                                    {
                                        InvalidateSorted();
                                    }
                                }
                                else
                                {
                                    meta.OriginalWidth = dims.Value.width;
                                    meta.OriginalHeight = dims.Value.height;
                                    
                                    long scanned = Interlocked.Increment(ref dimensionsScanned);
                                    if (scanned % notifyBatch == 0 || scanned == total)
                                    {
                                        InvalidateSorted();
                                    }
                                }
                            }
                            else
                            {
                                Interlocked.Increment(ref dimensionsScanned);
                            }
                        }
                        catch 
                        { 
                            Interlocked.Increment(ref dimensionsScanned);
                        }
                    }
                    else
                    {
                        Interlocked.Increment(ref dimensionsScanned);
                    }
                }
            }, ct));
        }

        await Task.WhenAll(dimConsumers.Append(dimProducer)).ConfigureAwait(false);
        
        // Phase 1 완료 후 UI 최종 업데이트
        InvalidateSorted();

        // Phase 2: Full metadata extraction (background)
        var channel = System.Threading.Channels.Channel.CreateBounded<string>(new System.Threading.Channels.BoundedChannelOptions(256) { FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait });
        long processed = 0;
        var tagBatches = new ConcurrentBag<IEnumerable<string>>();
        var sw = Stopwatch.StartNew();

        var producer = Task.Run(async () =>
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*.png", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    await channel.Writer.WriteAsync(file, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch { }
            finally { channel.Writer.TryComplete(); }
        }, ct);

        var consumers = new List<Task>();
        for (int i = 0; i < workerCount; i++)
        {
            consumers.Add(Task.Run(async () =>
            {
                await foreach (var file in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    bool unchanged = false;
                    if (_index.TryGetValue(file, out var existing))
                    {
                        try 
                        { 
                            var t = new FileInfo(file).LastWriteTimeUtc.Ticks; 
                            if (existing.LastWriteTimeTicks == t && existing.OriginalWidth.HasValue && existing.OriginalHeight.HasValue && existing.Tags.Count > 0) 
                                unchanged = true; 
                        } 
                        catch { }
                    }
                    if (!unchanged)
                    {
                        var meta = _metadataExtractor.Extract(file, folder);
                        if (meta != null)
                        {
                            meta.SearchText = BuildSearchText(meta);
                            meta.TokenSet = BuildFrozenTokenSet(meta);

                            if (_index.TryGetValue(file, out var oldMeta))
                                _searchIndex.Remove(oldMeta);

                            _index[file] = meta;
                            tagBatches.Add(meta.Tags);
                            _searchIndex.Index(meta);
                            
                            // 일정 수량마다 UI 업데이트 알림
                            long proc = Interlocked.Increment(ref processed);
                            if (proc % notifyBatch == 0 || proc == total)
                            {
                                InvalidateSorted();
                            }
                            
                            foreach (var t in meta.Tags) _tagTrie.Add(t);
                        }
                        else
                        {
                            Interlocked.Increment(ref processed);
                        }
                    }
                    else
                    {
                        Interlocked.Increment(ref processed);
                    }
                    
                    ThrottledProgress(processed, total, progress, sw);
                }
            }, ct));
        }

        await Task.WhenAll(consumers.Append(producer)).ConfigureAwait(false);

        if (!tagBatches.IsEmpty)
        {
            lock (_tagLock)
            {
                foreach (var batch in tagBatches)
                    foreach (var t in batch)
                        _tagSet.Add(t);
            }
        }

        progress?.Report(1);
        
        // Phase 2 완료 후 UI 최종 업데이트
        InvalidateSorted();
        
        await SaveIndexAsync(folder).ConfigureAwait(false);
    }

    /// <summary>
    /// PNG 파일에서 IHDR 청크만 빠르게 읽어 원본 크기 추출 (메타데이터 파싱보다 훨씬 빠름)
    /// </summary>
    private static (int width, int height)? ExtractQuickDimensions(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            // PNG signature: 89 50 4E 47 0D 0A 1A 0A
            var sig = new byte[8];
            if (fs.Read(sig, 0, 8) != 8) return null;
            if (sig[0] != 0x89 || sig[1] != 0x50 || sig[2] != 0x4E || sig[3] != 0x47) return null;

            // IHDR chunk: 4 bytes length + "IHDR" + width(4) + height(4) + ...
            var lenBuf = new byte[4];
            if (fs.Read(lenBuf, 0, 4) != 4) return null;
            
            var typeBuf = new byte[4];
            if (fs.Read(typeBuf, 0, 4) != 4) return null;
            if (typeBuf[0] != 'I' || typeBuf[1] != 'H' || typeBuf[2] != 'D' || typeBuf[3] != 'R') return null;

            var dimBuf = new byte[8];
            if (fs.Read(dimBuf, 0, 8) != 8) return null;

            int width = (dimBuf[0] << 24) | (dimBuf[1] << 16) | (dimBuf[2] << 8) | dimBuf[3];
            int height = (dimBuf[4] << 24) | (dimBuf[5] << 16) | (dimBuf[6] << 8) | dimBuf[7];

            if (width > 0 && height > 0 && width < 100000 && height < 100000)
                return (width, height);

            return null;
        }
        catch { return null; }
    }

    private static void ThrottledProgress(long processed, int total, IProgress<double>? progress, Stopwatch sw)
    { if (progress == null) return; if (processed == total || sw.ElapsedMilliseconds >= 100) { sw.Restart(); progress.Report((double)processed / total); } }

    private async Task LoadIndexAsync(string folder)
    {
        var path = Path.Combine(folder, IndexFileName);
        if (!File.Exists(path)) return;
        try
        {
            var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            var list = JsonSerializer.Deserialize<List<ImageMetadata>>(json) ?? new();
            int loaded = 0, withDimensions = 0;
            foreach (var meta in list)
            {
                if (string.IsNullOrWhiteSpace(meta.FilePath) && meta.RelativePath != null)
                    meta.FilePath = Path.GetFullPath(Path.Combine(folder, meta.RelativePath));
                else if (meta.FilePath != null && !Path.IsPathRooted(meta.FilePath))
                    meta.FilePath = Path.GetFullPath(Path.Combine(folder, meta.FilePath));
                meta.Tags ??= new();
                meta.SearchText = BuildSearchText(meta);
                meta.TokenSet = BuildFrozenTokenSet(meta);
                if (meta.LastWriteTimeTicks == null)
                {
                    try { meta.LastWriteTimeTicks = new FileInfo(meta.FilePath).LastWriteTimeUtc.Ticks; } catch { }
                }
                
                // OriginalWidth/OriginalHeight are loaded from JSON
                // AspectRatio getter will compute from them automatically
                if (meta.OriginalWidth.HasValue && meta.OriginalHeight.HasValue)
                    withDimensions++;
                
                if (!string.IsNullOrWhiteSpace(meta.FilePath))
                {
                    _index[meta.FilePath] = meta;
                    foreach (var t in meta.Tags) _tagSet.Add(t);
                    _searchIndex.Index(meta);
                    foreach (var t in meta.Tags) _tagTrie.Add(t);
                    loaded++;
                }
            }
            InvalidateSorted();
            _logger?.LogInformation("Loaded {Loaded} images from index, {WithDimensions} have original dimensions", loaded, withDimensions);
        }
        catch { }
    }

    private async Task SaveIndexAsync(string folder)
    {
        var path = Path.Combine(folder, IndexFileName);
        var temp = path + ".tmp";
        try
        {
            var list = _index.Values
                              .Where(m => m.FilePath.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                              .Select(m => new ImageMetadata
                              {
                                  FilePath = m.FilePath,
                                  RelativePath = Path.GetRelativePath(folder, m.FilePath),
                                  Prompt = m.Prompt,
                                  NegativePrompt = m.NegativePrompt,
                                  BasePrompt = m.BasePrompt,
                                  BaseNegativePrompt = m.BaseNegativePrompt,
                                  CharacterPrompts = m.CharacterPrompts,
                                  Parameters = m.Parameters,
                                  Tags = m.Tags,
                                  LastWriteTimeTicks = m.LastWriteTimeTicks,
                                  OriginalWidth = m.OriginalWidth,
                                  OriginalHeight = m.OriginalHeight
                              }).ToList();
            var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(temp, json).ConfigureAwait(false);
            File.Copy(temp, path, true);
            File.Delete(temp);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    public IEnumerable<ImageMetadata> SearchByTag(string query)
    {
        // Keep original OR search for backwards compatibility
        return Search(query, andMode: false, partialMode: true);
    }

    public IEnumerable<ImageMetadata> Search(string query, bool andMode, bool partialMode)
    {
        if (string.IsNullOrWhiteSpace(query)) return All;
        var tokens = query.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                          .Select(t => t.ToLowerInvariant())
                          .Distinct()
                          .ToArray();
        if (tokens.Length == 0) return All;

        // Candidate gathering (exact token OR index)
        var exactCandidates = _searchIndex.QueryTokens(tokens);
        HashSet<ImageMetadata> candidateSet = new();
        foreach (var c in exactCandidates) candidateSet.Add(c);

        if (partialMode)
        {
            // Expand by prefix/substring matches over token sets if partial requested
            foreach (var m in _index.Values)
            {
                var set = m.TokenSet ??= BuildFrozenTokenSet(m);
                foreach (var tok in tokens)
                {
                    if (set.Any(t => t.Contains(tok, StringComparison.Ordinal))) { candidateSet.Add(m); break; }
                }
            }
        }
        if (candidateSet.Count == 0) return Array.Empty<ImageMetadata>();

        // Scoring & AND filter
        var results = new List<(ImageMetadata m, bool any, int tagHits, int score, int tokenHits)>();
        foreach (var m in candidateSet)
        {
            var hay = m.SearchText ??= BuildSearchText(m);
            var set = m.TokenSet ??= BuildFrozenTokenSet(m);
            int score = 0, tagHits = 0, tokenHits = 0; bool any = false; bool allMatch = true;
            foreach (var tok in tokens)
            {
                bool matched = false;
                if (set.Contains(tok)) { matched = true; score += Math.Max(1, tok.Length); }
                else if (partialMode && set.Any(t => t.Contains(tok, StringComparison.Ordinal))) { matched = true; score += Math.Max(1, tok.Length / 2); }
                else if (!partialMode)
                {
                    int idx = hay.IndexOf(tok, StringComparison.Ordinal);
                    if (idx >= 0)
                    {
                        matched = true; int occ = 0; while (idx >= 0) { occ++; idx = hay.IndexOf(tok, idx + tok.Length, StringComparison.Ordinal); }
                        score += occ * Math.Max(1, tok.Length);
                    }
                }
                if (matched)
                {
                    any = true; tokenHits++; if (m.Tags.Any(t => t.Contains(tok, StringComparison.OrdinalIgnoreCase))) tagHits++;
                }
                else if (andMode) { allMatch = false; }
            }
            if (!any) continue;
            if (andMode && !allMatch) continue;
            results.Add((m, any, tagHits, score, tokenHits));
        }

        return results
            .OrderByDescending(r => r.tagHits)
            .ThenByDescending(r => r.tokenHits)
            .ThenByDescending(r => r.score)
            .Select(r => r.m);
    }

    public void RefreshMetadata(ImageMetadata meta)
    {
        if (meta == null || string.IsNullOrWhiteSpace(meta.FilePath)) return;
        if (!File.Exists(meta.FilePath)) return; // 방어적 파일 체크로 예외 예방
        if (meta.BasePrompt != null || (meta.CharacterPrompts != null && meta.CharacterPrompts.Count > 0)) return;
        try
        {
            var parsed = _metadataExtractor.Extract(meta.FilePath, Path.GetDirectoryName(meta.FilePath) ?? string.Empty);
            if (parsed != null)
            {
                if (meta.BasePrompt == null && parsed.BasePrompt != null) meta.BasePrompt = parsed.BasePrompt;
                if (meta.BaseNegativePrompt == null && parsed.BaseNegativePrompt != null) meta.BaseNegativePrompt = parsed.BaseNegativePrompt;
                if ((meta.CharacterPrompts == null || meta.CharacterPrompts.Count == 0) && parsed.CharacterPrompts != null) meta.CharacterPrompts = parsed.CharacterPrompts;
                if (string.IsNullOrWhiteSpace(meta.Prompt) && !string.IsNullOrWhiteSpace(parsed.Prompt)) meta.Prompt = parsed.Prompt;
                if (string.IsNullOrWhiteSpace(meta.NegativePrompt) && !string.IsNullOrWhiteSpace(parsed.NegativePrompt)) meta.NegativePrompt = parsed.NegativePrompt;
                if (meta.Parameters == null && parsed.Parameters != null) meta.Parameters = parsed.Parameters;
                meta.SearchText = BuildSearchText(meta);
                meta.TokenSet = BuildFrozenTokenSet(meta);
                _searchIndex.Index(meta);
                _index[meta.FilePath] = meta;
                InvalidateSorted();
                foreach (var t in meta.Tags) _tagTrie.Add(t);
            }
        }
        catch { }
    }

    private static string BuildSearchText(ImageMetadata m)
    {
        var sb = new StringBuilder();
        foreach (var t in m.Tags) sb.Append(t).Append(' ');
        if (!string.IsNullOrEmpty(m.Prompt)) sb.Append(m.Prompt).Append(' ');
        if (!string.IsNullOrEmpty(m.NegativePrompt)) sb.Append(m.NegativePrompt).Append(' ');
        if (!string.IsNullOrEmpty(m.BasePrompt)) sb.Append(m.BasePrompt).Append(' ');
        if (!string.IsNullOrEmpty(m.BaseNegativePrompt)) sb.Append(m.BaseNegativePrompt).Append(' ');
        if (m.CharacterPrompts != null)
            foreach (var cp in m.CharacterPrompts)
            {
                if (!string.IsNullOrEmpty(cp.Prompt)) sb.Append(cp.Prompt).Append(' ');
                if (!string.IsNullOrEmpty(cp.NegativePrompt)) sb.Append(cp.NegativePrompt).Append(' ');
            }
        return sb.ToString().ToLowerInvariant();
    }

    private static IReadOnlySet<string> BuildFrozenTokenSet(ImageMetadata m)
    {
        var hs = new HashSet<string>(StringComparer.Ordinal);
        void Add(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            foreach (var tok in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (tok.Length <= 1 || tok.Length > 64) continue;
                hs.Add(tok.ToLowerInvariant());
            }
        }
        foreach (var t in m.Tags) Add(t);
        Add(m.Prompt); Add(m.NegativePrompt); Add(m.BasePrompt); Add(m.BaseNegativePrompt);
        if (m.CharacterPrompts != null)
        {
            foreach (var cp in m.CharacterPrompts)
            {
                Add(cp.Prompt); Add(cp.NegativePrompt);
            }
        }
        return System.Collections.Frozen.FrozenSet.ToFrozenSet(hs, StringComparer.Ordinal);
    }

    public IEnumerable<string> SuggestTags(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return Enumerable.Empty<string>();
        var trie = _tagTrie.Suggest(prefix, AppDefaults.SuggestionLimit);
        if (!trie.Any())
        {
            lock (_tagLock)
            {
                return _tagSet.Where(t => t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                              .OrderBy(t => t)
                              .Take(AppDefaults.SuggestionLimit)
                              .ToList();
            }
        }
        HashSet<string> merged = new(StringComparer.OrdinalIgnoreCase);
        foreach (var t in trie) merged.Add(t);
        lock (_tagLock)
        {
            foreach (var t in _tagSet)
            {
                if (merged.Count >= AppDefaults.SuggestionLimit) break;
                if (t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) merged.Add(t);
            }
        }
        return merged.OrderBy(t => t).Take(AppDefaults.SuggestionLimit).ToList();
    }

    public IReadOnlyList<ImageMetadata> GetSortedByFilePath()
    {
        var snap = _sortedCache; if (snap != null) return snap;
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
        => _index.Values.Where(m => m.FilePath.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(m => m.FilePath, StringComparer.OrdinalIgnoreCase);

    // Scheduling wrappers
    public void Schedule(ImageMetadata meta, int width, bool highPriority = false) => _thumbPipeline.Schedule(meta, width, highPriority);
    public void BoostVisible(IEnumerable<ImageMetadata> metas, int width) => _thumbPipeline.BoostVisible(metas, width);
    public void UpdateViewport(IReadOnlyList<ImageMetadata> orderedVisible, IReadOnlyList<ImageMetadata> bufferItems, int width) => _thumbPipeline.UpdateViewport(orderedVisible, bufferItems, width);
}
