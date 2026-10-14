using System.Buffers;
using CashGear.Capture.Core.Geometry;

namespace CashGear.Capture.Core.Imaging;

/// <summary>
/// A top-down BGRA8 image backed by a pooled buffer. Rows are tightly packed:
/// <see cref="Stride"/> is always <c>Width * 4</c>. Dispose to return the buffer to the pool; any access
/// after that throws <see cref="ObjectDisposedException"/>. Not thread-safe for concurrent writes.
/// </summary>
public sealed class CapturedImage : IDisposable
{
    public const int BytesPerPixel = 4;

    private readonly ArrayPool<byte> _pool;
    private byte[]? _buffer;

    private CapturedImage(ArrayPool<byte> pool, byte[] buffer, int width, int height, CaptureMetadata metadata)
    {
        _pool = pool;
        _buffer = buffer;
        Width = width;
        Height = height;
        Metadata = metadata;
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride => Width * BytesPerPixel;

    public int ByteLength => Stride * Height;

    public CaptureMetadata Metadata { get; set; }

    public bool IsDisposed => Volatile.Read(ref _buffer) is null;

    /// <summary>The pixel bytes, exactly <see cref="ByteLength"/> long.</summary>
    public Span<byte> Pixels => Buffer.AsSpan(0, ByteLength);

    private byte[] Buffer => Volatile.Read(ref _buffer) ?? throw new ObjectDisposedException(nameof(CapturedImage));

    /// <summary>Rents a buffer for a <paramref name="width"/> × <paramref name="height"/> image.</summary>
    /// <param name="clear">Zero the pixels. Pooled buffers come back dirty, so leave this on unless every byte is overwritten.</param>
    public static CapturedImage Allocate(
        int width,
        int height,
        CaptureMetadata metadata,
        ArrayPool<byte>? pool = null,
        bool clear = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(metadata);

        var size = (long)width * height * BytesPerPixel;
        if (size > Array.MaxLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width), $"An image of {width}x{height} exceeds the maximum buffer size.");
        }

        pool ??= ArrayPool<byte>.Shared;
        var buffer = pool.Rent((int)size);
        if (clear)
        {
            Array.Clear(buffer, 0, (int)size);
        }

        return new CapturedImage(pool, buffer, width, height, metadata);
    }

    public Span<byte> GetRow(int y)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, Height);
        return Buffer.AsSpan(y * Stride, Stride);
    }

    /// <summary>
    /// Copies the part of this image inside <paramref name="localRect"/> (image-local pixels, clipped to the
    /// image) into a new image from the same pool. <see cref="CaptureMetadata.SourceBounds"/> is adjusted.
    /// </summary>
    /// <exception cref="ArgumentException">The rectangle does not overlap the image.</exception>
    public CapturedImage Crop(PixelRect localRect)
    {
        var clipped = localRect.Intersect(new PixelRect(0, 0, Width, Height));
        if (clipped.IsEmpty)
        {
            throw new ArgumentException($"Crop rectangle {localRect} does not overlap the {Width}x{Height} image.", nameof(localRect));
        }

        var source = Metadata.SourceBounds;
        var metadata = Metadata with
        {
            SourceBounds = new PixelRect(source.X + clipped.X, source.Y + clipped.Y, clipped.Width, clipped.Height),
        };

        var result = Allocate(clipped.Width, clipped.Height, metadata, _pool, clear: false);
        PixelCompositor.Blit(
            Pixels[(clipped.Y * Stride + clipped.X * BytesPerPixel)..], Stride, clipped.Width, clipped.Height,
            result.Pixels, result.Stride, result.Width, result.Height,
            0, 0);
        return result;
    }

    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
        {
            _pool.Return(buffer);
        }
    }
}
