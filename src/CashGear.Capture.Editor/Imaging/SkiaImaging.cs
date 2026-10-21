using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Imaging;
using SkiaSharp;

namespace CashGear.Capture.Editor.Imaging;

/// <summary>
/// The Skia edge for <see cref="CapturedImage"/>. Captures are straight-alpha BGRA; Skia works premultiplied, so
/// pixels are converted on the way in and out (exact for opaque pixels, which is nearly all of a capture).
/// </summary>
public static class SkiaImaging
{
    /// <summary>A premultiplied copy of <paramref name="image"/>'s pixels (the capture can be disposed afterwards).</summary>
    public static unsafe SKImage ToSKImage(CapturedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var source = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(source.WithAlphaType(SKAlphaType.Premul));
        fixed (byte* pixels = image.Pixels)
        {
            using var pixmap = new SKPixmap(source, (nint)pixels, image.Stride);
            if (!pixmap.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0))
            {
                throw new InvalidOperationException("Could not convert the capture for the editor.");
            }
        }

        return SKImage.FromPixelCopy(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes);
    }

    /// <summary>
    /// The pixels of <paramref name="image"/> as a capture with <paramref name="metadata"/>: the size follows the image
    /// and <see cref="CaptureMetadata.HasTransparency"/> says whether any pixel is not fully opaque.
    /// </summary>
    public static unsafe CapturedImage ToCapturedImage(SKImage image, CaptureMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(metadata);
        var result = CapturedImage.Allocate(image.Width, image.Height, metadata, clear: false);
        try
        {
            var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            fixed (byte* pixels = result.Pixels)
            {
                if (!image.ReadPixels(info, (nint)pixels, result.Stride, 0, 0))
                {
                    throw new InvalidOperationException("Could not read the edited image.");
                }
            }

            var source = metadata.SourceBounds;
            result.Metadata = metadata with
            {
                SourceBounds = new PixelRect(source.X, source.Y, image.Width, image.Height),
                HasTransparency = HasTransparency(result.Pixels),
            };
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private static bool HasTransparency(ReadOnlySpan<byte> bgra)
    {
        for (var i = 3; i < bgra.Length; i += 4)
        {
            if (bgra[i] != 255)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The image's pixels as straight-alpha BGRA bytes, row after row with no padding.</summary>
    public static unsafe byte[] ReadUnpremul(SKImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var bytes = new byte[image.Width * image.Height * 4];
        fixed (byte* p = bytes)
        {
            if (!image.ReadPixels(info, (nint)p, image.Width * 4, 0, 0))
            {
                throw new InvalidOperationException("Could not read the image's pixels.");
            }
        }

        return bytes;
    }

    /// <summary>A premultiplied image from straight-alpha BGRA bytes (the inverse of <see cref="ReadUnpremul"/>).</summary>
    public static unsafe SKImage FromUnpremul(byte[] bgra, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        var source = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(source.WithAlphaType(SKAlphaType.Premul));
        fixed (byte* pixels = bgra)
        {
            using var pixmap = new SKPixmap(source, (nint)pixels, width * 4);
            if (!pixmap.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0))
            {
                throw new InvalidOperationException("Could not convert the pixels.");
            }
        }

        return SKImage.FromPixelCopy(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes);
    }

    /// <summary>A copy of <paramref name="area"/> of the image, pixel for pixel.</summary>
    public static SKImage Copy(SKImage image, SKRectI area)
    {
        ArgumentNullException.ThrowIfNull(image);
        return Draw(area.Width, area.Height, c => c.DrawImage(image, -area.Left, -area.Top, new SKSamplingOptions(SKFilterMode.Nearest), null));
    }

    /// <summary>Draws into a new transparent raster image of the given size.</summary>
    public static SKImage Draw(int width, int height, Action<SKCanvas> draw)
    {
        ArgumentNullException.ThrowIfNull(draw);
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info) ?? throw new InvalidOperationException($"Could not allocate a {width}x{height} image.");
        surface.Canvas.Clear(SKColors.Transparent);
        draw(surface.Canvas);
        return surface.Snapshot();
    }
}
