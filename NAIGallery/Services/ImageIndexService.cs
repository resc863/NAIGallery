using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using NAIGallery.Models;
using Microsoft.UI.Xaml.Media.Imaging; // WriteableBitmap
using System.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.UI.Dispatching; // DispatcherQueue
using Microsoft.UI.Xaml.Media; // ImageSource
using Windows.Graphics.Imaging;
using Windows.Storage.Streams; // IRandomAccessStream, IBuffer
using System.Runtime.InteropServices.WindowsRuntime; // WindowsRuntime* extensions

namespace NAIGallery.Services;

/// <summary>
/// Central service that indexes PNG files, parses NovelAI metadata, manages a thumbnail cache,
/// and marshals UI updates for thumbnails.
/// </summary>
public class ImageIndexService
{
    private readonly ConcurrentDictionary<string, ImageMetadata> _index = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _tagSet = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private const string IndexFileName = "nai_index.json";

    private DispatcherQueue? _dispatcherQueue;

    // In-memory LRU-ish cache of raw BGRA8 pixel buffers for thumbnails.
    private MemoryCache _thumbCache;
    private readonly object _cacheLock = new();
    private int _thumbCapacity = 5000;

    /// <summary>
    /// 캐시 용량 변경 시 알림을 위한 이벤트
    /// </summary>
    public event Action<int>? ThumbnailCacheCapacityChanged;

    /// <summary>
    /// Maximum number of entries stored in the thumbnail memory cache.
    /// </summary>
    public int ThumbnailCacheCapacity
    {
        get => _thumbCapacity;
        set
        {
            int newCap = Math.Max(100, value);
            lock (_cacheLock)
            {
                _thumbCapacity = newCap;
                var old = _thumbCache;
                _thumbCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = _thumbCapacity });
                try { old.Dispose(); } catch { }
            }
            ThumbnailCacheCapacityChanged?.Invoke(_thumbCapacity);
        }
    }

    private readonly ConcurrentDictionary<string, byte> _thumbLoading = new();
    private static readonly SemaphoreSlim _uiDecodeGate = new(1,1);

    // Queue of (meta, image source) awaiting UI application, for batching.
    private readonly ConcurrentQueue<(ImageMetadata Meta, ImageSource Src, int Width, int Gen)> _applyQueue = new();
    private int _applyScheduled = 0;
    private volatile bool _applySuspended = false;

    // Generation counter per file to drop stale decodes.
    private readonly ConcurrentDictionary<string, int> _fileGeneration = new(StringComparer.OrdinalIgnoreCase);

    // Cache entry storing raw BGRA8 premultiplied pixels and dimensions
    private sealed class PixelCacheEntry
    {
        public required byte[] Pixels { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
    }

    /// <summary>Suspends or resumes applying decoded thumbnails to the UI.</summary>
    /// <param name="suspended"><see langword="true"/> to suspend, <see langword="false"/> to resume.</param>
    public void SetApplySuspended(bool suspended)
    {
        _applySuspended = suspended;
        if (!suspended) ScheduleApplyDrain();
    }

    /// <summary>Schedules draining the apply queue (if any).</summary>
    public void FlushApplyQueue() => ScheduleApplyDrain();

    public ImageIndexService()
    {
        _thumbCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = _thumbCapacity });
    }

    /// <summary>
    /// Clears the in-memory thumbnail cache and resets thumbnails on all known items.
    /// </summary>
    public void ClearThumbnailCache()
    {
        lock (_cacheLock)
        {
            var old = _thumbCache;
            _thumbCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = _thumbCapacity });
            try { old.Dispose(); } catch { }
        }
        void ClearAction()
        {
            foreach (var meta in _index.Values)
            {
                if (meta.Thumbnail != null) { meta.Thumbnail = null; meta.ThumbnailPixelWidth = null; }
            }
        }
        if (_dispatcherQueue != null) _dispatcherQueue.TryEnqueue(ClearAction);
        else ClearAction();
    }

    /// <summary>Returns all indexed images (unsorted).</summary>
    public IEnumerable<ImageMetadata> All => _index.Values;

    private volatile List<ImageMetadata>? _sortedByPath;
    private readonly object _sortedLock = new();

    /// <summary>
    /// Returns a cached snapshot of the index sorted by full file path.
    /// </summary>
    public IReadOnlyList<ImageMetadata> GetSortedByFilePath()
    {
        var snapshot = _sortedByPath;
        if (snapshot != null) return snapshot;
        lock (_sortedLock)
        {
            snapshot ??= _index.Values.OrderBy(m => m.FilePath, StringComparer.OrdinalIgnoreCase).ToList();
            _sortedByPath = snapshot;
            return snapshot;
        }
    }

    private void InvalidateSortedCache() => _sortedByPath = null;

    /// <summary>Looks up a metadata entry by absolute path.</summary>
    public bool TryGet(string path, out ImageMetadata? meta) => _index.TryGetValue(path, out meta);

    /// <summary>Stores the dispatcher queue for marshaling UI work to the UI thread.</summary>
    public void InitializeDispatcher(DispatcherQueue dispatcherQueue) => _dispatcherQueue = dispatcherQueue;

    private static string MakeMemKey(string filePath, long ticks, int width) => filePath + "|" + ticks.ToString() + "|w" + width.ToString();

    /// <summary>
    /// Ensures a thumbnail is loaded. If <paramref name="allowDownscale"/> is true, it can re-decode to a smaller width
    /// when the current thumbnail is larger than requested; otherwise, it only upgrades.
    /// </summary>
    public async Task EnsureThumbnailAsync(ImageMetadata meta, int decodeWidth = 256, CancellationToken ct = default, bool allowDownscale = false)
    {
        try
        {
            if (_dispatcherQueue == null) return;
            if (!File.Exists(meta.FilePath)) return;
            if (ct.IsCancellationRequested) return;

            long ticks = meta.LastWriteTimeTicks ?? SafeGetTicks(meta.FilePath);
            if (ticks == 0) return;

            int currentWidth = meta.ThumbnailPixelWidth ?? 0;
            if (!allowDownscale && currentWidth >= decodeWidth)
            {
                var memKeyTouch = MakeMemKey(meta.FilePath, ticks, currentWidth);
                lock (_cacheLock) { _ = _thumbCache.TryGetValue(memKeyTouch, out _); }
                try { _dispatcherQueue.TryEnqueue(() => meta.ThumbnailPixelWidth = currentWidth); } catch { }
                return;
            }

            var reqKey = MakeMemKey(meta.FilePath, ticks, decodeWidth);
            PixelCacheEntry? cached;
            lock (_cacheLock) { _thumbCache.TryGetValue(reqKey, out cached); }
            int gen = _fileGeneration.AddOrUpdate(meta.FilePath, 1, (_, g) => g + 1);
            if (cached != null)
            {
                if (ct.IsCancellationRequested) return;
                await CreateAndApplyFromPixelsUIAsync(meta, cached, decodeWidth, gen, allowDownscale).ConfigureAwait(false);
                return;
            }

            int smallWidth = Math.Clamp(decodeWidth / 2, 96, 160);
            if (!allowDownscale && currentWidth == 0 && decodeWidth > smallWidth)
            {
                if (ct.IsCancellationRequested) return;
                await LoadEnsureAsync(meta, smallWidth, ticks, ct, gen, false).ConfigureAwait(false);
            }

            if (ct.IsCancellationRequested) return;
            await LoadEnsureAsync(meta, decodeWidth, ticks, ct, gen, allowDownscale).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // swallow
        }
    }

    /// <summary>
    /// Loads (or retrieves from cache) a raw pixel buffer for the requested width and schedules UI application.
    /// </summary>
    private async Task LoadEnsureAsync(ImageMetadata meta, int width, long ticks, CancellationToken ct, int gen, bool allowDownscale)
    {
        try
        {
            if (ct.IsCancellationRequested) return;
            string loadKey = MakeMemKey(meta.FilePath, ticks, width);
            PixelCacheEntry? cached;
            lock (_cacheLock) { _thumbCache.TryGetValue(loadKey, out cached); }
            if (cached != null)
            {
                if (ct.IsCancellationRequested) return;
                await CreateAndApplyFromPixelsUIAsync(meta, cached, width, gen, allowDownscale).ConfigureAwait(false);
                return;
            }

            if (!_thumbLoading.TryAdd(loadKey, 0)) return;
            try
            {
#if DEBUG
                Debug.WriteLine($"[Thumb][IO] {meta.FilePath} w={width}");
#endif
                if (ct.IsCancellationRequested) return;

                // Allow reading even if another process holds the file for writing/rename
                using var fs = File.Open(meta.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using IRandomAccessStream ras = fs.AsRandomAccessStream();

                var swb = await DecodeSoftwareBitmapAsync(ras, width, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) { try { swb?.Dispose(); } catch { } return; }
                if (swb == null) return;

                int pxCount = (int)(swb.PixelWidth * swb.PixelHeight * 4);
                var bytes = new byte[pxCount];
                try { swb.CopyToBuffer(bytes.AsBuffer()); }
                catch { try { swb.Dispose(); } catch { } return; }
                var entry = new PixelCacheEntry { Pixels = bytes, Width = (int)swb.PixelWidth, Height = (int)swb.PixelHeight };
                try { swb.Dispose(); } catch { }

                lock (_cacheLock)
                {
                    _thumbCache.Set(loadKey, entry, new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = TimeSpan.FromMinutes(10) });
                }
                await CreateAndApplyFromPixelsUIAsync(meta, entry, width, gen, allowDownscale).ConfigureAwait(false);
            }
            finally
            {
                _thumbLoading.TryRemove(loadKey, out _);
            }
        }
        catch (OperationCanceledException)
        {
            // swallow
        }
    }

    /// <summary>
    /// Creates a WriteableBitmap from raw pixels on the UI thread and enqueues it for application to the bound item.
    /// </summary>
    private async Task CreateAndApplyFromPixelsUIAsync(ImageMetadata meta, PixelCacheEntry entry, int width, int gen, bool allowDownscale)
    {
        if (_dispatcherQueue == null) return;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool enq = _dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, async () =>
        {
            try
            {
                if (_applySuspended) { tcs.TrySetResult(true); return; }
                await _uiDecodeGate.WaitAsync();
                try
                {
                    var wb = new WriteableBitmap(entry.Width, entry.Height);
                    try
                    {
                        using var s = wb.PixelBuffer.AsStream();
                        s.Write(entry.Pixels, 0, entry.Pixels.Length);
                        wb.Invalidate();
                    }
                    catch { }
                    await EnqueueApplyAsync(meta, wb, width, gen, allowDownscale).ConfigureAwait(false);
                }
                finally { _uiDecodeGate.Release(); }
            }
            catch { }
            finally { tcs.TrySetResult(true); }
        });
        if (!enq) tcs.TrySetResult(true);
        await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Enqueues the prepared ImageSource to be applied to the item on the UI thread. Drops stale generations.
    /// </summary>
    private async Task EnqueueApplyAsync(ImageMetadata meta, ImageSource src, int width, int gen, bool allowDownscale)
    {
        int latest = _fileGeneration.TryGetValue(meta.FilePath, out var g) ? g : gen;
        if (gen < latest && !allowDownscale) return;

        _applyQueue.Enqueue((meta, src, width, gen));
        ScheduleApplyDrain();
        await Task.CompletedTask;
    }

    /// <summary>
    /// Decodes the image stream into a SoftwareBitmap at or below target width.
    /// </summary>
    private static async Task<SoftwareBitmap?> DecodeSoftwareBitmapAsync(IRandomAccessStream ras, int targetWidth, CancellationToken ct)
    {
        try
        {
            var decoder = await BitmapDecoder.CreateAsync(ras);
            uint srcW = decoder.PixelWidth;
            uint srcH = decoder.PixelHeight;
            if (srcW == 0 || srcH == 0) return null;

            double scale = Math.Min(1.0, (double)targetWidth / srcW);
            uint outW = (uint)Math.Max(1, Math.Round(srcW * scale));
            uint outH = (uint)Math.Max(1, Math.Round(srcH * scale));

            var transform = new BitmapTransform
            {
                ScaledWidth = outW,
                ScaledHeight = outH,
                InterpolationMode = BitmapInterpolationMode.Fant
            };

            var sb = await decoder.GetSoftwareBitmapAsync(
                decoder.BitmapPixelFormat,
                decoder.BitmapAlphaMode,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb);

            if (sb.BitmapPixelFormat != BitmapPixelFormat.Bgra8 || sb.BitmapAlphaMode != BitmapAlphaMode.Premultiplied)
            {
                var converted = SoftwareBitmap.Convert(sb, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                sb.Dispose();
                sb = converted;
            }
            return sb;
        }
        catch { return null; }
    }

    /// <summary>
    /// Drains the apply queue in small batches on the UI thread, applying progressive thumbnail upgrades.
    /// </summary>
    private void ScheduleApplyDrain()
    {
        if (_dispatcherQueue == null) return;
        if (_applySuspended) return;
        if (Interlocked.Exchange(ref _applyScheduled, 1) != 0) return;
        _dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, async () =>
        {
            try
            {
                await Task.Yield();
                int processed = 0;
                while (!_applySuspended && _applyQueue.TryDequeue(out var item))
                {
                    var (meta, src, width, gen) = item;
                    try
                    {
                        int latest = _fileGeneration.TryGetValue(meta.FilePath, out var g) ? g : gen;
                        if (gen < latest) continue;

                        // Apply thumbnail and width upgrade
                        if (meta.Thumbnail == null || width >= (meta.ThumbnailPixelWidth ?? 0))
                        {
                            meta.Thumbnail = src;
                            meta.ThumbnailPixelWidth = width;
                        }

                        // Update AspectRatio from the applied source to preserve original proportions
                        try
                        {
                            if (src is WriteableBitmap wb && wb.PixelWidth > 0 && wb.PixelHeight > 0)
                            {
                                double ar = Math.Clamp((double)wb.PixelWidth / Math.Max(1, wb.PixelHeight), 0.1, 10.0);
                                if (Math.Abs(ar - meta.AspectRatio) > 0.001)
                                {
                                    meta.AspectRatio = ar;
                                }
                            }
                        }
                        catch { }
                    }
                    catch { }

                    processed++;
                    if (processed >= 24)
                    {
                        processed = 0;
                        await Task.Yield();
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _applyScheduled, 0);
                if (!_applySuspended && !_applyQueue.IsEmpty) ScheduleApplyDrain();
            }
        });
    }

    private static long SafeGetTicks(string filePath)
    {
        try { return new FileInfo(filePath).LastWriteTimeUtc.Ticks; } catch { return 0; }
    }

    public async Task IndexFolderAsync(string folder, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        await LoadIndexAsync(folder).ConfigureAwait(false);

        var toRemove = _index.Keys.Where(k => k.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && !File.Exists(k)).ToList();
        foreach (var r in toRemove)
        {
            if (_index.TryRemove(r, out var removed) && removed != null)
            {
                lock (_lock) foreach (var t in removed.Tags) _tagSet.Remove(t);
                InvalidateSortedCache();
#if DEBUG
                Debug.WriteLine($"[Index] Removed stale {r}");
#endif
            }
        }

        var files = System.IO.Directory.EnumerateFiles(folder, "*.png", SearchOption.AllDirectories).ToArray();
        int total = files.Length;
        if (total == 0) { progress?.Report(1); await SaveIndexAsync(folder).ConfigureAwait(false); return; }

        long processed = 0;
        var tagBatches = new ConcurrentBag<IEnumerable<string>>();
        var sw = Stopwatch.StartNew();
        var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1), CancellationToken = ct };

        await Parallel.ForEachAsync(files, po, async (file, token) =>
        {
            token.ThrowIfCancellationRequested();

            if (_index.TryGetValue(file, out var existing))
            {
                try
                {
                    var ticks = new FileInfo(file).LastWriteTimeUtc.Ticks;
                    if (existing.LastWriteTimeTicks == ticks)
                    {
                        Interlocked.Increment(ref processed);
                        ThrottledReport(processed, total, progress, sw);
                        return;
                    }
                }
                catch { }
            }

            var meta = ExtractMetadata(file, folder);
            if (meta != null)
            {
                meta.SearchText = BuildSearchText(meta);
                _index[file] = meta;
                tagBatches.Add(meta.Tags);
                InvalidateSortedCache();
#if DEBUG
                Debug.WriteLine($"[Index] Added {file}");
#endif
            }

            Interlocked.Increment(ref processed);
            ThrottledReport(processed, total, progress, sw);
            await ValueTask.CompletedTask;
        }).ConfigureAwait(false);

        if (!tagBatches.IsEmpty)
        {
            lock (_lock)
            {
                foreach (var batch in tagBatches)
                    foreach (var tag in batch)
                        _tagSet.Add(tag);
            }
        }
        progress?.Report(1);
        await SaveIndexAsync(folder).ConfigureAwait(false);
    }

    private static void ThrottledReport(long processed, int total, IProgress<double>? progress, Stopwatch sw)
    {
        if (progress == null) return;
        if (processed == total || sw.ElapsedMilliseconds >= 100)
        {
            sw.Restart();
            progress.Report((double)processed / total);
        }
    }

    // New: parallelizable preload with configurable degree of parallelism (default: 4)
    public async Task PreloadThumbnailsAsync(IEnumerable<ImageMetadata> items, int decodeWidth = 256, CancellationToken ct = default, int maxParallelism = 0)
    {
        if (maxParallelism <= 0)
        {
            int cpu = Math.Max(1, Environment.ProcessorCount - 1);
            maxParallelism = Math.Clamp(cpu, 2, 12);
        }
        if (maxParallelism <= 1)
        {
            foreach (var m in items)
            {
                if (ct.IsCancellationRequested) break;
                await EnsureThumbnailAsync(m, decodeWidth, ct).ConfigureAwait(false);
            }
            return;
        }
        var po = new ParallelOptions { MaxDegreeOfParallelism = maxParallelism, CancellationToken = ct };
        await Parallel.ForEachAsync(items, po, async (m, token) =>
        {
            await EnsureThumbnailAsync(m, decodeWidth, token).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    public IEnumerable<ImageMetadata> SearchByTag(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return All;
        var tokens = query.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                          .Select(t => t.ToLowerInvariant())
                          .Distinct()
                          .ToArray();
        if (tokens.Length == 0) return All;

        return All
            .Select(m =>
            {
                var hay = m.SearchText ??= BuildSearchText(m);
                int score = 0; int tagHits = 0; bool any = false;
                foreach (var tok in tokens)
                {
                    int idx = hay.IndexOf(tok, StringComparison.Ordinal);
                    if (idx < 0) continue;
                    any = true;
                    int occ = 0;
                    while (idx >= 0) { occ++; idx = hay.IndexOf(tok, idx + tok.Length, StringComparison.Ordinal); }
                    score += occ * Math.Max(1, tok.Length);
                    if (m.Tags.Any(t => t.Contains(tok, StringComparison.OrdinalIgnoreCase))) tagHits++;
                }
                return (m, any, tagHits, score);
            })
            .Where(r => r.any)
            .OrderByDescending(r => r.tagHits)
            .ThenByDescending(r => r.score)
            .Select(r => r.m);
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
        {
            foreach (var cp in m.CharacterPrompts)
            {
                if (!string.IsNullOrEmpty(cp.Prompt)) sb.Append(cp.Prompt).Append(' ');
                if (!string.IsNullOrEmpty(cp.NegativePrompt)) sb.Append(cp.NegativePrompt).Append(' ');
            }
        }
        return sb.ToString().ToLowerInvariant();
    }

    public IEnumerable<string> SuggestTags(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return Enumerable.Empty<string>();
        lock (_lock)
        {
            return _tagSet.Where(t => t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                          .OrderBy(t => t)
                          .Take(20)
                          .ToList();
        }
    }

    private async Task LoadIndexAsync(string folder)
    {
        var path = System.IO.Path.Combine(folder, IndexFileName);
        if (!File.Exists(path)) return;
        try
        {
            var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            var list = JsonSerializer.Deserialize<List<ImageMetadata>>(json) ?? new();
            foreach (var meta in list)
            {
                if (string.IsNullOrWhiteSpace(meta.FilePath) && meta.RelativePath != null)
                    meta.FilePath = System.IO.Path.GetFullPath(System.IO.Path.Combine(folder, meta.RelativePath));
                else if (meta.FilePath != null && !System.IO.Path.IsPathRooted(meta.FilePath))
                    meta.FilePath = System.IO.Path.GetFullPath(System.IO.Path.Combine(folder, meta.FilePath));

                // ensure non-null collections to avoid NREs on legacy index files
                meta.Tags ??= new();

                meta.SearchText = BuildSearchText(meta);
                if (meta.LastWriteTimeTicks == null)
                {
                    try { meta.LastWriteTimeTicks = new FileInfo(meta.FilePath).LastWriteTimeUtc.Ticks; } catch { }
                }
                if (!string.IsNullOrWhiteSpace(meta.FilePath))
                {
                    _index[meta.FilePath] = meta;
                    foreach (var t in meta.Tags) _tagSet.Add(t);
                }
            }
            InvalidateSortedCache();
        }
        catch { }
    }

    private async Task SaveIndexAsync(string folder)
    {
        var path = System.IO.Path.Combine(folder, IndexFileName);
        var tempPath = path + ".tmp";
        try
        {
            var list = _index.Values
                              .Where(m => m.FilePath.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                              .Select(m => new ImageMetadata
                              {
                                  FilePath = m.FilePath,
                                  RelativePath = System.IO.Path.GetRelativePath(folder, m.FilePath),
                                  Prompt = m.Prompt,
                                  NegativePrompt = m.NegativePrompt,
                                  BasePrompt = m.BasePrompt,
                                  BaseNegativePrompt = m.BaseNegativePrompt,
                                  CharacterPrompts = m.CharacterPrompts,
                                  Parameters = m.Parameters,
                                  Tags = m.Tags,
                                  LastWriteTimeTicks = m.LastWriteTimeTicks
                              }).ToList();
            var opts = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(list, opts);
            await File.WriteAllTextAsync(tempPath, json).ConfigureAwait(false);
            File.Copy(tempPath, path, overwrite: true);
            File.Delete(tempPath);
        }
        catch { try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { } }
    }

    private ImageMetadata? ExtractMetadata(string file, string rootFolder)
    {
        var collected = new HashSet<string>(StringComparer.Ordinal);

        // Read textual chunks directly (tEXt/zTXt/iTXt)
        foreach (var raw in PngTextChunkReader.ReadRawTextChunks(file))
            if (!string.IsNullOrWhiteSpace(raw)) collected.Add(raw.Trim());

        string? prompt = null, negative = null, basePrompt = null, baseNegative = null;
        List<CharacterPrompt>? characterPrompts = null; List<string?>? charNegatives = null;
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in collected)
        {
            if (e.StartsWith('{'))
            {
                try
                {
                    using var doc = JsonDocument.Parse(e);
                    var root = doc.RootElement;
                    if (root.ValueKind == JsonValueKind.Object)
                    {
                        if (root.TryGetProperty("v4_prompt", out var v4p) && v4p.ValueKind == JsonValueKind.Object)
                            ParseV4Prompt(v4p, ref basePrompt, ref characterPrompts, false, ref charNegatives);
                        if (root.TryGetProperty("v4_negative_prompt", out var v4np) && v4np.ValueKind == JsonValueKind.Object)
                            ParseV4Prompt(v4np, ref baseNegative, ref characterPrompts, true, ref charNegatives);
                        if (root.TryGetProperty("base_prompt", out var bpProp)) basePrompt ??= bpProp.GetString();
                        if (root.TryGetProperty("negative_base_prompt", out var nbpProp)) baseNegative ??= nbpProp.GetString();
                        if (root.TryGetProperty("character_prompts", out var charsProp) && charsProp.ValueKind == JsonValueKind.Array)
                        {
                            characterPrompts ??= new();
                            foreach (var c in charsProp.EnumerateArray())
                            {
                                characterPrompts.Add(new CharacterPrompt
                                {
                                    Name = c.TryGetProperty("name", out var nProp) ? nProp.GetString() : null,
                                    Prompt = c.TryGetProperty("prompt", out var cpProp) ? cpProp.GetString() : null,
                                    NegativePrompt = c.TryGetProperty("negative_prompt", out var cnpProp) ? cnpProp.GetString() : null
                                });
                            }
                        }
                        if (root.TryGetProperty("prompt", out var pProp)) prompt ??= pProp.GetString();
                        if (root.TryGetProperty("negative_prompt", out var npProp)) negative ??= npProp.GetString();
                        if (root.TryGetProperty("uc", out var ucProp)) negative ??= ucProp.GetString();

                        foreach (var prop in root.EnumerateObject())
                        {
                            if (prop.NameEquals("prompt") || prop.NameEquals("negative_prompt") || prop.NameEquals("uc") ||
                                prop.NameEquals("base_prompt") || prop.NameEquals("negative_base_prompt") || prop.NameEquals("character_prompts") ||
                                prop.NameEquals("v4_prompt") || prop.NameEquals("v4_negative_prompt")) continue;
                            parameters[prop.Name] = prop.Value.ToString();
                        }
                        continue;
                    }
                }
                catch { }
            }
            if (e.Contains("Negative prompt:", StringComparison.OrdinalIgnoreCase))
            {
                var lines = e.Replace("\r", string.Empty).Split('\n');
                if (lines.Length > 0 && string.IsNullOrEmpty(prompt)) prompt = lines[0];
                foreach (var line in lines)
                {
                    if (line.StartsWith("Negative prompt:", StringComparison.OrdinalIgnoreCase))
                        negative = line["Negative prompt:".Length..].Trim();
                    if (line.Contains(':') && line.Contains(',') && line.IndexOf(':') < line.IndexOf(','))
                    {
                        foreach (var seg in line.Split(',', StringSplitOptions.RemoveEmptyEntries))
                        {
                            var kvp = seg.Split(':', 2);
                            if (kvp.Length == 2) parameters[kvp[0].Trim()] = kvp[1].Trim();
                        }
                    }
                }
            }
        }

        if (charNegatives != null && characterPrompts != null)
        {
            for (int i = 0; i < characterPrompts.Count && i < charNegatives.Count; i++)
            {
                var neg = CleanSegment(charNegatives[i]);
                if (!string.IsNullOrWhiteSpace(neg))
                {
                    if (string.IsNullOrWhiteSpace(characterPrompts[i].NegativePrompt)) characterPrompts[i].NegativePrompt = neg;
                    else characterPrompts[i].NegativePrompt += ", " + neg;
                }
            }
        }

        prompt = CleanSegment(prompt);
        negative = CleanSegment(negative);
        basePrompt = CleanSegment(basePrompt);
        baseNegative = CleanSegment(baseNegative);
        if (characterPrompts != null)
        {
            int idx = 1;
            foreach (var cp in characterPrompts)
            {
                cp.Prompt = CleanSegment(cp.Prompt);
                cp.NegativePrompt = CleanSegment(cp.NegativePrompt);
                if (string.IsNullOrWhiteSpace(cp.Name)) cp.Name = $"Character {idx}";
                idx++;
            }
        }

        var tags = new List<string>();
        if (!string.IsNullOrWhiteSpace(basePrompt)) tags.Add(basePrompt);
        if (characterPrompts != null)
            foreach (var cp in characterPrompts)
                if (!string.IsNullOrWhiteSpace(cp.Prompt)) tags.Add(cp.Prompt!);
        if (tags.Count == 0 && !string.IsNullOrWhiteSpace(prompt)) tags.Add(prompt!);
        if (tags.Count > 0)
        {
            tags = string.Join(',', tags)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(t => t.Trim())
                    .Where(t => t.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
        }

        var fi = new FileInfo(file);
        return new ImageMetadata
        {
            FilePath = file,
            RelativePath = System.IO.Path.GetRelativePath(rootFolder, file),
            Prompt = prompt,
            NegativePrompt = negative,
            BasePrompt = basePrompt,
            BaseNegativePrompt = baseNegative,
            CharacterPrompts = characterPrompts,
            Parameters = parameters.Count > 0 ? parameters : null,
            Tags = tags,
            LastWriteTimeTicks = fi.LastWriteTimeUtc.Ticks
        };
    }

    private static string? CleanSegment(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s;
        s = s.Trim();
        while (s.EndsWith(',')) s = s[..^1].TrimEnd();
        return s;
    }

    private static void ParseV4Prompt(JsonElement v4Obj, ref string? baseCaption, ref List<CharacterPrompt>? characterPrompts, bool isNegative, ref List<string?>? negativeListRef)
    {
        try
        {
            if (v4Obj.TryGetProperty("caption", out var captionObj) && captionObj.ValueKind == JsonValueKind.Object)
            {
                if (captionObj.TryGetProperty("base_caption", out var baseCapProp)) baseCaption ??= baseCapProp.GetString();
                if (v4Obj.TryGetProperty("char_captions", out var chars) && chars.ValueKind == JsonValueKind.Array)
                {
                    int idx = 0;
                    foreach (var c in chars.EnumerateArray())
                    {
                        string? charCaption = c.TryGetProperty("char_caption", out var cc) ? cc.GetString() : null;
                        if (!isNegative)
                        {
                            characterPrompts ??= new();
                            if (characterPrompts.Count <= idx)
                                characterPrompts.Add(new CharacterPrompt { Name = null, Prompt = charCaption });
                            else if (string.IsNullOrWhiteSpace(characterPrompts[idx].Prompt))
                                characterPrompts[idx].Prompt = charCaption;
                        }
                        else
                        {
                            negativeListRef ??= new();
                            while (negativeListRef.Count <= idx) negativeListRef.Add(null);
                            negativeListRef[idx] = charCaption;
                        }
                        idx++;
                    }
                }
            }
        }
        catch { }
    }

    public void RefreshMetadata(ImageMetadata meta)
    {
        if (meta.BasePrompt != null || (meta.CharacterPrompts != null && meta.CharacterPrompts.Count > 0)) return;
        try
        {
            var parsed = ExtractMetadata(meta.FilePath, System.IO.Path.GetDirectoryName(meta.FilePath) ?? string.Empty);
            if (parsed != null)
            {
                if (meta.BasePrompt == null && parsed.BasePrompt != null) meta.BasePrompt = parsed.BasePrompt;
                if (meta.BaseNegativePrompt == null && parsed.BaseNegativePrompt != null) meta.BaseNegativePrompt = parsed.BaseNegativePrompt;
                if ((meta.CharacterPrompts == null || meta.CharacterPrompts.Count == 0) && parsed.CharacterPrompts != null)
                    meta.CharacterPrompts = parsed.CharacterPrompts;
                if (string.IsNullOrWhiteSpace(meta.Prompt) && !string.IsNullOrWhiteSpace(parsed.Prompt)) meta.Prompt = parsed.Prompt;
                if (string.IsNullOrWhiteSpace(meta.NegativePrompt) && !string.IsNullOrWhiteSpace(parsed.NegativePrompt)) meta.NegativePrompt = parsed.NegativePrompt;
                if (meta.Parameters == null && parsed.Parameters != null) meta.Parameters = parsed.Parameters;
                meta.SearchText = BuildSearchText(meta);
                _index[meta.FilePath] = meta;
            }
        }
        catch { }
    }
}
