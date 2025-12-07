using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using NAIGallery.Models;
using NAIGallery.Services.Thumbnails;

namespace NAIGallery.Services;

internal sealed partial class ThumbnailPipeline
{
    #region Apply Queue
    
    // Apply 처리 간격 제어
    private long _lastApplyBatchTicks = 0;
    private const long MinApplyBatchIntervalTicks = TimeSpan.TicksPerMillisecond * 8; // 최소 8ms 간격
    
    private void EnqueueApply(ImageMetadata meta, PixelEntry entry, int width, int gen, bool allowDownscale)
    {
        int latest = _fileGeneration.TryGetValue(meta.FilePath, out var g) ? g : gen;
        if (gen < latest - 5 && !allowDownscale) return;

        _applyQ.Enqueue((meta, entry, width, gen));
        ScheduleDrain();
    }

    private void ScheduleDrain()
    {
        if (_dispatcher == null) return;
        
        if (Interlocked.CompareExchange(ref _applyScheduled, 1, 0) != 0) return;

        bool enqueued = _dispatcher.TryEnqueue(DispatcherQueuePriority.Normal, async () =>
        {
            try
            {
                // 이전 배치 처리 후 최소 간격 확보
                long now = Stopwatch.GetTimestamp();
                long elapsed = now - _lastApplyBatchTicks;
                if (elapsed < MinApplyBatchIntervalTicks)
                {
                    await Task.Yield();
                }
                _lastApplyBatchTicks = Stopwatch.GetTimestamp();
                
                int processed = 0;
                // UI가 바쁘거나 일시 중지 중이면 더 작은 배치로 처리
                int batchSize = _applySuspended ? 4 : (_uiBusy ? 6 : 12);

                while (_applyQ.TryDequeue(out var item) && processed < batchSize)
                {
                    try
                    {
                        await TryApplyFromEntryAsync(item.Meta, item.Entry, item.Width, item.Gen, false);
                    }
                    catch (System.Runtime.InteropServices.COMException comEx)
                    {
                        HandleComException(comEx, item.Meta.FilePath);
                    }
                    catch (OutOfMemoryException)
                    {
                        _memoryPressure = true;
                        _applyQ.Enqueue(item);
                        break;
                    }

                    processed++;
                    
                    // 배치 중간에 yield하여 UI 응답성 유지
                    if (processed % 4 == 0)
                    {
                        await Task.Yield();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Drain error");
            }
            finally
            {
                Interlocked.Exchange(ref _applyScheduled, 0);
                if (!_applyQ.IsEmpty && !_applySuspended)
                {
                    // 다음 배치 전에 약간의 지연
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(5);
                        ScheduleDrain();
                    });
                }
            }
        });
        
        if (!enqueued)
        {
            Interlocked.Exchange(ref _applyScheduled, 0);
        }
    }

    private async Task TryApplyFromEntryAsync(ImageMetadata meta, PixelEntry entry, int width, int gen, bool allowDownscale)
    {
        int latest = _fileGeneration.TryGetValue(meta.FilePath, out var g) ? g : gen;
        if (gen < latest - 5 && !allowDownscale && meta.Thumbnail != null && (meta.ThumbnailPixelWidth ?? 0) >= width) return;

        // 비트맵 생성 간격 제어
        long now = Stopwatch.GetTimestamp();
        long last = Interlocked.Read(ref _lastBitmapCreationTicks);
        long elapsed = now - last;
        if (elapsed < MinBitmapCreationIntervalTicks * 2)
        {
            await Task.Yield();
        }
        Interlocked.Exchange(ref _lastBitmapCreationTicks, Stopwatch.GetTimestamp());

        bool acquired = await _bitmapCreationGate.WaitAsync(200); // 타임아웃 축소
        if (!acquired)
        {
            _applyQ.Enqueue((meta, entry, width, gen));
            return;
        }

        try
        {
            if (entry.W <= 0 || entry.H <= 0 || entry.W > 8192 || entry.H > 8192)
            {
                return;
            }

            WriteableBitmap? wb = null;
            try
            {
                wb = new WriteableBitmap(entry.W, entry.H);
                var buffer = wb.PixelBuffer;
                int expected = entry.W * entry.H * 4;
                int capacity = (int)buffer.Capacity;
                int toWrite = Math.Min(expected, Math.Min(capacity, entry.Rented));

                using var s = buffer.AsStream();
                s.Write(entry.Pixels, 0, toWrite);
                wb.Invalidate();
            }
            catch (System.Runtime.InteropServices.COMException comEx)
            {
                HandleComException(comEx, meta.FilePath);
                return;
            }
            catch (OutOfMemoryException)
            {
                _memoryPressure = true;
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "WriteableBitmap creation failed for {File}", meta.FilePath);
                return;
            }

            if (wb != null && (meta.Thumbnail == null || width >= (meta.ThumbnailPixelWidth ?? 0)))
            {
                meta.Thumbnail = wb;
                meta.ThumbnailPixelWidth = width;
                meta.IsLoadingThumbnail = false;
                
                try { ThumbnailApplied?.Invoke(meta); } catch { }
            }

            if (!meta.OriginalWidth.HasValue || !meta.OriginalHeight.HasValue)
            {
                if (entry.W > 0 && entry.H > 0)
                {
                    double ar = Math.Clamp(entry.W / (double)Math.Max(1, entry.H), 0.1, 10.0);
                    if (Math.Abs(ar - meta.AspectRatio) > 0.001)
                    {
                        meta.AspectRatio = ar;
                    }
                }
            }
        }
        finally
        {
            _bitmapCreationGate.Release();
        }
    }
    
    #endregion
}
