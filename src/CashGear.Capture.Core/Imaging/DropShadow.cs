using System.Buffers;

namespace CashGear.Capture.Core.Imaging;

/// <summary>
/// Adds a soft drop shadow around an image (typically a captured window) on a transparent margin, the way
/// window screenshots look on a page. Neither WGC nor GDI capture the DWM shadow itself, so it is synthesized:
/// a rectangle mask blurred with three box-blur passes (≈ Gaussian), then the image composited over it.
/// </summary>
public static class DropShadow
{
    public const int DefaultRadius = 14;
    public const int DefaultOffsetY = 4;
    public const double DefaultOpacity = 0.45;

    /// <summary>The transparent margin added on each side for a given radius and offset.</summary>
    public static int MarginFor(int radius, int offsetY) => (int)Math.Ceiling(radius * 1.5) + Math.Abs(offsetY);

    /// <param name="radius">Blur radius in pixels (scale it by the monitor DPI for a consistent look).</param>
    /// <param name="offsetY">Downward shift of the shadow.</param>
    /// <param name="opacity">Darkness of the shadow right under the image, 0–1.</param>
    public static CapturedImage Apply(
        CapturedImage source,
        int radius = DefaultRadius,
        int offsetY = DefaultOffsetY,
        double opacity = DefaultOpacity,
        ArrayPool<byte>? pool = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(radius);
        ArgumentOutOfRangeException.ThrowIfLessThan(opacity, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(opacity, 1);

        var margin = MarginFor(radius, offsetY);
        var width = source.Width + (2 * margin);
        var height = source.Height + (2 * margin);

        var mask = new float[width * height];
        FillRect(mask, width, margin, margin + offsetY, source.Width, source.Height);
        var box = Math.Max(1, (radius + 1) / 2);
        for (var pass = 0; pass < 3; pass++)
        {
            BlurHorizontal(mask, width, height, box);
            BlurVertical(mask, width, height, box);
        }

        var result = CapturedImage.Allocate(
            width,
            height,
            source.Metadata with { HasTransparency = true },
            pool,
            clear: false);

        // Shadow: black with the blurred mask as alpha.
        var dst = result.Pixels;
        for (var i = 0; i < mask.Length; i++)
        {
            var o = i * 4;
            dst[o] = 0;
            dst[o + 1] = 0;
            dst[o + 2] = 0;
            dst[o + 3] = (byte)Math.Clamp((int)Math.Round(mask[i] * opacity * 255), 0, 255);
        }

        // The image itself, composited "over" the shadow (respects transparent corners, e.g. on Windows 11).
        for (var y = 0; y < source.Height; y++)
        {
            var srcRow = source.GetRow(y);
            var dstRow = result.GetRow(y + margin);
            for (var x = 0; x < source.Width; x++)
            {
                var s = x * 4;
                var d = (x + margin) * 4;
                int sa = srcRow[s + 3];
                if (sa == 255)
                {
                    srcRow.Slice(s, 4).CopyTo(dstRow.Slice(d, 4));
                    continue;
                }

                int da = dstRow[d + 3]; // the shadow under it is black
                var outA = sa + (da * (255 - sa) / 255);
                if (outA == 0)
                {
                    continue;
                }

                dstRow[d] = (byte)(srcRow[s] * sa / outA);
                dstRow[d + 1] = (byte)(srcRow[s + 1] * sa / outA);
                dstRow[d + 2] = (byte)(srcRow[s + 2] * sa / outA);
                dstRow[d + 3] = (byte)outA;
            }
        }

        return result;
    }

    private static void FillRect(float[] mask, int stride, int x, int y, int w, int h)
    {
        for (var row = y; row < y + h; row++)
        {
            mask.AsSpan((row * stride) + x, w).Fill(1f);
        }
    }

    /// <summary>Running-sum box blur along rows; edges are treated as zero (transparent).</summary>
    private static void BlurHorizontal(float[] data, int width, int height, int r)
    {
        var line = new float[width];
        var norm = 1f / ((2 * r) + 1);
        for (var y = 0; y < height; y++)
        {
            var row = data.AsSpan(y * width, width);
            row.CopyTo(line);
            float sum = 0;
            for (var x = -r; x <= r; x++)
            {
                sum += x is >= 0 && x < width ? line[x] : 0;
            }

            for (var x = 0; x < width; x++)
            {
                row[x] = sum * norm;
                var add = x + r + 1;
                var remove = x - r;
                sum += (add < width ? line[add] : 0) - (remove >= 0 ? line[remove] : 0);
            }
        }
    }

    private static void BlurVertical(float[] data, int width, int height, int r)
    {
        var column = new float[height];
        var norm = 1f / ((2 * r) + 1);
        for (var x = 0; x < width; x++)
        {
            for (var y = 0; y < height; y++)
            {
                column[y] = data[(y * width) + x];
            }

            float sum = 0;
            for (var y = -r; y <= r; y++)
            {
                sum += y is >= 0 && y < height ? column[y] : 0;
            }

            for (var y = 0; y < height; y++)
            {
                data[(y * width) + x] = sum * norm;
                var add = y + r + 1;
                var remove = y - r;
                sum += (add < height ? column[add] : 0) - (remove >= 0 ? column[remove] : 0);
            }
        }
    }
}
