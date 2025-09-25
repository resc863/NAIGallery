using Microsoft.UI.Xaml.Input;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NAIGallery.Views;

public sealed partial class GalleryPage
{
    private async Task PrimeInitialAsync()
    {
        if (!await _primeGate.WaitAsync(0)) return;
        try
        {
            if (_initialPrimed || ViewModel.Images.Count == 0) return;
            EnqueueVisibleStrict();
            var desired = GetDesiredDecodeWidth();
            CancelPreloading();
            var slice = ViewModel.Images.Skip(_viewStartIndex).Take(Math.Max(1, (_viewEndIndex - _viewStartIndex + 1) + 30)).ToList();
            try { await _service.PreloadThumbnailsAsync(slice, desired, _preloadCts!.Token); } catch { }
            EnqueueVisibleStrict();
            _ = ProcessQueueAsync();
            _initialPrimed = true;
        }
        finally { _primeGate.Release(); }
    }

    private void CancelPreloading()
    {
        try { _preloadCts?.Cancel(); } catch { }
        _preloadCts = new CancellationTokenSource();
    }

    private async void Root_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if ((e.KeyModifiers & Windows.System.VirtualKeyModifiers.Control) == 0) return;
        var delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        double factor = delta > 0 ? 1.1 : 0.9;
        double oldSize = _baseItemSize;
        double newSize = Math.Clamp(oldSize * factor, _minSize, _maxSize);
        if (Math.Abs(newSize - oldSize) < 0.5) { e.Handled = true; return; }
        AnimateZoomTiles(oldSize, newSize, e.GetCurrentPoint(this).Position);
        AdjustScrollForZoom(oldSize, newSize, e);
        _baseItemSize = newSize; e.Handled = true;
        SuppressImplicitBriefly(280);
        CancelPreloading(); ResetQueueForScroll(); EnqueueVisibleStrict();
        try { await _service.PreloadThumbnailsAsync(ViewModel.Images.Skip(_viewStartIndex).Take(Math.Max(1, _viewEndIndex - _viewStartIndex + 1)), GetDesiredDecodeWidth(), _preloadCts!.Token); } catch { }
        EnqueueVisibleStrict(); _ = ProcessQueueAsync();
        var debounceCts = new CancellationTokenSource(); var ct = debounceCts.Token;
        _ = Task.Run(async () => { try { await Task.Delay(80, ct); } catch { return; } if (!ct.IsCancellationRequested) DispatcherQueue.TryEnqueue(ApplyItemSize); });
    }
}
