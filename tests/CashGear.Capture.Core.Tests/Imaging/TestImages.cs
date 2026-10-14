using System.Buffers;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Imaging;

namespace CashGear.Capture.Core.Tests.Imaging;

internal static class TestImages
{
    public static CaptureMetadata Metadata(PixelRect bounds) =>
        new() { Kind = CaptureKind.Region, SourceBounds = bounds };

    /// <summary>An image whose every pixel is BGRA (b, g, r, a).</summary>
    public static CapturedImage Solid(int width, int height, byte b, byte g, byte r, byte a = 255, ArrayPool<byte>? pool = null)
    {
        var image = CapturedImage.Allocate(width, height, Metadata(new PixelRect(0, 0, width, height)), pool);
        var pixels = image.Pixels;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = a;
        }

        return image;
    }

    /// <summary>An image where pixel (x, y) is BGRA (x, y, tag, 255) — position-identifiable.</summary>
    public static CapturedImage Coordinates(int width, int height, byte tag)
    {
        var image = CapturedImage.Allocate(width, height, Metadata(new PixelRect(0, 0, width, height)));
        for (var y = 0; y < height; y++)
        {
            var row = image.GetRow(y);
            for (var x = 0; x < width; x++)
            {
                row[x * 4] = (byte)x;
                row[x * 4 + 1] = (byte)y;
                row[x * 4 + 2] = tag;
                row[x * 4 + 3] = 255;
            }
        }

        return image;
    }

    public static (byte B, byte G, byte R, byte A) PixelAt(CapturedImage image, int x, int y)
    {
        var row = image.GetRow(y);
        return (row[x * 4], row[x * 4 + 1], row[x * 4 + 2], row[x * 4 + 3]);
    }
}

/// <summary>Counts rentals/returns and hands out dirty buffers so missing clears show up.</summary>
internal sealed class SpyPool : ArrayPool<byte>
{
    public int Rented { get; private set; }

    public int Returned { get; private set; }

    public override byte[] Rent(int minimumLength)
    {
        Rented++;
        var buffer = new byte[minimumLength + 32];
        buffer.AsSpan().Fill(0xAB);
        return buffer;
    }

    public override void Return(byte[] array, bool clearArray = false) => Returned++;
}
