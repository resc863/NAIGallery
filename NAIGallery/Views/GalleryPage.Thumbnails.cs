using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NAIGallery.Models;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

namespace NAIGallery.Views;

public sealed partial class GalleryPage
{
    private static string MakeQueueKey(string path, int width) => $"{path}|{width}";

    private void EnqueueMeta(ImageMetadata meta, int decodeWidth, bool highPriority = false)
    {
        if (meta.FilePath == null) return;
        var key = MakeQueueKey(meta.FilePath, decodeWidth);
        var existingKeys = _queued.Keys.Where(k => k.StartsWith(meta.FilePath + "|", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var k in existingKeys)
        {
            var parts = k.Split('|');
            if (parts.Length == 2 && int.TryParse(parts[1], out int w) && w >= decodeWidth) return;
        }
        if (highPriority)
        {
            if (_queued.ContainsKey(key)) { _thumbHighQueue.Enqueue((meta, decodeWidth)); return; }
            if (_queued.TryAdd(key, 0)) _thumbHighQueue.Enqueue((meta, decodeWidth));
            return;
        }
        if (!_queued.TryAdd(key, 0)) return;
        _thumbQueue.Enqueue((meta, decodeWidth));
    }

    private int GetDesiredDecodeWidth()
    {
        double size = _baseItemSize;
        double scale = XamlRoot?.RasterizationScale ?? 1.0;
        int px = (int)Math.Round(size * scale);
        return (int)Math.Clamp(Math.Round(px / 128.0) * 128.0, 128, 2048);
    }

    private async Task ProcessQueueAsync()
    {
        await _queueRunnerGate.WaitAsync();
        try
        {
            var token = _thumbCts.Token;
            var tasks = new List<Task>();
            while (!token.IsCancellationRequested)
            {
                (ImageMetadata Meta, int Width) item;
                if (_thumbHighQueue.TryDequeue(out item)) { }
                else if (_thumbQueue.TryDequeue(out item)) { }
                else break;

                await _sequentialGate.WaitAsync().ConfigureAwait(false);
                var meta = item.Meta; var width = item.Width;
                var key = MakeQueueKey(meta.FilePath, width);
                var t = Task.Run(async () =>
                {
                    try
                    {
                        if (!token.IsCancellationRequested)
                            await _service.EnsureThumbnailAsync(meta, width, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { Debug.WriteLine($"[Thumb][ERR] {meta.FilePath} {ex.Message}"); }
                    finally
                    {
                        _sequentialGate.Release();
                        _queued.TryRemove(key, out _);
                    }
                });
                tasks.Add(t);
            }
            if (tasks.Count > 0) { try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { } }
        }
        finally { _queueRunnerGate.Release(); }
    }
}
