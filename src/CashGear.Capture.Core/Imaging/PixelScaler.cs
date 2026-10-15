namespace CashGear.Capture.Core.Imaging;

/// <summary>Resizes BGRA pixels (used to give text recognition larger letters to read).</summary>
public static class PixelScaler
{
    /// <summary>Bilinear resize of a top-down BGRA image to <paramref name="width"/> × <paramref name="height"/>.</summary>
    public static byte[] Resize(ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (source.Length < sourceWidth * sourceHeight * 4)
        {
            throw new ArgumentException("The source is smaller than its size says.", nameof(source));
        }

        var result = new byte[width * height * 4];
        var sx = (double)sourceWidth / width;
        var sy = (double)sourceHeight / height;
        for (var y = 0; y < height; y++)
        {
            // Sample at pixel centres, clamped to the edges.
            var fy = Math.Clamp(((y + 0.5) * sy) - 0.5, 0, sourceHeight - 1);
            var y0 = (int)fy;
            var y1 = Math.Min(y0 + 1, sourceHeight - 1);
            var wy = fy - y0;
            for (var x = 0; x < width; x++)
            {
                var fx = Math.Clamp(((x + 0.5) * sx) - 0.5, 0, sourceWidth - 1);
                var x0 = (int)fx;
                var x1 = Math.Min(x0 + 1, sourceWidth - 1);
                var wx = fx - x0;
                var o = ((y * width) + x) * 4;
                for (var c = 0; c < 4; c++)
                {
                    var top = (source[((y0 * sourceWidth) + x0) * 4 + c] * (1 - wx)) + (source[((y0 * sourceWidth) + x1) * 4 + c] * wx);
                    var bottom = (source[((y1 * sourceWidth) + x0) * 4 + c] * (1 - wx)) + (source[((y1 * sourceWidth) + x1) * 4 + c] * wx);
                    result[o + c] = (byte)Math.Round((top * (1 - wy)) + (bottom * wy));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Shrinks a top-down BGRA image (rows <paramref name="sourceStride"/> bytes apart) by averaging every source
    /// pixel that falls into each result pixel, weighted by alpha so transparent areas don't darken the edges. For
    /// thumbnails, where bilinear sampling would skip most pixels and shimmer.
    /// </summary>
    public static byte[] Shrink(ReadOnlySpan<byte> source, int sourceWidth, int sourceHeight, int sourceStride, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceHeight);
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceStride, sourceWidth * 4);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, sourceWidth);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, sourceHeight);
        if (source.Length < ((sourceHeight - 1) * sourceStride) + (sourceWidth * 4))
        {
            throw new ArgumentException("The source is smaller than its size says.", nameof(source));
        }

        var result = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var y0 = (int)((long)y * sourceHeight / height);
            var y1 = Math.Max(y0 + 1, (int)((long)(y + 1) * sourceHeight / height));
            for (var x = 0; x < width; x++)
            {
                var x0 = (int)((long)x * sourceWidth / width);
                var x1 = Math.Max(x0 + 1, (int)((long)(x + 1) * sourceWidth / width));
                long b = 0, g = 0, r = 0, a = 0;
                for (var sy = y0; sy < y1; sy++)
                {
                    var row = source.Slice(sy * sourceStride);
                    for (var sx = x0; sx < x1; sx++)
                    {
                        var i = sx * 4;
                        long alpha = row[i + 3];
                        b += row[i] * alpha;
                        g += row[i + 1] * alpha;
                        r += row[i + 2] * alpha;
                        a += alpha;
                    }
                }

                var o = ((y * width) + x) * 4;
                var count = (long)(y1 - y0) * (x1 - x0);
                if (a > 0)
                {
                    result[o] = (byte)((b + (a / 2)) / a);
                    result[o + 1] = (byte)((g + (a / 2)) / a);
                    result[o + 2] = (byte)((r + (a / 2)) / a);
                    result[o + 3] = (byte)((a + (count / 2)) / count);
                }
            }
        }

        return result;
    }
}
