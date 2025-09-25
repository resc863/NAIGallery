using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NAIGallery.Models;

namespace NAIGallery.Services;

public class ThumbnailSchedulerService : IDisposable
{
    private readonly IImageIndexService _imageService;
    private ConcurrentQueue<ThumbnailRequest> _high = new();
    private ConcurrentQueue<ThumbnailRequest> _normal = new();
    private readonly ConcurrentDictionary<string, int> _maxRequestedWidth = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private int _activeWorkers = 0;
    private volatile int _epoch = 0; // viewport epoch

    private int _targetWorkers = Math.Clamp(Environment.ProcessorCount - 1, 2, 12);

    private sealed record ThumbnailRequest(ImageMetadata Meta, int Width, bool High, int Epoch)
    {
        public DateTime EnqueuedAt { get; } = DateTime.UtcNow;
    }

    public ThumbnailSchedulerService(IImageIndexService imageService)
    {
        _imageService = imageService;
    }

    private static string MakeFileKey(ImageMetadata meta) => meta.FilePath ?? string.Empty;

    /// <summary>Adjust background decode worker count (spawns additional workers on next EnsureWorker call).</summary>
    public void SetWorkerCount(int count)
    {
        _targetWorkers = Math.Clamp(count, 1, 24);
        EnsureWorker();
    }

    /// <summary>
    /// Legacy single request enqueue (kept for compatibility). Uses current epoch.
    /// </summary>
    public void Request(ImageMetadata meta, int width, bool highPriority)
    {
        InternalEnqueue(meta, width, highPriority, _epoch);
        EnsureWorker();
    }

    /// <summary>
    /// Boost visible items (current epoch).
    /// </summary>
    public void BoostVisible(IEnumerable<ImageMetadata> metas, int width)
    {
        int ep = _epoch;
        foreach (var m in metas)
            InternalEnqueue(m, width, highPriority: true, ep, force: true);
        EnsureWorker();
    }

    /// <summary>
    /// Update the scheduler with a new viewport snapshot. Visible items are processed center-out first, then buffer items.
    /// Previous epoch requests become low priority (skipped when dequeued) without needing explicit cancellation.
    /// </summary>
    /// <param name="orderedVisible">Visible items in priority order (already center-out).</param>
    /// <param name="bufferItems">Prefetch buffer items (outside immediate viewport).</param>
    /// <param name="width">Desired decode width.</param>
    public void UpdateViewport(IReadOnlyList<ImageMetadata> orderedVisible, IReadOnlyList<ImageMetadata> bufferItems, int width)
    {
        // Start new epoch; old queued tasks will be skipped when encountered
        int newEpoch = Interlocked.Increment(ref _epoch);

        // Recreate queues to drop large backlog quickly
        _high = new ConcurrentQueue<ThumbnailRequest>();
        _normal = new ConcurrentQueue<ThumbnailRequest>();

        // Visible high priority
        foreach (var m in orderedVisible)
            InternalEnqueue(m, width, highPriority: true, newEpoch, force: true);

        // Buffer (normal priority)
        foreach (var m in bufferItems)
            InternalEnqueue(m, width, highPriority: false, newEpoch, force: false);

        EnsureWorker();
    }

    private void InternalEnqueue(ImageMetadata meta, int width, bool highPriority, int epoch, bool force = false)
    {
        if (meta.FilePath == null) return;
        int existingWidth = meta.ThumbnailPixelWidth ?? 0;
        if (!force && existingWidth >= width) return;

        _maxRequestedWidth.AddOrUpdate(MakeFileKey(meta), width, (_, prev) => width > prev ? width : prev);
        if (!force && _maxRequestedWidth.TryGetValue(MakeFileKey(meta), out var maxReq) && maxReq > width)
            return; // a larger decode requested already

        var req = new ThumbnailRequest(meta, width, highPriority, epoch);
        if (highPriority) _high.Enqueue(req); else _normal.Enqueue(req);
    }

    private void EnsureWorker()
    {
        // Spawn until active reaches target
        while (!_cts.IsCancellationRequested)
        {
            int current = _activeWorkers;
            if (current >= _targetWorkers) break;
            if (Interlocked.CompareExchange(ref _activeWorkers, current + 1, current) == current)
            {
                _ = Task.Run(() => WorkerLoopAsync());
            }
        }
    }

    private async Task WorkerLoopAsync()
    {
        var token = _cts.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (!_high.TryDequeue(out var req) && !_normal.TryDequeue(out req))
                {
                    await Task.Delay(30, token).ConfigureAwait(false);
                    // If queues empty and workers exceed 1, allow this worker to retire
                    if (_high.IsEmpty && _normal.IsEmpty && _activeWorkers > 1) break;
                    continue;
                }
                // Skip if outdated epoch or already satisfied / superseded
                if (req.Epoch < _epoch) continue;
                if ((req.Meta.ThumbnailPixelWidth ?? 0) >= req.Width) continue;
                if (_maxRequestedWidth.TryGetValue(MakeFileKey(req.Meta), out var maxReq) && maxReq > req.Width) continue;

                try
                {
                    await _imageService.EnsureThumbnailAsync(req.Meta, req.Width, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch { }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            Interlocked.Decrement(ref _activeWorkers);
            // If work remains, attempt to spawn replacement
            if (!_cts.IsCancellationRequested && (!_high.IsEmpty || !_normal.IsEmpty)) EnsureWorker();
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        _cts.Dispose();
    }
}
