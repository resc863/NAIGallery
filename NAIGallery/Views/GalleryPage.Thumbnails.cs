using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NAIGallery.Models;
using System;
using NAIGallery.Services;

namespace NAIGallery.Views;

public sealed partial class GalleryPage
{
    private double _cachedRasterScale = 1.0; // thread-safe cached scale

    private void EnqueueMeta(ImageMetadata meta, int decodeWidth, bool highPriority = false)
    {
        try { (_service as ImageIndexService)?.Schedule(meta, decodeWidth, highPriority); }
        catch { }
    }

    private int GetDesiredDecodeWidth()
    {
        double size = _baseItemSize;
        double scale = 1.0;
        try
        {
            // Only access XamlRoot on UI thread to prevent COMException.
            if (DispatcherQueue?.HasThreadAccess == true)
            {
                scale = XamlRoot?.RasterizationScale ?? _cachedRasterScale;
                _cachedRasterScale = scale; // update cache
            }
            else
            {
                // Background thread: use last known value
                scale = _cachedRasterScale;
            }
        }
        catch { scale = _cachedRasterScale > 0 ? _cachedRasterScale : 1.0; }

        // Clamp and bucket to 128 multiples (same logic as before)
        int px = (int)Math.Round(size * scale);
        return (int)Math.Clamp(Math.Round(px / 128.0) * 128.0, 128, 2048);
    }

    private async System.Threading.Tasks.Task ProcessQueueAsync() => await System.Threading.Tasks.Task.CompletedTask;
}
