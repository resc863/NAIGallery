using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NAIGallery.Views;

public sealed partial class GalleryPage
{
    private void HookScroll()
    {
        try { _scrollViewer = RepeaterScroll; } catch { _scrollViewer = null; }
        if (_scrollViewer == null) return;
        _lastScrollOffset = _scrollViewer.VerticalOffset;
        _scrollViewer.ViewChanged += (s, args) =>
        {
            if (!_isLoaded) return;
            double current = _scrollViewer.VerticalOffset;
            _isScrollBubbling = args.IsIntermediate || Math.Abs(current - _lastScrollOffset) > 0.5;
            if (args.IsIntermediate)
            {
                EnqueueVisibleStrict();
                _ = ProcessQueueAsync();
            }
            else
            {
                ResetQueueForScroll();
                EnqueueVisibleStrict();
                _ = ProcessQueueAsync();
                _isScrollBubbling = false;
            }
            _lastScrollOffset = current;
        };
    }

    private void ResetQueueForScroll()
    {
        try { _thumbCts.Cancel(); } catch { }
        _thumbCts = new CancellationTokenSource();
        while (_thumbQueue.TryDequeue(out _)) { }
        while (_thumbHighQueue.TryDequeue(out _)) { }
        _queued.Clear();
    }

    private void RequestReflow(int delayMs = 80)
    {
        try { _reflowCts?.Cancel(); } catch { }
        _reflowCts = new CancellationTokenSource();
        var ct = _reflowCts.Token;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(delayMs, ct); } catch { return; }
            if (ct.IsCancellationRequested) return;
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    GalleryView?.InvalidateMeasure();
                    GalleryView?.InvalidateArrange();
                    EnqueueVisibleStrict();
                    _ = ProcessQueueAsync();
                }
                catch { }
            });
        });
    }
}
