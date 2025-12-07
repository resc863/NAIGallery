using System;
using System.Collections.Concurrent;
using System.Threading;

namespace NAIGallery.Services.Thumbnails;

/// <summary>
/// Dedicated thread pool separated from the main ThreadPool to prevent UI thread starvation.
/// </summary>
internal sealed class DedicatedThreadPool : IDisposable
{
    private readonly Thread[] _threads;
    private readonly BlockingCollection<Action> _workQueue;
    private readonly CancellationTokenSource _cts;
    private volatile bool _disposed;

    public DedicatedThreadPool(int threadCount, string namePrefix)
    {
        _cts = new CancellationTokenSource();
        _workQueue = new BlockingCollection<Action>(new ConcurrentQueue<Action>(), 1024);
        _threads = new Thread[threadCount];

        for (int i = 0; i < threadCount; i++)
        {
            _threads[i] = new Thread(WorkerLoop)
            {
                Name = $"{namePrefix}_{i}",
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal
            };
            _threads[i].Start();
        }
    }

    public void QueueWork(Action work)
    {
        if (_disposed) return;

        try
        {
            // TryAdd with timeout to prevent blocking
            if (!_workQueue.TryAdd(work, 10))
            {
                // Queue full, run on thread pool as fallback
                ThreadPool.QueueUserWorkItem(_ => work());
            }
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private void WorkerLoop()
    {
        try
        {
            foreach (var work in _workQueue.GetConsumingEnumerable(_cts.Token))
            {
                try
                {
                    work();
                }
                catch
                {
                    // Swallow exceptions to keep worker alive
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _cts.Cancel(); } catch { }
        try { _workQueue.CompleteAdding(); } catch { }

        // Wait briefly for threads to finish
        foreach (var thread in _threads)
        {
            try { thread.Join(100); } catch { }
        }

        try { _workQueue.Dispose(); } catch { }
        try { _cts.Dispose(); } catch { }
    }
}
