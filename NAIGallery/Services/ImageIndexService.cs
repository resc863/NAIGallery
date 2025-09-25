using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NAIGallery.Models;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace NAIGallery.Services;

public class ImageIndexService : IImageIndexService
{
    private readonly ConcurrentDictionary<string, ImageMetadata> _index = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _tagSet = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _tagLock = new();
    private const string IndexFileName = "nai_index.json";

    private DispatcherQueue? _dispatcherQueue;
    private readonly ILogger<ImageIndexService>? _logger;

    private MemoryCache _thumbCache;
    private readonly object _cacheLock = new();
    private int _thumbCapacity = 5000;

    private readonly Dictionary<string, HashSet<ImageMetadata>> _tokenIndex = new(StringComparer.Ordinal);
    private readonly object _tokenLock = new();

    private long _cacheHit, _cacheMiss;
    public long ThumbnailCacheHitCount => Interlocked.Read(ref _cacheHit);
    public long ThumbnailCacheMissCount => Interlocked.Read(ref _cacheMiss);

    public event Action<int>? ThumbnailCacheCapacityChanged;

    // Sorted cache
    private volatile List<ImageMetadata>? _sortedCache;
    private readonly object _sortedLock = new();
    private void InvalidateSorted() => _sortedCache = null;

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
                _thumbCache = CreateThumbCache(_thumbCapacity);
                try { old.Dispose(); } catch { }
            }
            ThumbnailCacheCapacityChanged?.Invoke(_thumbCapacity);
        }
    }

    private static MemoryCache CreateThumbCache(int capacity) => new(new MemoryCacheOptions { SizeLimit = capacity });

    // Decode coordination
    private readonly ConcurrentDictionary<string, byte> _thumbLoading = new();
    private static readonly SemaphoreSlim _uiDecodeGate = new(1, 1);
    private readonly SemaphoreSlim _decodeConcurrency = new(Math.Clamp(Environment.ProcessorCount / 2, 1, 4));

    // UI apply batching
    private readonly ConcurrentQueue<(ImageMetadata Meta, ImageSource Src, int Width, int Gen)> _applyQueue = new();
    private int _applyScheduled = 0;
    private volatile bool _applySuspended = false;

    // Generation per file to drop stale decodes
    private readonly ConcurrentDictionary<string, int> _fileGeneration = new(StringComparer.OrdinalIgnoreCase);

    private sealed class PixelCacheEntry
    {
        public required byte[] Pixels { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required int RentedLength { get; init; }
    }

    private readonly ConcurrentDictionary<string,int> _maxRequestedWidth = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string,int> _decodeInProgressMaxWidth = new(StringComparer.OrdinalIgnoreCase);

    public ImageIndexService(ILogger<ImageIndexService>? logger = null)
    {
        _logger = logger;
        _thumbCache = CreateThumbCache(_thumbCapacity);
    }

    public IEnumerable<ImageMetadata> All => _index.Values;

    public void InitializeDispatcher(DispatcherQueue dispatcherQueue) => _dispatcherQueue = dispatcherQueue;

    public bool TryGet(string path, out ImageMetadata? meta) => _index.TryGetValue(path, out meta);

    public void SetApplySuspended(bool suspended)
    {
        _applySuspended = suspended;
        if (!suspended) ScheduleApplyDrain();
    }

    public void FlushApplyQueue() => ScheduleApplyDrain();

    public void ClearThumbnailCache()
    {
        lock (_cacheLock)
        {
            var old = _thumbCache;
            _thumbCache = CreateThumbCache(_thumbCapacity);
            try { old.Dispose(); } catch { }
        }
        void ResetThumbs()
        {
            foreach (var m in _index.Values)
            {
                if (m.Thumbnail != null) { m.Thumbnail = null; m.ThumbnailPixelWidth = null; }
            }
        }
        if (_dispatcherQueue != null) _dispatcherQueue.TryEnqueue(ResetThumbs); else ResetThumbs();
    }

    private static IEnumerable<string> Tokenize(ImageMetadata m)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        void Add(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            foreach (var tok in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (tok.Length <= 1 || tok.Length > 64) continue; set.Add(tok.ToLowerInvariant());
            }
        }
        foreach (var t in m.Tags) Add(t);
        Add(m.Prompt); Add(m.NegativePrompt); Add(m.BasePrompt); Add(m.BaseNegativePrompt);
        if (m.CharacterPrompts != null) foreach (var cp in m.CharacterPrompts) { Add(cp.Prompt); Add(cp.NegativePrompt); }
        return set;
    }

    private void AddToTokenIndex(ImageMetadata meta)
    {
        var tokens = Tokenize(meta);
        lock (_tokenLock)
        {
            foreach (var tok in tokens)
            {
                if (!_tokenIndex.TryGetValue(tok, out var set)) { set = new HashSet<ImageMetadata>(); _tokenIndex[tok] = set; }
                set.Add(meta);
            }
        }
    }

    private void RemoveFromTokenIndex(ImageMetadata meta)
    {
        lock (_tokenLock)
        {
            foreach (var kv in _tokenIndex.ToList())
                if (kv.Value.Remove(meta) && kv.Value.Count == 0) _tokenIndex.Remove(kv.Key);
        }
    }

    private static string MakeMemKey(string filePath, long ticks, int width)
    {
        var ticksStr = ticks.ToString(); var widthStr = width.ToString();
        return string.Create(filePath.Length + 1 + ticksStr.Length + 2 + widthStr.Length, (filePath, ticksStr, widthStr), static (dst, s) =>
        {
            s.filePath.AsSpan().CopyTo(dst); int p = s.filePath.Length; dst[p++] = '|';
            s.ticksStr.AsSpan().CopyTo(dst[p..]); p += s.ticksStr.Length; dst[p++] = '|'; dst[p++] = 'w';
            s.widthStr.AsSpan().CopyTo(dst[p..]);
        });
    }

    public async Task EnsureThumbnailAsync(ImageMetadata meta, int decodeWidth = 256, CancellationToken ct = default, bool allowDownscale = false)
    {
        try
        {
            if (_dispatcherQueue == null || !File.Exists(meta.FilePath) || ct.IsCancellationRequested) return;
            long ticks = meta.LastWriteTimeTicks ?? SafeGetTicks(meta.FilePath); if (ticks == 0) return;

            // Record the maximum requested width for this file (upgrade coalescing)
            int maxReq = _maxRequestedWidth.AddOrUpdate(meta.FilePath, decodeWidth, (_, old) => decodeWidth > old ? decodeWidth : old);
            if (decodeWidth < maxReq) decodeWidth = maxReq; // always chase latest/biggest request

            int current = meta.ThumbnailPixelWidth ?? 0;
            if (!allowDownscale && current >= decodeWidth)
            { TouchCache(meta.FilePath, ticks, current); try { _dispatcherQueue.TryEnqueue(() => meta.ThumbnailPixelWidth = current); } catch { } return; }

            // If a larger(or equal) decode already in progress, skip this redundant smaller request
            int inProg = _decodeInProgressMaxWidth.AddOrUpdate(meta.FilePath, decodeWidth, (_, old) => decodeWidth > old ? decodeWidth : old);
            if (inProg > decodeWidth) return; // larger decode already running

            var key = MakeMemKey(meta.FilePath, ticks, decodeWidth);
            if (TryGetPixelEntry(key, out var entry))
            {
                int generation = _fileGeneration.AddOrUpdate(meta.FilePath, 1, (_, g) => g + 1);
                await CreateAndApplyFromPixelsUIAsync(meta, entry, decodeWidth, generation, allowDownscale).ConfigureAwait(false);
                // Cleanup decode in-progress marker if we didn't actually start a decode
                _decodeInProgressMaxWidth.TryGetValue(meta.FilePath, out var curVal);
                if (curVal == decodeWidth) _decodeInProgressMaxWidth.TryRemove(meta.FilePath, out _);
                return;
            }

            int small = Math.Clamp(decodeWidth / 2, 96, 160);
            if (!allowDownscale && current == 0 && decodeWidth > small)
            {
                // Only do progressive small decode if no larger decode has superseded it meanwhile
                int smallMarker = _decodeInProgressMaxWidth.AddOrUpdate(meta.FilePath, small, (_, old) => old > small ? old : small);
                if (smallMarker == small)
                {
                    int smallGen = _fileGeneration.AddOrUpdate(meta.FilePath, 1, (_, g) => g + 1);
                    await LoadEnsureAsync(meta, small, ticks, ct, smallGen, false).ConfigureAwait(false);
                    // do not clear marker: larger decode may follow; keep max width info
                }
            }

            int mainGen = _fileGeneration.AddOrUpdate(meta.FilePath, 1, (_, g) => g + 1);
            await LoadEnsureAsync(meta, decodeWidth, ticks, ct, mainGen, allowDownscale).ConfigureAwait(false);

            // After main decode completes, if this is the max width, remove marker
            _decodeInProgressMaxWidth.TryGetValue(meta.FilePath, out var finalVal);
            if (finalVal == decodeWidth) _decodeInProgressMaxWidth.TryRemove(meta.FilePath, out _);
        }
        catch (OperationCanceledException) { }
    }

    private void TouchCache(string file, long ticks, int width)
    {
        var key = MakeMemKey(file, ticks, width);
        lock (_cacheLock) { if (_thumbCache.TryGetValue(key, out _)) Interlocked.Increment(ref _cacheHit); else Interlocked.Increment(ref _cacheMiss); }
    }

    private bool TryGetPixelEntry(string key, out PixelCacheEntry? entry)
    {
        lock (_cacheLock)
        { if (_thumbCache.TryGetValue(key, out entry)) { Interlocked.Increment(ref _cacheHit); return true; } Interlocked.Increment(ref _cacheMiss); entry = null; return false; }
    }

    private async Task LoadEnsureAsync(ImageMetadata meta, int width, long ticks, CancellationToken ct, int gen, bool allowDownscale)
    {
        if (ct.IsCancellationRequested) return;
        string key = MakeMemKey(meta.FilePath, ticks, width);
        if (TryGetPixelEntry(key, out var cached)) { await CreateAndApplyFromPixelsUIAsync(meta, cached!, width, gen, allowDownscale).ConfigureAwait(false); return; }
        if (!_thumbLoading.TryAdd(key, 0)) return;
        try
        {
#if DEBUG
            _logger?.LogDebug("Decode start {File} w={Width}", meta.FilePath, width);
#endif
            await _decodeConcurrency.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var fs = File.Open(meta.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using IRandomAccessStream ras = fs.AsRandomAccessStream();
                var sb = await DecodeSoftwareBitmapAsync(ras, width, ct).ConfigureAwait(false);
                if (sb == null || ct.IsCancellationRequested) { try { sb?.Dispose(); } catch { } return; }
                int pxCount = (int)(sb.PixelWidth * sb.PixelHeight * 4);
                byte[] rented = ArrayPool<byte>.Shared.Rent(pxCount);
                try { sb.CopyToBuffer(rented.AsBuffer()); }
                catch { ArrayPool<byte>.Shared.Return(rented); try { sb.Dispose(); } catch { } return; }
                var entry = new PixelCacheEntry { Pixels = rented, Width = (int)sb.PixelWidth, Height = (int)sb.PixelHeight, RentedLength = rented.Length };
                try { sb.Dispose(); } catch { }
                lock (_cacheLock)
                {
                    var opts = new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = TimeSpan.FromMinutes(10) };
                    opts.PostEvictionCallbacks.Add(new PostEvictionCallbackRegistration { EvictionCallback = (_, v, __, ___) => { if (v is PixelCacheEntry p) ArrayPool<byte>.Shared.Return(p.Pixels); } });
                    _thumbCache.Set(key, entry, opts);
                }
                await CreateAndApplyFromPixelsUIAsync(meta, entry, width, gen, allowDownscale).ConfigureAwait(false);
            }
            finally { _decodeConcurrency.Release(); }
        }
        catch (OperationCanceledException) { }
        finally { _thumbLoading.TryRemove(key, out _); }
    }

    private static async Task<SoftwareBitmap?> DecodeSoftwareBitmapAsync(IRandomAccessStream ras, int targetWidth, CancellationToken ct)
    {
        try
        {
            var decoder = await BitmapDecoder.CreateAsync(ras);
            uint sw = decoder.PixelWidth, sh = decoder.PixelHeight; if (sw == 0 || sh == 0) return null;
            double scale = Math.Min(1.0, targetWidth / (double)sw);
            uint outW = (uint)Math.Max(1, Math.Round(sw * scale)), outH = (uint)Math.Max(1, Math.Round(sh * scale));
            var transform = new BitmapTransform { ScaledWidth = outW, ScaledHeight = outH, InterpolationMode = BitmapInterpolationMode.Fant };
            var sb = await decoder.GetSoftwareBitmapAsync(decoder.BitmapPixelFormat, decoder.BitmapAlphaMode, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
            if (sb.BitmapPixelFormat != BitmapPixelFormat.Bgra8 || sb.BitmapAlphaMode != BitmapAlphaMode.Premultiplied)
            { var conv = SoftwareBitmap.Convert(sb, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied); sb.Dispose(); sb = conv; }
            return sb;
        }
        catch { return null; }
    }

    private async Task CreateAndApplyFromPixelsUIAsync(ImageMetadata meta, PixelCacheEntry entry, int width, int gen, bool allowDownscale)
    {
        if (_dispatcherQueue == null) return; var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, async () =>
        {
            try { await _uiDecodeGate.WaitAsync(); try { var wb = new WriteableBitmap(entry.Width, entry.Height); try { using var s = wb.PixelBuffer.AsStream(); s.Write(entry.Pixels, 0, entry.Width * entry.Height * 4); wb.Invalidate(); } catch { } await EnqueueApplyAsync(meta, wb, width, gen, allowDownscale).ConfigureAwait(false); } finally { _uiDecodeGate.Release(); } }
            catch { }
            finally { tcs.TrySetResult(true); }
        })) tcs.TrySetResult(true);
        await tcs.Task.ConfigureAwait(false);
    }

    private Task EnqueueApplyAsync(ImageMetadata meta, ImageSource src, int width, int gen, bool allowDownscale)
    { int latest = _fileGeneration.TryGetValue(meta.FilePath, out var g) ? g : gen; if (gen < latest && !allowDownscale) return Task.CompletedTask; _applyQueue.Enqueue((meta, src, width, gen)); ScheduleApplyDrain(); return Task.CompletedTask; }

    private void ScheduleApplyDrain()
    {
        if (_dispatcherQueue == null || _applySuspended) return; if (Interlocked.Exchange(ref _applyScheduled, 1) != 0) return;
        _dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, async () =>
        {
            try
            {
                await Task.Yield(); int processed = 0;
                while (!_applySuspended && _applyQueue.TryDequeue(out var item))
                {
                    var (meta, src, width, gen) = item; try { int latest = _fileGeneration.TryGetValue(meta.FilePath, out var g) ? g : gen; if (gen < latest) continue; if (meta.Thumbnail == null || width >= (meta.ThumbnailPixelWidth ?? 0)) { meta.Thumbnail = src; meta.ThumbnailPixelWidth = width; } if (src is WriteableBitmap wb && wb.PixelWidth > 0 && wb.PixelHeight > 0) { double ar = Math.Clamp(wb.PixelWidth / (double)Math.Max(1, wb.PixelHeight), 0.1, 10.0); if (Math.Abs(ar - meta.AspectRatio) > 0.001) meta.AspectRatio = ar; } } catch { }
                    processed++; if (processed >= 24) { processed = 0; await Task.Yield(); }
                }
            }
            finally { Interlocked.Exchange(ref _applyScheduled, 0); if (!_applySuspended && !_applyQueue.IsEmpty) ScheduleApplyDrain(); }
        });
    }

    public void DrainVisible(HashSet<ImageMetadata> visible)
    {
        if (_dispatcherQueue == null || visible.Count == 0) return; _dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            var remainder = new List<(ImageMetadata, ImageSource, int, int)>(); while (_applyQueue.TryDequeue(out var item)) { if (!visible.Contains(item.Meta)) { remainder.Add(item); continue; } try { int latest = _fileGeneration.TryGetValue(item.Meta.FilePath, out var g) ? g : item.Gen; if (item.Gen < latest) continue; if (item.Meta.Thumbnail == null || item.Width >= (item.Meta.ThumbnailPixelWidth ?? 0)) { item.Meta.Thumbnail = item.Src; item.Meta.ThumbnailPixelWidth = item.Width; } if (item.Src is WriteableBitmap wb && wb.PixelWidth > 0 && wb.PixelHeight > 0) { double ar = Math.Clamp(wb.PixelWidth / (double)Math.Max(1, wb.PixelHeight), 0.1, 10.0); if (Math.Abs(ar - item.Meta.AspectRatio) > 0.001) item.Meta.AspectRatio = ar; } } catch { } } foreach (var r in remainder) _applyQueue.Enqueue(r);
        });
    }

    private static long SafeGetTicks(string filePath) { try { return new FileInfo(filePath).LastWriteTimeUtc.Ticks; } catch { return 0; } }

    public async Task IndexFolderAsync(string folder, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        await LoadIndexAsync(folder).ConfigureAwait(false);
        // Remove stale
        var stale = _index.Keys.Where(k => k.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && !File.Exists(k)).ToList();
        foreach (var p in stale) if (_index.TryRemove(p, out var removed)) { RemoveFromTokenIndex(removed); lock (_tagLock) foreach (var t in removed.Tags) _tagSet.Remove(t); InvalidateSorted(); }
        var files = Directory.EnumerateFiles(folder, "*.png", SearchOption.AllDirectories).ToArray(); int total = files.Length; if (total == 0) { progress?.Report(1); await SaveIndexAsync(folder).ConfigureAwait(false); return; }
        long processed = 0; var tagBatches = new ConcurrentBag<IEnumerable<string>>(); var sw = Stopwatch.StartNew(); var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1), CancellationToken = ct };
        await Parallel.ForEachAsync(files, po, async (file, token) =>
        {
            token.ThrowIfCancellationRequested(); bool unchanged = false; if (_index.TryGetValue(file, out var existing)) { try { var t = new FileInfo(file).LastWriteTimeUtc.Ticks; if (existing.LastWriteTimeTicks == t) unchanged = true; } catch { } }
            if (!unchanged) { var meta = ExtractMetadata(file, folder); if (meta != null) { meta.SearchText = BuildSearchText(meta); _index[file] = meta; tagBatches.Add(meta.Tags); AddToTokenIndex(meta); InvalidateSorted(); } }
            Interlocked.Increment(ref processed); ThrottledProgress(processed, total, progress, sw); await ValueTask.CompletedTask;
        }).ConfigureAwait(false);
        if (!tagBatches.IsEmpty) { lock (_tagLock) { foreach (var batch in tagBatches) foreach (var t in batch) _tagSet.Add(t); } }
        progress?.Report(1); await SaveIndexAsync(folder).ConfigureAwait(false);
    }

    private static void ThrottledProgress(long processed, int total, IProgress<double>? progress, Stopwatch sw)
    { if (progress == null) return; if (processed == total || sw.ElapsedMilliseconds >= 100) { sw.Restart(); progress.Report((double)processed / total); } }

    public IEnumerable<ImageMetadata> SearchByTag(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return All;
        var tokens = query.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t => t.ToLowerInvariant()).Distinct().ToArray(); if (tokens.Length == 0) return All;
        HashSet<ImageMetadata>? candidates = null; lock (_tokenLock) { foreach (var tok in tokens) if (_tokenIndex.TryGetValue(tok, out var set)) { candidates ??= new HashSet<ImageMetadata>(); foreach (var m in set) candidates.Add(m); } }
        if (candidates == null) return Array.Empty<ImageMetadata>();
        return candidates.Select(m => { var hay = m.SearchText ??= BuildSearchText(m); int score = 0, tagHits = 0; bool any = false; foreach (var tok in tokens) { int idx = hay.IndexOf(tok, StringComparison.Ordinal); if (idx < 0) continue; any = true; int occ = 0; while (idx >= 0) { occ++; idx = hay.IndexOf(tok, idx + tok.Length, StringComparison.Ordinal); } score += occ * Math.Max(1, tok.Length); if (m.Tags.Any(t => t.Contains(tok, StringComparison.OrdinalIgnoreCase))) tagHits++; } return (m, any, tagHits, score); })
                       .Where(r => r.any)
                       .OrderByDescending(r => r.tagHits)
                       .ThenByDescending(r => r.score)
                       .Select(r => r.m);
    }

    public async Task PreloadThumbnailsAsync(IEnumerable<ImageMetadata> items, int decodeWidth = 256, CancellationToken ct = default, int maxParallelism = 0)
    {
        if (maxParallelism <= 0) { int cpu = Math.Max(1, Environment.ProcessorCount - 1); maxParallelism = Math.Clamp(cpu, 2, 12); }
        if (maxParallelism <= 1) { foreach (var m in items) { if (ct.IsCancellationRequested) break; await EnsureThumbnailAsync(m, decodeWidth, ct).ConfigureAwait(false); } return; }
        var po = new ParallelOptions { MaxDegreeOfParallelism = maxParallelism, CancellationToken = ct }; await Parallel.ForEachAsync(items, po, async (m, token) => await EnsureThumbnailAsync(m, decodeWidth, token).ConfigureAwait(false));
    }

    private async Task LoadIndexAsync(string folder)
    {
        var path = Path.Combine(folder, IndexFileName); if (!File.Exists(path)) return; try { var json = await File.ReadAllTextAsync(path).ConfigureAwait(false); var list = JsonSerializer.Deserialize<List<ImageMetadata>>(json) ?? new(); foreach (var meta in list) { if (string.IsNullOrWhiteSpace(meta.FilePath) && meta.RelativePath != null) meta.FilePath = Path.GetFullPath(Path.Combine(folder, meta.RelativePath)); else if (meta.FilePath != null && !Path.IsPathRooted(meta.FilePath)) meta.FilePath = Path.GetFullPath(Path.Combine(folder, meta.FilePath)); meta.Tags ??= new(); meta.SearchText = BuildSearchText(meta); if (meta.LastWriteTimeTicks == null) { try { meta.LastWriteTimeTicks = new FileInfo(meta.FilePath).LastWriteTimeUtc.Ticks; } catch { } } if (!string.IsNullOrWhiteSpace(meta.FilePath)) { _index[meta.FilePath] = meta; foreach (var t in meta.Tags) _tagSet.Add(t); AddToTokenIndex(meta); } } InvalidateSorted(); } catch { } }

    private async Task SaveIndexAsync(string folder)
    {
        var path = Path.Combine(folder, IndexFileName); var temp = path + ".tmp"; try { var list = _index.Values.Where(m => m.FilePath.StartsWith(folder, StringComparison.OrdinalIgnoreCase)).Select(m => new ImageMetadata { FilePath = m.FilePath, RelativePath = Path.GetRelativePath(folder, m.FilePath), Prompt = m.Prompt, NegativePrompt = m.NegativePrompt, BasePrompt = m.BasePrompt, BaseNegativePrompt = m.BaseNegativePrompt, CharacterPrompts = m.CharacterPrompts, Parameters = m.Parameters, Tags = m.Tags, LastWriteTimeTicks = m.LastWriteTimeTicks }).ToList(); var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }); await File.WriteAllTextAsync(temp, json).ConfigureAwait(false); File.Copy(temp, path, true); File.Delete(temp); } catch { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }

    private static string? CleanSegment(string? s) { if (string.IsNullOrWhiteSpace(s)) return s; s = s.Trim(); while (s.EndsWith(',')) s = s[..^1].TrimEnd(); return s; }

    private static void ParseV4Prompt(JsonElement v4Obj, ref string? baseCaption, ref List<CharacterPrompt>? characterPrompts, bool isNegative, ref List<string?>? negativeListRef)
    { try { if (v4Obj.TryGetProperty("caption", out var captionObj) && captionObj.ValueKind == JsonValueKind.Object) { if (captionObj.TryGetProperty("base_caption", out var baseCapProp)) baseCaption ??= baseCapProp.GetString(); if (v4Obj.TryGetProperty("char_captions", out var chars) && chars.ValueKind == JsonValueKind.Array) { int i = 0; foreach (var c in chars.EnumerateArray()) { string? charCaption = c.TryGetProperty("char_caption", out var cc) ? cc.GetString() : null; if (!isNegative) { characterPrompts ??= new(); if (characterPrompts.Count <= i) characterPrompts.Add(new CharacterPrompt { Prompt = charCaption }); else if (string.IsNullOrWhiteSpace(characterPrompts[i].Prompt)) characterPrompts[i].Prompt = charCaption; } else { negativeListRef ??= new(); while (negativeListRef.Count <= i) negativeListRef.Add(null); negativeListRef[i] = charCaption; } i++; } } } } catch { } }

    private ImageMetadata? ExtractMetadata(string file, string rootFolder)
    {
        var collected = new HashSet<string>(StringComparer.Ordinal); foreach (var raw in PngTextChunkReader.ReadRawTextChunks(file)) if (!string.IsNullOrWhiteSpace(raw)) collected.Add(raw.Trim());
        string? prompt = null, negative = null, basePrompt = null, baseNegative = null; List<CharacterPrompt>? characterPrompts = null; List<string?>? charNegatives = null; var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in collected)
        {
            if (e.StartsWith('{')) { try { using var doc = JsonDocument.Parse(e); var root = doc.RootElement; if (root.ValueKind == JsonValueKind.Object) { if (root.TryGetProperty("v4_prompt", out var v4p) && v4p.ValueKind == JsonValueKind.Object) ParseV4Prompt(v4p, ref basePrompt, ref characterPrompts, false, ref charNegatives); if (root.TryGetProperty("v4_negative_prompt", out var v4np) && v4np.ValueKind == JsonValueKind.Object) ParseV4Prompt(v4np, ref baseNegative, ref characterPrompts, true, ref charNegatives); if (root.TryGetProperty("base_prompt", out var bpProp)) basePrompt ??= bpProp.GetString(); if (root.TryGetProperty("negative_base_prompt", out var nbpProp)) baseNegative ??= nbpProp.GetString(); if (root.TryGetProperty("character_prompts", out var charsProp) && charsProp.ValueKind == JsonValueKind.Array) { characterPrompts ??= new(); foreach (var c in charsProp.EnumerateArray()) characterPrompts.Add(new CharacterPrompt { Name = c.TryGetProperty("name", out var nProp) ? nProp.GetString() : null, Prompt = c.TryGetProperty("prompt", out var cpProp) ? cpProp.GetString() : null, NegativePrompt = c.TryGetProperty("negative_prompt", out var cnpProp) ? cnpProp.GetString() : null }); } if (root.TryGetProperty("prompt", out var pProp)) prompt ??= pProp.GetString(); if (root.TryGetProperty("negative_prompt", out var npProp)) negative ??= npProp.GetString(); if (root.TryGetProperty("uc", out var ucProp)) negative ??= ucProp.GetString(); foreach (var prop in root.EnumerateObject()) { if (prop.NameEquals("prompt") || prop.NameEquals("negative_prompt") || prop.NameEquals("uc") || prop.NameEquals("base_prompt") || prop.NameEquals("negative_base_prompt") || prop.NameEquals("character_prompts") || prop.NameEquals("v4_prompt") || prop.NameEquals("v4_negative_prompt")) continue; parameters[prop.Name] = prop.Value.ToString(); } continue; } } catch { } }
            if (e.Contains("Negative prompt:", StringComparison.OrdinalIgnoreCase)) { var lines = e.Replace("\r", string.Empty).Split('\n'); if (lines.Length > 0 && string.IsNullOrEmpty(prompt)) prompt = lines[0]; foreach (var line in lines) { if (line.StartsWith("Negative prompt:", StringComparison.OrdinalIgnoreCase)) negative = line["Negative prompt:".Length..].Trim(); if (line.Contains(':') && line.Contains(',') && line.IndexOf(':') < line.IndexOf(',')) { foreach (var seg in line.Split(',', StringSplitOptions.RemoveEmptyEntries)) { var kv = seg.Split(':', 2); if (kv.Length == 2) parameters[kv[0].Trim()] = kv[1].Trim(); } } } }
        }
        if (charNegatives != null && characterPrompts != null) { for (int i = 0; i < characterPrompts.Count && i < charNegatives.Count; i++) { var neg = CleanSegment(charNegatives[i]); if (!string.IsNullOrWhiteSpace(neg)) { if (string.IsNullOrWhiteSpace(characterPrompts[i].NegativePrompt)) characterPrompts[i].NegativePrompt = neg; else characterPrompts[i].NegativePrompt += ", " + neg; } } }
        prompt = CleanSegment(prompt); negative = CleanSegment(negative); basePrompt = CleanSegment(basePrompt); baseNegative = CleanSegment(baseNegative);
        if (characterPrompts != null) { int idx = 1; foreach (var cp in characterPrompts) { cp.Prompt = CleanSegment(cp.Prompt); cp.NegativePrompt = CleanSegment(cp.NegativePrompt); if (string.IsNullOrWhiteSpace(cp.Name)) cp.Name = $"Character {idx}"; idx++; } }
        var tags = new List<string>(); if (!string.IsNullOrWhiteSpace(basePrompt)) tags.Add(basePrompt); if (characterPrompts != null) foreach (var cp in characterPrompts) if (!string.IsNullOrWhiteSpace(cp.Prompt)) tags.Add(cp.Prompt!); if (tags.Count == 0 && !string.IsNullOrWhiteSpace(prompt)) tags.Add(prompt!); if (tags.Count > 0) { tags = string.Join(',', tags).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(); }
        var fi = new FileInfo(file); return new ImageMetadata { FilePath = file, RelativePath = Path.GetRelativePath(rootFolder, file), Prompt = prompt, NegativePrompt = negative, BasePrompt = basePrompt, BaseNegativePrompt = baseNegative, CharacterPrompts = characterPrompts, Parameters = parameters.Count > 0 ? parameters : null, Tags = tags, LastWriteTimeTicks = fi.LastWriteTimeUtc.Ticks };
    }

    public void RefreshMetadata(ImageMetadata meta)
    {
        if (meta.BasePrompt != null || (meta.CharacterPrompts != null && meta.CharacterPrompts.Count > 0)) return; try { var parsed = ExtractMetadata(meta.FilePath, Path.GetDirectoryName(meta.FilePath) ?? string.Empty); if (parsed != null) { if (meta.BasePrompt == null && parsed.BasePrompt != null) meta.BasePrompt = parsed.BasePrompt; if (meta.BaseNegativePrompt == null && parsed.BaseNegativePrompt != null) meta.BaseNegativePrompt = parsed.BaseNegativePrompt; if ((meta.CharacterPrompts == null || meta.CharacterPrompts.Count == 0) && parsed.CharacterPrompts != null) meta.CharacterPrompts = parsed.CharacterPrompts; if (string.IsNullOrWhiteSpace(meta.Prompt) && !string.IsNullOrWhiteSpace(parsed.Prompt)) meta.Prompt = parsed.Prompt; if (string.IsNullOrWhiteSpace(meta.NegativePrompt) && !string.IsNullOrWhiteSpace(parsed.NegativePrompt)) meta.NegativePrompt = parsed.NegativePrompt; if (meta.Parameters == null && parsed.Parameters != null) meta.Parameters = parsed.Parameters; meta.SearchText = BuildSearchText(meta); RemoveFromTokenIndex(meta); AddToTokenIndex(meta); _index[meta.FilePath] = meta; InvalidateSorted(); } } catch { }
    }

    private static string BuildSearchText(ImageMetadata m)
    { var sb = new StringBuilder(); foreach (var t in m.Tags) sb.Append(t).Append(' '); if (!string.IsNullOrEmpty(m.Prompt)) sb.Append(m.Prompt).Append(' '); if (!string.IsNullOrEmpty(m.NegativePrompt)) sb.Append(m.NegativePrompt).Append(' '); if (!string.IsNullOrEmpty(m.BasePrompt)) sb.Append(m.BasePrompt).Append(' '); if (!string.IsNullOrEmpty(m.BaseNegativePrompt)) sb.Append(m.BaseNegativePrompt).Append(' '); if (m.CharacterPrompts != null) foreach (var cp in m.CharacterPrompts) { if (!string.IsNullOrEmpty(cp.Prompt)) sb.Append(cp.Prompt).Append(' '); if (!string.IsNullOrEmpty(cp.NegativePrompt)) sb.Append(cp.NegativePrompt).Append(' '); } return sb.ToString().ToLowerInvariant(); }

    public IEnumerable<string> SuggestTags(string prefix)
    { if (string.IsNullOrWhiteSpace(prefix)) return Enumerable.Empty<string>(); lock (_tagLock) { return _tagSet.Where(t => t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).OrderBy(t => t).Take(20).ToList(); } }

    public IReadOnlyList<ImageMetadata> GetSortedByFilePath()
    { var snap = _sortedCache; if (snap != null) return snap; lock (_sortedLock) { snap = _sortedCache; if (snap == null) { snap = _index.Values.OrderBy(m => m.FilePath, StringComparer.OrdinalIgnoreCase).ToList(); _sortedCache = snap; } return snap; } }

    public IEnumerable<ImageMetadata> GetSortedByFilePath(string folder)
        => _index.Values.Where(m => m.FilePath.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(m => m.FilePath, StringComparer.OrdinalIgnoreCase);
}
