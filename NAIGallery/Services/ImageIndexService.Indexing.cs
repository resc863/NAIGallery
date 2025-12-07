using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAIGallery.Models;

namespace NAIGallery.Services;

public partial class ImageIndexService
{
    #region Indexing
    
    public async Task IndexFolderAsync(string folder, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        await LoadIndexAsync(folder).ConfigureAwait(false);

        RemoveStaleEntries(folder);

        int total = CountPngFiles(folder);
        if (total == 0)
        {
            progress?.Report(1);
            await SaveIndexAsync(folder).ConfigureAwait(false);
            return;
        }

        int notifyBatch = Math.Max(1, total / 50);

        // Phase 1: Quick dimension scan
        await ScanDimensionsAsync(folder, total, notifyBatch, ct).ConfigureAwait(false);
        InvalidateSorted();

        // Phase 2: Full metadata extraction
        await ExtractMetadataAsync(folder, total, notifyBatch, progress, ct).ConfigureAwait(false);
        
        progress?.Report(1);
        InvalidateSorted();
        
        await SaveIndexAsync(folder).ConfigureAwait(false);
    }

    private void RemoveStaleEntries(string folder)
    {
        var stale = _index.Keys
            .Where(k => k.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && !File.Exists(k))
            .ToList();
            
        foreach (var p in stale)
        {
            if (_index.TryRemove(p, out var removed))
            {
                _searchIndex.Remove(removed);
                lock (_tagLock)
                {
                    foreach (var t in removed.Tags) 
                        _tagSet.Remove(t);
                }
                InvalidateSorted();
            }
        }
    }

    private static int CountPngFiles(string folder)
    {
        int total = 0;
        try 
        { 
            foreach (var _ in Directory.EnumerateFiles(folder, "*.png", SearchOption.AllDirectories)) 
                total++; 
        } 
        catch { }
        return total;
    }

    private async Task ScanDimensionsAsync(string folder, int total, int notifyBatch, CancellationToken ct)
    {
        var channel = System.Threading.Channels.Channel.CreateBounded<string>(
            new System.Threading.Channels.BoundedChannelOptions(256) { FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait });
        
        int workerCount = Math.Max(1, Environment.ProcessorCount - 1);
        long scanned = 0;

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

        var consumers = Enumerable.Range(0, workerCount).Select(_ => Task.Run(async () =>
        {
            await foreach (var file in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                ProcessDimensionScan(file, folder, ref scanned, total, notifyBatch);
            }
        }, ct)).ToList();

        await Task.WhenAll(consumers.Append(producer)).ConfigureAwait(false);
    }

    private void ProcessDimensionScan(string file, string folder, ref long scanned, int total, int notifyBatch)
    {
        bool needsDimensions = !_index.TryGetValue(file, out var existing) || 
                               !existing.OriginalWidth.HasValue || 
                               !existing.OriginalHeight.HasValue;

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
                            Tags = new System.Collections.Generic.List<string>(),
                            LastWriteTimeTicks = new FileInfo(file).LastWriteTimeUtc.Ticks
                        };
                        _index[file] = meta;
                    }
                    else
                    {
                        meta.OriginalWidth = dims.Value.width;
                        meta.OriginalHeight = dims.Value.height;
                    }
                }
            }
            catch { }
        }

        long current = Interlocked.Increment(ref scanned);
        if (current % notifyBatch == 0 || current == total)
        {
            InvalidateSorted();
        }
    }

    private static (int width, int height)? ExtractQuickDimensions(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            
            Span<byte> sig = stackalloc byte[8];
            if (fs.Read(sig) != 8) return null;
            if (sig[0] != 0x89 || sig[1] != 0x50 || sig[2] != 0x4E || sig[3] != 0x47) return null;

            Span<byte> lenBuf = stackalloc byte[4];
            if (fs.Read(lenBuf) != 4) return null;
            
            Span<byte> typeBuf = stackalloc byte[4];
            if (fs.Read(typeBuf) != 4) return null;
            if (typeBuf[0] != 'I' || typeBuf[1] != 'H' || typeBuf[2] != 'D' || typeBuf[3] != 'R') return null;

            Span<byte> dimBuf = stackalloc byte[8];
            if (fs.Read(dimBuf) != 8) return null;

            int width = (dimBuf[0] << 24) | (dimBuf[1] << 16) | (dimBuf[2] << 8) | dimBuf[3];
            int height = (dimBuf[4] << 24) | (dimBuf[5] << 16) | (dimBuf[6] << 8) | dimBuf[7];

            if (width > 0 && height > 0 && width < 100000 && height < 100000)
                return (width, height);

            return null;
        }
        catch { return null; }
    }

    private async Task ExtractMetadataAsync(string folder, int total, int notifyBatch, IProgress<double>? progress, CancellationToken ct)
    {
        var channel = System.Threading.Channels.Channel.CreateBounded<string>(
            new System.Threading.Channels.BoundedChannelOptions(256) { FullMode = System.Threading.Channels.BoundedChannelFullMode.Wait });
        
        int workerCount = Math.Max(1, Environment.ProcessorCount - 1);
        long processed = 0;
        var tagBatches = new ConcurrentBag<System.Collections.Generic.IEnumerable<string>>();
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

        var consumers = Enumerable.Range(0, workerCount).Select(_ => Task.Run(async () =>
        {
            await foreach (var file in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                ProcessMetadataExtraction(file, folder, tagBatches, ref processed, total, notifyBatch, progress, sw);
            }
        }, ct)).ToList();

        await Task.WhenAll(consumers.Append(producer)).ConfigureAwait(false);

        MergeTagBatches(tagBatches);
    }

    private void ProcessMetadataExtraction(string file, string folder, ConcurrentBag<System.Collections.Generic.IEnumerable<string>> tagBatches,
        ref long processed, int total, int notifyBatch, IProgress<double>? progress, Stopwatch sw)
    {
        bool unchanged = false;
        if (_index.TryGetValue(file, out var existing))
        {
            try
            {
                var t = new FileInfo(file).LastWriteTimeUtc.Ticks;
                if (existing.LastWriteTimeTicks == t && 
                    existing.OriginalWidth.HasValue && 
                    existing.OriginalHeight.HasValue && 
                    existing.Tags.Count > 0)
                {
                    unchanged = true;
                }
            }
            catch { }
        }

        if (!unchanged)
        {
            var meta = _metadataExtractor.Extract(file, folder);
            if (meta != null)
            {
                meta.SearchText = SearchTextBuilder.BuildSearchText(meta);
                meta.TokenSet = SearchTextBuilder.BuildFrozenTokenSet(meta);

                if (_index.TryGetValue(file, out var oldMeta))
                    _searchIndex.Remove(oldMeta);

                _index[file] = meta;
                tagBatches.Add(meta.Tags);
                _searchIndex.Index(meta);

                foreach (var t in meta.Tags) 
                    _tagTrie.Add(t);
            }
        }

        long proc = Interlocked.Increment(ref processed);
        if (proc % notifyBatch == 0 || proc == total)
        {
            InvalidateSorted();
        }
        
        ThrottledProgress(proc, total, progress, sw);
    }

    private void MergeTagBatches(ConcurrentBag<System.Collections.Generic.IEnumerable<string>> tagBatches)
    {
        if (tagBatches.IsEmpty) return;
        
        lock (_tagLock)
        {
            foreach (var batch in tagBatches)
            {
                foreach (var t in batch)
                    _tagSet.Add(t);
            }
        }
    }

    private static void ThrottledProgress(long processed, int total, IProgress<double>? progress, Stopwatch sw)
    {
        if (progress == null) return;
        if (processed == total || sw.ElapsedMilliseconds >= 100)
        {
            sw.Restart();
            progress.Report((double)processed / total);
        }
    }

    public void RefreshMetadata(ImageMetadata meta)
    {
        if (meta == null || string.IsNullOrWhiteSpace(meta.FilePath)) return;
        if (!File.Exists(meta.FilePath)) return;
        if (meta.BasePrompt != null || (meta.CharacterPrompts != null && meta.CharacterPrompts.Count > 0)) return;
        
        try
        {
            var parsed = _metadataExtractor.Extract(meta.FilePath, Path.GetDirectoryName(meta.FilePath) ?? string.Empty);
            if (parsed != null)
            {
                if (meta.BasePrompt == null && parsed.BasePrompt != null) 
                    meta.BasePrompt = parsed.BasePrompt;
                if (meta.BaseNegativePrompt == null && parsed.BaseNegativePrompt != null) 
                    meta.BaseNegativePrompt = parsed.BaseNegativePrompt;
                if ((meta.CharacterPrompts == null || meta.CharacterPrompts.Count == 0) && parsed.CharacterPrompts != null) 
                    meta.CharacterPrompts = parsed.CharacterPrompts;
                if (string.IsNullOrWhiteSpace(meta.Prompt) && !string.IsNullOrWhiteSpace(parsed.Prompt)) 
                    meta.Prompt = parsed.Prompt;
                if (string.IsNullOrWhiteSpace(meta.NegativePrompt) && !string.IsNullOrWhiteSpace(parsed.NegativePrompt)) 
                    meta.NegativePrompt = parsed.NegativePrompt;
                if (meta.Parameters == null && parsed.Parameters != null) 
                    meta.Parameters = parsed.Parameters;
                    
                meta.SearchText = SearchTextBuilder.BuildSearchText(meta);
                meta.TokenSet = SearchTextBuilder.BuildFrozenTokenSet(meta);
                _searchIndex.Index(meta);
                _index[meta.FilePath] = meta;
                InvalidateSorted();
                
                foreach (var t in meta.Tags) 
                    _tagTrie.Add(t);
            }
        }
        catch { }
    }
    
    #endregion
}
