using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace NAIGallery.Services.Thumbnails;

/// <summary>
/// Handles the decoding of image files to pixel data.
/// </summary>
internal static class ThumbnailDecoder
{
    /// <summary>
    /// Decodes an image file to a PixelEntry at the specified width.
    /// </summary>
    public static async Task<PixelEntry?> DecodeFileAsync(string filePath, int targetWidth, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return null;

        try
        {
            using var fs = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using IRandomAccessStream ras = fs.AsRandomAccessStream();
            return await DecodeStreamAsync(ras, targetWidth, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Telemetry.DecodeCanceled.Add(1);
            return null;
        }
        catch (IOException)
        {
            Telemetry.DecodeIoErrors.Add(1);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            Telemetry.DecodeIoErrors.Add(1);
            return null;
        }
        catch (COMException comEx)
        {
            HandleComException(comEx);
            return null;
        }
        catch
        {
            Telemetry.DecodeUnknownErrors.Add(1);
            return null;
        }
    }

    /// <summary>
    /// Decodes a stream to a PixelEntry at the specified width.
    /// </summary>
    public static async Task<PixelEntry?> DecodeStreamAsync(IRandomAccessStream ras, int targetWidth, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return null;

        try
        {
            var sb = await DecodeToSoftwareBitmapAsync(ras, targetWidth, ct).ConfigureAwait(false);
            if (sb == null) return null;

            return ConvertToPixelEntry(sb);
        }
        catch (OperationCanceledException)
        {
            Telemetry.DecodeCanceled.Add(1);
            return null;
        }
        catch (COMException comEx)
        {
            HandleComException(comEx);
            return null;
        }
        catch
        {
            Telemetry.DecodeUnknownErrors.Add(1);
            return null;
        }
    }

    /// <summary>
    /// Extracts original dimensions from an image file without full decode.
    /// </summary>
    public static async Task<(int Width, int Height)?> GetDimensionsAsync(string filePath, CancellationToken ct)
    {
        try
        {
            using var fs = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using IRandomAccessStream ras = fs.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(ras);
            return ((int)decoder.PixelWidth, (int)decoder.PixelHeight);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<SoftwareBitmap?> DecodeToSoftwareBitmapAsync(IRandomAccessStream ras, int targetWidth, CancellationToken ct)
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
                InterpolationMode = BitmapInterpolationMode.Linear // Faster than Fant
            };

            try
            {
                return await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.DoNotColorManage); // Skip color management for speed
            }
            catch (COMException)
            {
                // Fallback for formats that don't support direct BGRA8 conversion
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

    private static PixelEntry? ConvertToPixelEntry(SoftwareBitmap sb)
    {
        try
        {
            int pxCount = (int)(sb.PixelWidth * sb.PixelHeight * 4);
            byte[] rented = ArrayPool<byte>.Shared.Rent(pxCount);

            try
            {
                sb.CopyToBuffer(rented.AsBuffer());
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(rented);
                Telemetry.DecodeFormatErrors.Add(1);
                return null;
            }
            finally
            {
                try { sb.Dispose(); } catch { }
            }

            return new PixelEntry
            {
                Pixels = rented,
                W = (int)sb.PixelWidth,
                H = (int)sb.PixelHeight,
                Rented = rented.Length
            };
        }
        catch (OutOfMemoryException)
        {
            Telemetry.ComOutOfMemory.Add(1);
            try { sb.Dispose(); } catch { }
            return null;
        }
    }

    public static void HandleComException(COMException comEx)
    {
        switch ((uint)comEx.HResult)
        {
            case 0x88982F50:
            case 0x88982F44:
                Telemetry.ComUnsupportedFormat.Add(1);
                break;
            case 0x8007000E:
            case 0x88982F07:
                Telemetry.ComOutOfMemory.Add(1);
                break;
            case 0x80070005:
                Telemetry.ComAccessDenied.Add(1);
                break;
            case 0x88982F81:
                Telemetry.ComWrongState.Add(1);
                break;
            case 0x887A0005:
            case 0x887A0006:
                Telemetry.ComDeviceLost.Add(1);
                break;
            default:
                Telemetry.ComOther.Add(1);
                break;
        }
    }
}
