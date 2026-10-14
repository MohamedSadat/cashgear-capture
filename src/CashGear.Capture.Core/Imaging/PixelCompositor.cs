using CashGear.Capture.Core.Geometry;

namespace CashGear.Capture.Core.Imaging;

/// <summary>Opaque copies of BGRA8 pixel blocks between buffers, with clipping.</summary>
public static class PixelCompositor
{
    /// <summary>
    /// Copies <paramref name="source"/> into <paramref name="destination"/> with its top-left at
    /// (<paramref name="destX"/>, <paramref name="destY"/>) in destination pixels. Parts falling outside the
    /// destination are clipped. Pixels are replaced, not blended.
    /// </summary>
    public static void Blit(CapturedImage source, CapturedImage destination, int destX, int destY)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        Blit(
            source.Pixels, source.Stride, source.Width, source.Height,
            destination.Pixels, destination.Stride, destination.Width, destination.Height,
            destX, destY);
    }

    /// <inheritdoc cref="Blit(CapturedImage, CapturedImage, int, int)"/>
    public static void Blit(
        ReadOnlySpan<byte> source, int sourceStride, int sourceWidth, int sourceHeight,
        Span<byte> destination, int destinationStride, int destinationWidth, int destinationHeight,
        int destX, int destY)
    {
        var target = new PixelRect(destX, destY, sourceWidth, sourceHeight)
            .Intersect(new PixelRect(0, 0, destinationWidth, destinationHeight));
        if (target.IsEmpty)
        {
            return;
        }

        var sourceX = target.X - destX;
        var sourceY = target.Y - destY;
        var rowBytes = target.Width * CapturedImage.BytesPerPixel;

        PixelBuffer.CopyRows(
            source[(sourceY * sourceStride + sourceX * CapturedImage.BytesPerPixel)..],
            sourceStride,
            destination[(target.Y * destinationStride + target.X * CapturedImage.BytesPerPixel)..],
            destinationStride,
            rowBytes,
            target.Height);
    }
}
