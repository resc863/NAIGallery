using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NAIGallery.Models;
using System;
using NAIGallery.Services; // for scheduler

namespace NAIGallery.Views;

public sealed partial class GalleryPage
{
    private static string MakeQueueKey(string path, int width) => $"{path}|{width}";

    private void EnqueueMeta(ImageMetadata meta, int decodeWidth, bool highPriority = false)
    {
        try { _thumbScheduler.Request(meta, decodeWidth, highPriority); } catch { }
    }

    private int GetDesiredDecodeWidth()
    {
        double size = _baseItemSize;
        double scale = XamlRoot?.RasterizationScale ?? 1.0;
        int px = (int)Math.Round(size * scale);
        return (int)Math.Clamp(Math.Round(px / 128.0) * 128.0, 128, 2048);
    }

    private async System.Threading.Tasks.Task ProcessQueueAsync()
    {
        // No-op now; scheduler runs independently
        await System.Threading.Tasks.Task.CompletedTask;
    }
}
