using System.Buffers.Binary;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Editor.Imaging;
using SkiaSharp;

namespace CashGear.Capture.Editor.Batch;

/// <summary>Reading and writing picture files with Skia, off the UI thread (no WPF, no WIC).</summary>
public static class ImageFiles
{
    /// <summary>The extensions the batch processor, Combine and the PDF tool read.</summary>
    public static IReadOnlyList<string> ReadableExtensions { get; } = [".png", ".jpg", ".jpeg", ".jpe", ".bmp", ".dib", ".gif", ".webp"];

    /// <summary>For open-file dialogs: "Pictures|*.png;*.jpg;…".</summary>
    public static string DialogFilter => "Pictures|" + string.Join(';', ReadableExtensions.Select(e => "*" + e)) + "|All files|*.*";

    public static bool IsReadable(string path) =>
        ReadableExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reads a picture, turned upright as its EXIF orientation says (photos). The file is read into memory first,
    /// so it isn't kept open (and can be replaced by the result). The caller owns the image.
    /// </summary>
    public static SKImage Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var data = SKData.CreateCopy(File.ReadAllBytes(path));
        using var codec = SKCodec.Create(data) ?? throw new InvalidDataException($"{Path.GetFileName(path)} isn't a picture this app can read.");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} could not be read ({result}).");
        }

        var decoded = SKImage.FromPixelCopy(info, bitmap.GetPixels(), bitmap.RowBytes);
        if (codec.EncodedOrigin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
        {
            return decoded;
        }

        using (decoded)
        {
            return Upright(decoded, codec.EncodedOrigin);
        }
    }

    /// <summary>The image as its EXIF orientation says it should be seen.</summary>
    internal static SKImage Upright(SKImage image, SKEncodedOrigin origin)
    {
        float w = image.Width;
        float h = image.Height;
        var (matrix, swap) = origin switch
        {
            SKEncodedOrigin.TopRight => (new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1), false),
            SKEncodedOrigin.BottomRight => (new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1), false),
            SKEncodedOrigin.BottomLeft => (new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1), false),
            SKEncodedOrigin.LeftTop => (new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1), true),
            SKEncodedOrigin.RightTop => (new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1), true),
            SKEncodedOrigin.RightBottom => (new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1), true),
            SKEncodedOrigin.LeftBottom => (new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1), true),
            _ => (SKMatrix.Identity, false),
        };
        return SkiaImaging.Draw(swap ? image.Height : image.Width, swap ? image.Width : image.Height, c =>
        {
            c.SetMatrix(in matrix);
            c.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), null);
        });
    }

    /// <summary>The format a file is saved in when the batch keeps formats: PNG for anything that isn't PNG, JPEG or BMP.</summary>
    public static ImageFileFormat KeptFormat(string path) => ImageFileFormatExtensions.FromPath(path) ?? ImageFileFormat.Png;

    /// <summary>
    /// Writes the image. PNG keeps transparency; JPEG and BMP (24-bit, as the app saves them) get white behind
    /// transparent parts.
    /// </summary>
    public static void Write(SKImage image, Stream output, ImageFileFormat format, int jpegQuality = 90)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(output);
        if (format == ImageFileFormat.Png)
        {
            using var png = image.Encode(SKEncodedImageFormat.Png, 100) ?? throw new InvalidOperationException("Could not encode the PNG.");
            png.SaveTo(output);
            return;
        }

        using var opaque = SkiaImaging.Draw(image.Width, image.Height, c =>
        {
            c.Clear(SKColors.White);
            c.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), null);
        });
        if (format == ImageFileFormat.Jpeg)
        {
            using var jpeg = opaque.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(jpegQuality, 1, 100)) ?? throw new InvalidOperationException("Could not encode the JPEG.");
            jpeg.SaveTo(output);
            return;
        }

        WriteBmp(opaque, output);
    }

    /// <summary>A 24-bit bottom-up BMP (Skia has no BMP encoder).</summary>
    private static void WriteBmp(SKImage image, Stream output)
    {
        var width = image.Width;
        var height = image.Height;
        var rowSize = ((width * 3) + 3) & ~3;
        var pixels = SkiaImaging.ReadUnpremul(image);
        Span<byte> header = stackalloc byte[54];
        header[0] = (byte)'B';
        header[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(header[2..], 54 + (rowSize * height));
        BinaryPrimitives.WriteInt32LittleEndian(header[10..], 54);
        BinaryPrimitives.WriteInt32LittleEndian(header[14..], 40);
        BinaryPrimitives.WriteInt32LittleEndian(header[18..], width);
        BinaryPrimitives.WriteInt32LittleEndian(header[22..], height);
        BinaryPrimitives.WriteInt16LittleEndian(header[26..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(header[28..], 24);
        BinaryPrimitives.WriteInt32LittleEndian(header[34..], rowSize * height);
        BinaryPrimitives.WriteInt32LittleEndian(header[38..], 3780); // 96 dpi in pixels per metre
        BinaryPrimitives.WriteInt32LittleEndian(header[42..], 3780);
        output.Write(header);

        var row = new byte[rowSize];
        for (var y = height - 1; y >= 0; y--)
        {
            var source = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                row[x * 3] = pixels[source + (x * 4)];
                row[(x * 3) + 1] = pixels[source + (x * 4) + 1];
                row[(x * 3) + 2] = pixels[source + (x * 4) + 2];
            }

            output.Write(row);
        }
    }
}
