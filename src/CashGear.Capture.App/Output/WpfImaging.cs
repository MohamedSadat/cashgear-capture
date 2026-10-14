using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CashGear.Capture.Core.Imaging;

namespace CashGear.Capture.App.Output;

/// <summary>The WPF edge for <see cref="CapturedImage"/>: conversion to frozen bitmaps and WIC encoding.</summary>
internal static class WpfImaging
{
    /// <summary>
    /// Copies the pixels into a frozen (thread-agnostic) Bgra32 bitmap. 96 DPI on purpose: Office and
    /// browsers scale images by their DPI tag, and a capture should open at 1 image pixel = 1 screen pixel.
    /// </summary>
    public static unsafe BitmapSource ToBitmapSource(this CapturedImage image)
    {
        BitmapSource bitmap;
        fixed (byte* pixels = image.Pixels)
        {
            bitmap = BitmapSource.Create(
                image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, (nint)pixels, image.ByteLength, image.Stride);
        }

        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// Encodes <paramref name="bitmap"/>. PNG keeps alpha only when <paramref name="keepAlpha"/> (transparent gaps
    /// between monitors); everything else is written as 24-bit, where transparent pixels become black.
    /// </summary>
    public static void Encode(BitmapSource bitmap, ImageFileFormat format, int jpegQuality, bool keepAlpha, Stream output)
    {
        BitmapEncoder encoder = format switch
        {
            ImageFileFormat.Png => new PngBitmapEncoder(),
            ImageFileFormat.Jpeg => new JpegBitmapEncoder { QualityLevel = Math.Clamp(jpegQuality, 1, 100) },
            ImageFileFormat.Bmp => new BmpBitmapEncoder(),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
        };

        var frame = format == ImageFileFormat.Png && keepAlpha
            ? bitmap
            : new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0);
        encoder.Frames.Add(BitmapFrame.Create(frame));
        encoder.Save(output);
    }

    /// <summary>
    /// A packed CF_DIB: 40-byte BITMAPINFOHEADER (32 bpp, BI_RGB) followed by bottom-up rows — the format
    /// classic Win32 apps such as Paint read most reliably.
    /// </summary>
    public static byte[] ToPackedDib(CapturedImage image)
    {
        const int headerSize = 40;
        var dib = new byte[headerSize + image.ByteLength];
        var header = dib.AsSpan(0, headerSize);
        BitConverter.TryWriteBytes(header[0..], headerSize); // biSize
        BitConverter.TryWriteBytes(header[4..], image.Width); // biWidth
        BitConverter.TryWriteBytes(header[8..], image.Height); // biHeight > 0 = bottom-up
        BitConverter.TryWriteBytes(header[12..], (ushort)1); // biPlanes
        BitConverter.TryWriteBytes(header[14..], (ushort)32); // biBitCount
        BitConverter.TryWriteBytes(header[20..], image.ByteLength); // biSizeImage (biCompression = BI_RGB = 0)

        var body = dib.AsSpan(headerSize);
        for (var y = 0; y < image.Height; y++)
        {
            image.GetRow(y).CopyTo(body.Slice((image.Height - 1 - y) * image.Stride, image.Stride));
        }

        return dib;
    }
}
