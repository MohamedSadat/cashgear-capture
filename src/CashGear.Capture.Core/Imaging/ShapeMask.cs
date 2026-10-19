using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Selection;

namespace CashGear.Capture.Core.Imaging;

/// <summary>
/// Makes the pixels of an image outside a <see cref="CaptureShape"/> transparent. Edge pixels get the part of them
/// the shape covers as alpha (anti-aliased: four sub-rows per pixel row, exact across). Pixels left fully outside
/// become transparent white, so a viewer that ignores alpha (JPEG, a DIB) shows white there, not the screen.
/// </summary>
public static class ShapeMask
{
    private const int SubRows = 4;

    /// <summary>Applies <paramref name="shape"/> (virtual-screen pixels) to <paramref name="image"/>, placed at its <see cref="CaptureMetadata.SourceBounds"/>.</summary>
    public static void Apply(CapturedImage image, CaptureShape shape)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(shape);
        if (shape.IsRectangle && image.Metadata.SourceBounds.Intersect(shape.Bounds) == image.Metadata.SourceBounds)
        {
            return; // nothing outside
        }

        var origin = image.Metadata.SourceBounds.TopLeft;
        var edges = Edges(shape, origin);
        var cover = new float[image.Width];
        for (var y = 0; y < image.Height; y++)
        {
            Array.Clear(cover);
            CoverRow(edges, y, cover);
            ApplyRow(image.GetRow(y), cover);
        }
    }

    /// <summary>How much of each pixel the shape covers (0 to 1), for tests and previews: <c>[y, x]</c> over <paramref name="area"/>.</summary>
    public static float[,] Coverage(CaptureShape shape, PixelRect area)
    {
        ArgumentNullException.ThrowIfNull(shape);
        var edges = Edges(shape, area.TopLeft);
        var result = new float[area.Height, area.Width];
        var cover = new float[area.Width];
        for (var y = 0; y < area.Height; y++)
        {
            Array.Clear(cover);
            CoverRow(edges, y, cover);
            for (var x = 0; x < area.Width; x++)
            {
                result[y, x] = Math.Clamp(cover[x], 0f, 1f);
            }
        }

        return result;
    }

    private static void ApplyRow(Span<byte> row, float[] cover)
    {
        for (var x = 0; x < cover.Length; x++)
        {
            var c = Math.Clamp(cover[x], 0f, 1f);
            if (c >= 0.999f)
            {
                continue;
            }

            var px = row.Slice(x * CapturedImage.BytesPerPixel, CapturedImage.BytesPerPixel);
            var alpha = (byte)Math.Round(px[3] * c);
            if (alpha == 0)
            {
                px[0] = px[1] = px[2] = 255;
            }

            px[3] = alpha;
        }
    }

    /// <summary>Adds the coverage of image row <paramref name="y"/> (local) to <paramref name="cover"/>.</summary>
    private static void CoverRow(Edge[] edges, int y, float[] cover)
    {
        var crossings = new List<(double X, int Winding)>(16);
        const float weight = 1f / SubRows;
        for (var s = 0; s < SubRows; s++)
        {
            var sy = y + ((s + 0.5) / SubRows);
            crossings.Clear();
            foreach (var e in edges)
            {
                if (sy >= e.Top && sy < e.Bottom)
                {
                    crossings.Add((e.XAt(sy), e.Winding));
                }
            }

            if (crossings.Count < 2)
            {
                continue;
            }

            crossings.Sort(static (a, b) => a.X.CompareTo(b.X));
            var winding = 0;
            for (var i = 0; i < crossings.Count - 1; i++)
            {
                winding += crossings[i].Winding;
                if (winding != 0)
                {
                    AddSpan(cover, crossings[i].X, crossings[i + 1].X, weight);
                }
            }
        }
    }

    /// <summary>Adds <paramref name="weight"/> times the part of each pixel inside [<paramref name="from"/>, <paramref name="to"/>).</summary>
    private static void AddSpan(float[] cover, double from, double to, float weight)
    {
        from = Math.Max(from, 0);
        to = Math.Min(to, cover.Length);
        if (to <= from)
        {
            return;
        }

        var first = (int)Math.Floor(from);
        var last = (int)Math.Floor(to);
        if (first == last)
        {
            cover[first] += (float)(to - from) * weight;
            return;
        }

        cover[first] += (float)(first + 1 - from) * weight;
        for (var x = first + 1; x < last; x++)
        {
            cover[x] += weight;
        }

        if (last < cover.Length)
        {
            cover[last] += (float)(to - last) * weight;
        }
    }

    private static Edge[] Edges(CaptureShape shape, PixelPoint origin)
    {
        var edges = new List<Edge>();
        foreach (var ring in shape.Rings())
        {
            for (var i = 0; i < ring.Count; i++)
            {
                var (x0, y0) = ring[i];
                var (x1, y1) = ring[(i + 1) % ring.Count];
                if (y0 == y1)
                {
                    continue; // horizontal edges never cross a sub-row
                }

                edges.Add(new Edge(x0 - origin.X, y0 - origin.Y, x1 - origin.X, y1 - origin.Y));
            }
        }

        return [.. edges];
    }

    private readonly struct Edge
    {
        private readonly double _x0;
        private readonly double _y0;
        private readonly double _slope;

        public Edge(double x0, double y0, double x1, double y1)
        {
            _x0 = x0;
            _y0 = y0;
            _slope = (x1 - x0) / (y1 - y0);
            Top = Math.Min(y0, y1);
            Bottom = Math.Max(y0, y1);
            Winding = y1 > y0 ? 1 : -1;
        }

        public double Top { get; }

        public double Bottom { get; }

        public int Winding { get; }

        public double XAt(double y) => _x0 + ((y - _y0) * _slope);
    }
}
