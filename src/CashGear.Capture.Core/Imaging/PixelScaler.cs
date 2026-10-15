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
}
