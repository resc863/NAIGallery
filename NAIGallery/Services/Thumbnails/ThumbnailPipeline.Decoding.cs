using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using NAIGallery.Models;
using NAIGallery.Services.Thumbnails;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace NAIGallery.Services;

internal sealed partial class ThumbnailPipeline
{
    #region Decoding
    
    private async Task LoadDecodeAsync(ImageMetadata meta, int width, long ticks, CancellationToken ct, int gen, bool allowDownscale)
    {
        string key = MakeCacheKey(meta.FilePath, ticks, width);
        if (_cache.TryGet(key, out var cached))
        {
            EnqueueApply(meta, cached!, width, gen, allowDownscale);
            return;
        }

        if (!_inflight.TryAdd(key, 0)) return;

        var sw = Stopwatch.StartNew();
        try
        {
            await _decodeGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var fs = File.Open(meta.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using IRandomAccessStream ras = fs.AsRandomAccessStream();

                var decodeSizeTask = Task.CompletedTask;
                if (!meta.OriginalWidth.HasValue || !meta.OriginalHeight.HasValue)
                {
                    decodeSizeTask = Task.Run(async () =>
                    {
                        try
                        {
                            using var fs2 = File.Open(meta.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                            using IRandomAccessStream ras2 = fs2.AsRandomAccessStream();
                            var decoder = await BitmapDecoder.CreateAsync(ras2);
                            meta.OriginalWidth = (int)decoder.PixelWidth;
                            meta.OriginalHeight = (int)decoder.PixelHeight;
                        }
                        catch { }
                    }, ct);
                }

                var sb = await DecodeAsync(ras, width, ct).ConfigureAwait(false);
                
                await decodeSizeTask.ConfigureAwait(false);
                
                if (sb == null || ct.IsCancellationRequested)
                {
                    if (ct.IsCancellationRequested)
                    {
                        Interlocked.Increment(ref _canceled);
                        Telemetry.DecodeCanceled.Add(1);
                    }
                    else
                    {
                        Interlocked.Increment(ref _formatErrors);
                        Telemetry.DecodeFormatErrors.Add(1);
                    }
                    return;
                }

                int pxCount = (int)(sb.PixelWidth * sb.PixelHeight * 4);
                byte[] rented;
                try
                {
                    rented = ArrayPool<byte>.Shared.Rent(pxCount);
                }
                catch (OutOfMemoryException)
                {
                    _memoryPressure = true;
                    try { sb.Dispose(); } catch { }
                    return;
                }

                try
                {
                    sb.CopyToBuffer(rented.AsBuffer());
                }
                catch
                {
                    ArrayPool<byte>.Shared.Return(rented);
                    Interlocked.Increment(ref _formatErrors);
                    Telemetry.DecodeFormatErrors.Add(1);
                    try { sb.Dispose(); } catch { }
                    return;
                }

                var entry = new PixelEntry
                {
                    Pixels = rented,
                    W = (int)sb.PixelWidth,
                    H = (int)sb.PixelHeight,
                    Rented = rented.Length
                };
                try { sb.Dispose(); } catch { }

                _cache.Add(key, entry);
                EnqueueApply(meta, entry, width, gen, allowDownscale);
            }
            catch (IOException)
            {
                Interlocked.Increment(ref _ioErrors);
                Telemetry.DecodeIoErrors.Add(1);
            }
            catch (UnauthorizedAccessException)
            {
                Interlocked.Increment(ref _ioErrors);
                Telemetry.DecodeIoErrors.Add(1);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref _canceled);
                Telemetry.DecodeCanceled.Add(1);
            }
            catch (COMException comEx)
            {
                HandleComException(comEx, meta.FilePath);
            }
            catch (OutOfMemoryException)
            {
                _memoryPressure = true;
                Interlocked.Increment(ref _comOutOfMemory);
                Telemetry.ComOutOfMemory.Add(1);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _unknownErrors);
                Telemetry.DecodeUnknownErrors.Add(1);
                _logger?.LogDebug(ex, "Decode error {File}", meta.FilePath);
            }
            finally
            {
                _decodeGate.Release();
            }
        }
        finally
        {
            sw.Stop();
            Telemetry.DecodeLatencyMs.Record(sw.Elapsed.TotalMilliseconds);
            _inflight.TryRemove(key, out _);
        }
    }

    private void HandleComException(COMException comEx, string filePath)
    {
        switch ((uint)comEx.HResult)
        {
            case 0x88982F50:
            case 0x88982F44:
                Interlocked.Increment(ref _comUnsupportedFormat);
                Telemetry.ComUnsupportedFormat.Add(1);
                break;
            case 0x8007000E:
            case 0x88982F07:
                _memoryPressure = true;
                Interlocked.Increment(ref _comOutOfMemory);
                Telemetry.ComOutOfMemory.Add(1);
                break;
            case 0x80070005:
                Interlocked.Increment(ref _comAccessDenied);
                Telemetry.ComAccessDenied.Add(1);
                break;
            case 0x88982F81:
                Interlocked.Increment(ref _comWrongState);
                Telemetry.ComWrongState.Add(1);
                break;
            case 0x887A0005:
            case 0x887A0006:
                Interlocked.Increment(ref _comDeviceLost);
                Telemetry.ComDeviceLost.Add(1);
                break;
            default:
                Interlocked.Increment(ref _comOther);
                Telemetry.ComOther.Add(1);
                break;
        }
        _logger?.LogDebug(comEx, "COM decode error {HResult:X8} {File}", comEx.HResult, filePath);
    }

    private static async Task<SoftwareBitmap?> DecodeAsync(IRandomAccessStream ras, int targetWidth, CancellationToken ct)
    {
        try
        {
            var decoder = await BitmapDecoder.CreateAsync(ras);
            uint sw = decoder.PixelWidth, sh = decoder.PixelHeight;
            if (sw == 0 || sh == 0) return null;

            double scale = Math.Min(1.0, targetWidth / (double)sw);
            uint ow = (uint)Math.Max(1, Math.Round(sw * scale));
            uint oh = (uint)Math.Max(1, Math.Round(sh * scale));

            var transform = new BitmapTransform
            {
                ScaledWidth = ow,
                ScaledHeight = oh,
                InterpolationMode = BitmapInterpolationMode.Linear
            };

            try
            {
                return await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.DoNotColorManage);
            }
            catch (COMException)
            {
                var sb = await decoder.GetSoftwareBitmapAsync(
                    decoder.BitmapPixelFormat,
                    decoder.BitmapAlphaMode,
                    transform,
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.DoNotColorManage);

                if (sb.BitmapPixelFormat != BitmapPixelFormat.Bgra8 ||
                    sb.BitmapAlphaMode != BitmapAlphaMode.Premultiplied)
                {
                    var conv = SoftwareBitmap.Convert(sb, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                    sb.Dispose();
                    sb = conv;
                }
                return sb;
            }
        }
        catch (OperationCanceledException) { return null; }
        catch { return null; }
    }
    
    #endregion
}
