using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NAIGallery.Models;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using System.Diagnostics.CodeAnalysis; // DynamicDependency for AOT
using System.Runtime.InteropServices; // COMException
using System.Diagnostics;

namespace NAIGallery.Services;

internal sealed class ThumbnailPipeline : IThumbnailPipeline, IDisposable
{
    // Hint trimmer: keep WriteableBitmap public constructors
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(WriteableBitmap))]
    public ThumbnailPipeline(int capacityBytes, ILogger? logger=null)
    { _logger = logger; _byteCapacity = Math.Max(1, capacityBytes); }

    private readonly ILogger? _logger;
    private DispatcherQueue? _dispatcher;

    private sealed class PixelEntry { public required byte[] Pixels; public required int W; public required int H; public required int Rented; }
    private sealed class LruNode { public required string Key; public required PixelEntry Entry; public LruNode? Prev; public LruNode? Next; }
    private readonly Dictionary<string, LruNode> _cacheMap = new(StringComparer.Ordinal);
    private LruNode? _head; private LruNode? _tail; private long _currentBytes; private long _byteCapacity; private readonly object _cacheLock = new();

    // Error / stats
    private long _ioErrors, _formatErrors, _canceled, _unknownErrors;

    private readonly ConcurrentDictionary<string, byte> _inflight = new();
    // Decode concurrency: allow wide parallelism but cap reasonably
    private readonly SemaphoreSlim _decodeGate = new(Math.Clamp(Environment.ProcessorCount, 2, 16));
    private static readonly SemaphoreSlim _uiGate = new(1,1);
    private readonly ConcurrentDictionary<string,int> _fileGeneration = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string,int> _maxRequested = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string,int> _decodeInProgressMax = new(StringComparer.OrdinalIgnoreCase);

    // Pending scheduled (dedup) file->maxWidth per current epoch
    private readonly ConcurrentDictionary<string,int> _pendingScheduled = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentQueue<(ImageMetadata Meta, ImageSource Src, int Width, int Gen)> _applyQ = new();
    private int _applyScheduled; private volatile bool _applySuspended;

    private sealed record ThumbReq(ImageMetadata Meta, int Width, bool High, int Epoch);
    private ConcurrentQueue<ThumbReq> _high = new();
    private ConcurrentQueue<ThumbReq> _normal = new();
    private readonly CancellationTokenSource _schedCts = new();
    private int _activeWorkers = 0;
    private volatile int _epoch = 0;
    private int _targetWorkers = Math.Clamp(Environment.ProcessorCount - 1, 2, 16);

    // UI responsiveness monitor
    private Timer? _uiPulseTimer;
    private double _uiLagMs;
    private volatile bool _uiBusy;

    public void InitializeDispatcher(DispatcherQueue dispatcherQueue)
    { _dispatcher = dispatcherQueue; StartUiPulseMonitor(); EnsureWorkers(); }

    public int CacheCapacity
    {
        get => (int)Math.Min(int.MaxValue, Interlocked.Read(ref _byteCapacity));
        set { long newCap = Math.Max(1, value); lock(_cacheLock){ _byteCapacity = newCap; Prune_NoLock(); } }
    }

    public void SetApplySuspended(bool suspended){ _applySuspended = suspended; if(!suspended) ScheduleDrain(); }
    public void FlushApplyQueue() => ScheduleDrain();

    public void DrainVisible(HashSet<ImageMetadata> visible)
    {
        if (_dispatcher == null || visible.Count==0) return;
        _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            var remainder = new List<(ImageMetadata,ImageSource,int,int)>();
            while(_applyQ.TryDequeue(out var it))
            {
                if(!visible.Contains(it.Meta)){ remainder.Add(it); continue; }
                TryApply(it.Meta,it.Src,it.Width,it.Gen,allowDownscale:true);
            }
            foreach(var r in remainder) _applyQ.Enqueue(r);
        });
    }

    public void ClearCache()
    {
        lock(_cacheLock)
        {
            foreach (var kv in _cacheMap) ArrayPool<byte>.Shared.Return(kv.Value.Entry.Pixels);
            _cacheMap.Clear(); _head = _tail = null; _currentBytes = 0;
        }
        if(_dispatcher!=null) _dispatcher.TryEnqueue(()=>{});
    }

    // Scheduling API
    public void Schedule(ImageMetadata meta, int width, bool highPriority = false)
    { InternalEnqueue(meta, width, highPriority, _epoch); EnsureWorkers(); }
    public void BoostVisible(IEnumerable<ImageMetadata> metas, int width)
    { int ep = _epoch; foreach (var m in metas) InternalEnqueue(m, width, true, ep, force:true); EnsureWorkers(); }
    public void UpdateViewport(IReadOnlyList<ImageMetadata> orderedVisible, IReadOnlyList<ImageMetadata> bufferItems, int width)
    {
        int newEpoch = Interlocked.Increment(ref _epoch);
        _high = new ConcurrentQueue<ThumbReq>(); _normal = new ConcurrentQueue<ThumbReq>();
        _pendingScheduled.Clear();
        foreach (var m in orderedVisible) InternalEnqueue(m, width, true, newEpoch, force:true);
        foreach (var m in bufferItems) InternalEnqueue(m, width, false, newEpoch);
        EnsureWorkers();
    }

    private void InternalEnqueue(ImageMetadata meta, int width, bool high, int epoch, bool force=false)
    {
        if (meta.FilePath == null) return;
        int existing = meta.ThumbnailPixelWidth ?? 0;
        if(!force && existing >= width) return;
        // Dedup across queues
        _pendingScheduled.AddOrUpdate(meta.FilePath, width, (_,prev)=> width>prev? width: prev);
        int pendingWidth = _pendingScheduled[meta.FilePath];
        if(width < pendingWidth && !force) return; // superseded by larger already
        var req = new ThumbReq(meta, pendingWidth, high, epoch);
        if (high) _high.Enqueue(req); else _normal.Enqueue(req);
        UpdateWorkerTarget();
    }

    private void UpdateWorkerTarget()
    {
        int backlog = _high.Count + _normal.Count;
        int cpu = Math.Max(2, Environment.ProcessorCount);
        int ideal;
        if (backlog <= 4) ideal = Math.Min(cpu, 4);
        else if (backlog <= 12) ideal = Math.Min(cpu + 2, 8);
        else if (backlog <= 32) ideal = Math.Min(cpu + 4, 12);
        else ideal = Math.Min(cpu * 2, 16);
        // If UI is busy, scale down to leave headroom
        if (_uiBusy)
        {
            int reserve = Math.Clamp(cpu / 3, 1, 4); // reserve ~1/3 cores for UI/animations
            ideal = Math.Max(1, Math.Min(ideal, cpu - reserve));
            ideal = Math.Min(ideal, 8);
        }
        ideal = Math.Clamp(ideal, 2, 16);
        int cur = _targetWorkers;
        if(ideal != cur){ _targetWorkers = ideal; EnsureWorkers(); }
    }

    private void EnsureWorkers()
    {
        while (!_schedCts.IsCancellationRequested)
        { int cur = _activeWorkers; if (cur >= _targetWorkers) break; if (Interlocked.CompareExchange(ref _activeWorkers, cur+1, cur) == cur) _ = Task.Run(WorkerLoopAsync); }
    }

    private async Task WorkerLoopAsync()
    {
        // Lower worker thread priority to avoid preempting UI
        try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; } catch { }

        var token = _schedCts.Token;
        try
        {
            while(!token.IsCancellationRequested)
            {
                if(!_high.TryDequeue(out var req) && !_normal.TryDequeue(out req))
                { await Task.Delay(30, token).ConfigureAwait(false); if(_high.IsEmpty && _normal.IsEmpty && _activeWorkers>1) break; continue; }
                if(req.Epoch < _epoch) continue; if((req.Meta.ThumbnailPixelWidth ?? 0) >= req.Width) continue;
                try { await EnsureThumbnailAsync(req.Meta, req.Width, token, false).ConfigureAwait(false); } catch (OperationCanceledException) { Interlocked.Increment(ref _canceled); }
                // Cooperatively yield to keep UI responsive
                await Task.Yield();
            }
        }
        catch(OperationCanceledException) { }
        finally
        { Interlocked.Decrement(ref _activeWorkers); if(!_schedCts.IsCancellationRequested && (!_high.IsEmpty || !_normal.IsEmpty)) EnsureWorkers(); }
    }

    public async Task EnsureThumbnailAsync(ImageMetadata meta, int decodeWidth, CancellationToken ct, bool allowDownscale)
    {
        try
        {
            if(_dispatcher==null || ct.IsCancellationRequested) return;
            if(!File.Exists(meta.FilePath)) return; long ticks = SafeGetTicks(meta.FilePath); if(ticks==0) return;
            int maxReq = _maxRequested.AddOrUpdate(meta.FilePath, decodeWidth, (_,old)=> decodeWidth>old?decodeWidth:old);
            if(decodeWidth < maxReq) decodeWidth = maxReq;
            int current = meta.ThumbnailPixelWidth ?? 0;
            if(!allowDownscale && current >= decodeWidth){ Touch(meta.FilePath,ticks, decodeWidth); return; }
            int inProg = _decodeInProgressMax.AddOrUpdate(meta.FilePath, decodeWidth, (_,old)=> decodeWidth>old?decodeWidth:old);
            if(inProg > decodeWidth) return;
            string key = MakeCacheKey(meta.FilePath,ticks,decodeWidth);
            if (TryGetEntry(key, out var entry))
            {
                int gen = _fileGeneration.AddOrUpdate(meta.FilePath,1,(_,g)=>g+1);
                await CreateAndQueueApplyAsync(meta, entry, decodeWidth, gen, allowDownscale).ConfigureAwait(false);
                _decodeInProgressMax.TryGetValue(meta.FilePath,out var cur); if(cur==decodeWidth) _decodeInProgressMax.TryRemove(meta.FilePath,out _);
                return;
            }
            int small = Math.Clamp(decodeWidth/2,96,160);
            if(!allowDownscale && current==0 && decodeWidth>small)
            {
                int smallMarker = _decodeInProgressMax.AddOrUpdate(meta.FilePath, small, (_,old)=> old>small? old: small);
                if(smallMarker==small)
                { int sgen = _fileGeneration.AddOrUpdate(meta.FilePath,1,(_,g)=>g+1); await LoadDecodeAsync(meta, small, ticks, ct, sgen, false).ConfigureAwait(false); }
            }
            int mainGen = _fileGeneration.AddOrUpdate(meta.FilePath,1,(_,g)=>g+1);
            await LoadDecodeAsync(meta, decodeWidth, ticks, ct, mainGen, allowDownscale).ConfigureAwait(false);
            _decodeInProgressMax.TryGetValue(meta.FilePath,out var final); if(final==decodeWidth) _decodeInProgressMax.TryRemove(meta.FilePath,out _);
        }
        catch(OperationCanceledException){ Interlocked.Increment(ref _canceled); }
    }

    public async Task PreloadAsync(IEnumerable<ImageMetadata> items, int decodeWidth, CancellationToken ct, int maxParallelism)
    {
        if(ct.IsCancellationRequested) return;
        if(maxParallelism<=0)
        {
            int cpu = Math.Max(1,Environment.ProcessorCount-1);
            int reserve = _uiBusy ? Math.Max(1, cpu/2) : Math.Max(1, cpu/4);
            int effective = Math.Max(1, cpu - reserve);
            maxParallelism = Math.Clamp(effective, 2, _uiBusy ? 8 : 24);
        }
        if(maxParallelism<=1){ foreach(var m in items){ if(ct.IsCancellationRequested) break; await EnsureThumbnailAsync(m, decodeWidth, ct, false).ConfigureAwait(false);} return; }
        var po = new ParallelOptions{ MaxDegreeOfParallelism = maxParallelism, CancellationToken = ct};
        await Parallel.ForEachAsync(items, po, async (m, token)=> await EnsureThumbnailAsync(m, decodeWidth, token, false).ConfigureAwait(false));
    }

    private async Task LoadDecodeAsync(ImageMetadata meta, int width, long ticks, CancellationToken ct, int gen, bool allowDownscale)
    {
        string key = MakeCacheKey(meta.FilePath,ticks,width);
        if (TryGetEntry(key, out var cached)) { await CreateAndQueueApplyAsync(meta, cached, width, gen, allowDownscale). ConfigureAwait(false); return; }
        if(!_inflight.TryAdd(key,0)) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await _decodeGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var fs = File.Open(meta.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using IRandomAccessStream ras = fs.AsRandomAccessStream();
                var sb = await DecodeAsync(ras, width, ct).ConfigureAwait(false);
                if(sb==null || ct.IsCancellationRequested){ if(ct.IsCancellationRequested) Interlocked.Increment(ref _canceled); else Interlocked.Increment(ref _formatErrors); return; }
                int pxCount = (int)(sb.PixelWidth * sb.PixelHeight * 4);
                var rented = ArrayPool<byte>.Shared.Rent(pxCount);
                try { sb.CopyToBuffer(rented.AsBuffer()); }
                catch { ArrayPool<byte>.Shared.Return(rented); Interlocked.Increment(ref _formatErrors); return; }
                var entry = new PixelEntry{ Pixels = rented, W = (int)sb.PixelWidth, H = (int)sb.PixelHeight, Rented = rented.Length};
                try { sb.Dispose(); } catch {}
                AddEntry(key, entry);
                await CreateAndQueueApplyAsync(meta, entry, width, gen, allowDownscale).ConfigureAwait(false);
            }
            catch (IOException) { Interlocked.Increment(ref _ioErrors); }
            catch (UnauthorizedAccessException) { Interlocked.Increment(ref _ioErrors); }
            catch (OperationCanceledException) { Interlocked.Increment(ref _canceled); }
            catch (COMException) { Interlocked.Increment(ref _unknownErrors); }
            catch (Exception ex) { Interlocked.Increment(ref _unknownErrors); if(_logger!=null) _logger.LogDebug(ex, "Decode error {File}", meta.FilePath); }
            finally { _decodeGate.Release(); }
        }
        finally
        {
            sw.Stop();
            _inflight.TryRemove(key,out _);
        }
    }

    private static async Task<SoftwareBitmap?> DecodeAsync(IRandomAccessStream ras, int targetWidth, CancellationToken ct)
    {
        try
        {
            var decoder = await BitmapDecoder.CreateAsync(ras);
            uint sw = decoder.PixelWidth, sh = decoder.PixelHeight; if(sw==0||sh==0) return null;
            double scale = Math.Min(1.0, targetWidth/(double)sw);
            uint ow = (uint)Math.Max(1, Math.Round(sw*scale)); uint oh = (uint)Math.Max(1, Math.Round(sh*scale));
            var transform = new BitmapTransform{ ScaledWidth = ow, ScaledHeight = oh, InterpolationMode = BitmapInterpolationMode.Fant };
            // Request BGRA8 Premultiplied directly to avoid an extra conversion step (and some native errors)
            try
            {
                var sb = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
                return sb;
            }
            catch (COMException)
            {
                // Fallback path: let decoder pick defaults, then convert
                var sb = await decoder.GetSoftwareBitmapAsync(decoder.BitmapPixelFormat, decoder.BitmapAlphaMode, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
                if(sb.BitmapPixelFormat!= BitmapPixelFormat.Bgra8 || sb.BitmapAlphaMode!= BitmapAlphaMode.Premultiplied)
                { var conv = SoftwareBitmap.Convert(sb, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied); sb.Dispose(); sb = conv; }
                return sb;
            }
        }
        catch (OperationCanceledException) { return null; }
        catch { return null; }
    }

    private async Task CreateAndQueueApplyAsync(ImageMetadata meta, PixelEntry entry, int width, int gen, bool allowDownscale)
    {
        if(_dispatcher==null) return; var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if(!_dispatcher.TryEnqueue(DispatcherQueuePriority.Low, async () =>
        {
            try
            {
                await _uiGate.WaitAsync();
                try
                {
                    var wb = new WriteableBitmap(entry.W, entry.H);
                    try 
                    { 
                        var buffer = wb.PixelBuffer; 
                        int expected = entry.W*entry.H*4;
                        int capacity = (int)buffer.Capacity;
                        int toWrite = Math.Min(expected, capacity);
                        using var s = buffer.AsStream();
                        s.Write(entry.Pixels,0, toWrite); 
                        wb.Invalidate(); 
                    } 
                    catch (Exception ex)
                    { 
                        _logger?.LogDebug(ex, "PixelBuffer write failed {File}", meta.FilePath);
                    }
                    EnqueueApply(meta, wb, width, gen, allowDownscale);
                }
                finally { _uiGate.Release(); }
            }
            catch { }
            finally { tcs.TrySetResult(true); }
        })) tcs.TrySetResult(true);
        await tcs.Task.ConfigureAwait(false);
    }

    private void EnqueueApply(ImageMetadata meta, ImageSource src, int width, int gen, bool allowDownscale)
    { int latest = _fileGeneration.TryGetValue(meta.FilePath, out var g) ? g : gen; if(gen < latest && !allowDownscale) return; _applyQ.Enqueue((meta,src,width,gen)); ScheduleDrain(); }

    private void ScheduleDrain()
    {
        if(_dispatcher==null || _applySuspended) return; if(Interlocked.Exchange(ref _applyScheduled,1)!=0) return;
        _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, async () =>
        {
            try
            {
                await Task.Yield(); int processed=0;
                while(!_applySuspended && _applyQ.TryDequeue(out var item))
                {
                    TryApply(item.Meta,item.Src,item.Width,item.Gen,false);
                    processed++; if(processed>=12){ processed=0; await Task.Yield(); }
                }
            }
            finally { Interlocked.Exchange(ref _applyScheduled,0); if(!_applySuspended && !_applyQ.IsEmpty) ScheduleDrain(); }
        });
    }

    private void TryApply(ImageMetadata meta, ImageSource src, int width, int gen, bool allowDownscale)
    {
        int latest = _fileGeneration.TryGetValue(meta.FilePath, out var g) ? g : gen; if(gen < latest && !allowDownscale) return;
        if(meta.Thumbnail == null || width >= (meta.ThumbnailPixelWidth ?? 0))
        { meta.Thumbnail = src; meta.ThumbnailPixelWidth = width; }
        if(src is WriteableBitmap wb && wb.PixelWidth>0 && wb.PixelHeight>0)
        { double ar = Math.Clamp(wb.PixelWidth / (double)Math.Max(1, wb.PixelHeight), 0.1, 10.0); if(Math.Abs(ar - meta.AspectRatio) > 0.001) meta.AspectRatio = ar; }
    }

    private string MakeCacheKey(String file, long ticks, int width)
    { var ts=ticks.ToString(); var ws=width.ToString(); return string.Create(file.Length+1+ts.Length+2+ws.Length,(file,ts,ws),(dst,s)=>{ s.file.AsSpan().CopyTo(dst); int p=s.file.Length; dst[p++]='|'; s.ts.AsSpan().CopyTo(dst[p..]); p+=s.ts.Length; dst[p++]='|'; dst[p++]='w'; s.ws.AsSpan().CopyTo(dst[p..]); }); }

    private bool TryGetEntry(string key, out PixelEntry? entry)
    { lock(_cacheLock){ if(_cacheMap.TryGetValue(key,out var node)){ MoveToHead_NoLock(node); entry = node.Entry; return true;} } entry=null; return false; }

    private void AddEntry(string key, PixelEntry entry)
    { lock(_cacheLock){ if(_cacheMap.TryGetValue(key,out var existing)){ _currentBytes -= existing.Entry.Rented; ArrayPool<byte>.Shared.Return(existing.Entry.Pixels); existing.Entry = entry; _currentBytes += entry.Rented; MoveToHead_NoLock(existing); Prune_NoLock(); return; } var node = new LruNode{ Key = key, Entry = entry }; _cacheMap[key] = node; _currentBytes += entry.Rented; InsertHead_NoLock(node); Prune_NoLock(); } }

    private void Touch(string file,long ticks,int width)
    { var key = MakeCacheKey(file,ticks,width); lock(_cacheLock){ if(_cacheMap.TryGetValue(key, out var node)){ MoveToHead_NoLock(node); return; } } }

    private void InsertHead_NoLock(LruNode node)
    { node.Prev = null; node.Next = _head; if(_head!=null) _head.Prev = node; _head = node; if(_tail==null) _tail = node; }
    private void MoveToHead_NoLock(LruNode node)
    { if(node == _head) return; if(node.Prev!=null) node.Prev.Next = node.Next; if(node.Next!=null) node.Next.Prev = node.Prev; if(node == _tail) _tail = node.Prev; node.Prev = null; node.Next = _head; if(_head!=null) _head.Prev = node; _head = node; if(_tail==null) _tail = node; }
    private void Prune_NoLock()
    { while(_currentBytes > _byteCapacity && _tail != null){ var evict = _tail; var prev = evict.Prev; if(prev!=null) prev.Next = null; _tail = prev; if(_tail==null) _head = null; _cacheMap.Remove(evict.Key); _currentBytes -= evict.Entry.Rented; ArrayPool<byte>.Shared.Return(evict.Entry.Pixels); } }

    private static long SafeGetTicks(string file){ try { return new FileInfo(file).LastWriteTimeUtc.Ticks; } catch { return 0; } }

    private void StartUiPulseMonitor()
    {
        try
        {
            _uiPulseTimer?.Dispose();
            _uiPulseTimer = new Timer(_ =>
            {
                if (_dispatcher == null) return;
                long stamp = Stopwatch.GetTimestamp();
                _dispatcher.TryEnqueue(DispatcherQueuePriority.Normal, () =>
                {
                    double ms = (Stopwatch.GetTimestamp() - stamp) * 1000.0 / Stopwatch.Frequency;
                    // EMA smoothing
                    _uiLagMs = _uiLagMs <= 0 ? ms : (_uiLagMs * 0.85 + ms * 0.15);
                    bool busy = ms > 40 || _uiLagMs > 25;
                    if (busy != _uiBusy)
                    {
                        _uiBusy = busy;
                        UpdateWorkerTarget();
                    }
                });
            }, null, dueTime: 0, period: 250);
        }
        catch { }
    }

    public void Dispose(){ try { _schedCts.Cancel(); } catch { } _schedCts.Dispose(); try { _uiPulseTimer?.Dispose(); } catch { } }
}
