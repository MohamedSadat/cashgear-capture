using CashGear.Capture.Core.Geometry;

namespace CashGear.Capture.Core.Selection;

public enum ShapeKind
{
    /// <summary>A rectangle with rounded corners.</summary>
    RoundedRectangle,

    /// <summary>A closed outline through points (a polygon, or a freehand lasso).</summary>
    Polygon,

    /// <summary>Several rectangles together (open menus and their submenus).</summary>
    Rectangles,
}

/// <summary>
/// The outline of a capture that isn't a plain rectangle, in virtual-screen physical pixels. The capture is cropped
/// to <see cref="Bounds"/> and the pixels outside the outline are made transparent
/// (<see cref="Imaging.ShapeMask"/>). Pixel (x, y) is the square from (x, y) to (x + 1, y + 1); polygon points are
/// pixel centres, so a polygon through four pixels' centres covers half of each of its edge pixels.
/// </summary>
public sealed class CaptureShape
{
    /// <summary>Arcs are drawn with segments at most this long (pixels), so they look round at any radius.</summary>
    private const double ArcStep = 1.5;

    private CaptureShape(ShapeKind kind, PixelRect bounds, int radius, IReadOnlyList<PixelPoint> points, IReadOnlyList<PixelRect> rectangles)
    {
        Kind = kind;
        Bounds = bounds;
        Radius = radius;
        Points = points;
        Rectangles = rectangles;
    }

    public ShapeKind Kind { get; }

    /// <summary>Every pixel the outline touches: what the capture is cropped to.</summary>
    public PixelRect Bounds { get; }

    /// <summary>The corner radius of a <see cref="ShapeKind.RoundedRectangle"/> (pixels).</summary>
    public int Radius { get; }

    /// <summary>The points of a <see cref="ShapeKind.Polygon"/>, in order (pixel centres).</summary>
    public IReadOnlyList<PixelPoint> Points { get; }

    /// <summary>The parts of a <see cref="ShapeKind.Rectangles"/> shape.</summary>
    public IReadOnlyList<PixelRect> Rectangles { get; }

    /// <summary>A rectangle with corners rounded by <paramref name="radius"/>, at most half its shorter side.</summary>
    public static CaptureShape RoundedRectangle(PixelRect bounds, int radius)
    {
        if (bounds.IsEmpty)
        {
            throw new ArgumentException("A rounded rectangle needs a size.", nameof(bounds));
        }

        var clamped = Math.Clamp(radius, 0, Math.Min(bounds.Width, bounds.Height) / 2);
        return new CaptureShape(ShapeKind.RoundedRectangle, bounds, clamped, [], []);
    }

    /// <summary>A closed outline through <paramref name="points"/> (at least three).</summary>
    public static CaptureShape Polygon(IReadOnlyList<PixelPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 3)
        {
            throw new ArgumentException("A polygon needs at least three points.", nameof(points));
        }

        var bounds = PixelRect.FromLTRB(points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X) + 1, points.Max(p => p.Y) + 1);
        return new CaptureShape(ShapeKind.Polygon, bounds, 0, points.ToArray(), []);
    }

    /// <summary>The union of <paramref name="rectangles"/> (empty ones are left out).</summary>
    public static CaptureShape Union(IReadOnlyList<PixelRect> rectangles)
    {
        ArgumentNullException.ThrowIfNull(rectangles);
        var parts = rectangles.Where(r => !r.IsEmpty).ToArray();
        if (parts.Length == 0)
        {
            throw new ArgumentException("At least one rectangle is needed.", nameof(rectangles));
        }

        return new CaptureShape(ShapeKind.Rectangles, VirtualScreen.GetBounds(parts), 0, [], parts);
    }

    /// <summary>True when the shape covers all of <see cref="Bounds"/> (a rounded rectangle without rounding, or one rectangle).</summary>
    public bool IsRectangle => Kind switch
    {
        ShapeKind.RoundedRectangle => Radius == 0,
        ShapeKind.Rectangles => Rectangles.Any(r => r == Bounds),
        _ => false,
    };

    /// <summary>The same shape moved by (<paramref name="dx"/>, <paramref name="dy"/>).</summary>
    public CaptureShape Offset(int dx, int dy) => Kind switch
    {
        ShapeKind.RoundedRectangle => new CaptureShape(Kind, Bounds.Offset(dx, dy), Radius, [], []),
        ShapeKind.Polygon => Polygon(Points.Select(p => p.Offset(dx, dy)).ToArray()),
        _ => Union(Rectangles.Select(r => r.Offset(dx, dy)).ToArray()),
    };

    /// <summary>
    /// The outline as closed rings of points in continuous pixel coordinates (pixel centres are at .5), all wound
    /// the same way, so a pixel is inside where the rings' winding is not zero.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<(double X, double Y)>> Rings()
    {
        switch (Kind)
        {
            case ShapeKind.Polygon:
                return [Points.Select(p => (p.X + 0.5, p.Y + 0.5)).ToArray()];
            case ShapeKind.Rectangles:
                return Rectangles.Select(r => (IReadOnlyList<(double, double)>)
                    [(r.Left, r.Top), (r.Right, r.Top), (r.Right, r.Bottom), (r.Left, r.Bottom)]).ToArray();
            default:
                return [RoundedRing()];
        }
    }

    private (double X, double Y)[] RoundedRing()
    {
        var b = Bounds;
        if (Radius == 0)
        {
            return [(b.Left, b.Top), (b.Right, b.Top), (b.Right, b.Bottom), (b.Left, b.Bottom)];
        }

        var r = (double)Radius;
        var steps = Math.Max(4, (int)Math.Ceiling(Math.PI / 2 * r / ArcStep));
        var ring = new List<(double X, double Y)>(4 * (steps + 1));

        // Clockwise on screen (y down): top-right, bottom-right, bottom-left, top-left corners.
        Arc(b.Right - r, b.Top + r, -Math.PI / 2);
        Arc(b.Right - r, b.Bottom - r, 0);
        Arc(b.Left + r, b.Bottom - r, Math.PI / 2);
        Arc(b.Left + r, b.Top + r, Math.PI);
        return [.. ring];

        void Arc(double cx, double cy, double start)
        {
            for (var i = 0; i <= steps; i++)
            {
                var angle = start + (Math.PI / 2 * i / steps);
                ring.Add((cx + (r * Math.Cos(angle)), cy + (r * Math.Sin(angle))));
            }
        }
    }
}
