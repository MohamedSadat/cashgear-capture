using SkiaSharp;

namespace CashGear.Capture.Editor.Model;

public enum HandleKind
{
    Start,
    End,
    TopLeft,
    Top,
    TopRight,
    Right,
    BottomRight,
    Bottom,
    BottomLeft,
    Left,
}

/// <summary>A grip shown on the selected annotation, in image pixels.</summary>
public readonly record struct Handle(HandleKind Kind, SKPoint Position);

/// <summary>Point, segment and rectangle math shared by the annotations and the interaction code (image pixels).</summary>
public static class Geometry
{
    public static float Distance(SKPoint a, SKPoint b) => MathF.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    public static float DistanceToSegment(SKPoint p, SKPoint a, SKPoint b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared < 1e-6f)
        {
            return Distance(p, a);
        }

        var t = Math.Clamp((((p.X - a.X) * dx) + ((p.Y - a.Y) * dy)) / lengthSquared, 0, 1);
        return Distance(p, new SKPoint(a.X + (t * dx), a.Y + (t * dy)));
    }

    public static SKPoint Offset(SKPoint p, float dx, float dy) => new(p.X + dx, p.Y + dy);

    /// <summary>The normalized rectangle spanned by two corners.</summary>
    public static SKRect RectFromPoints(SKPoint a, SKPoint b) =>
        new(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y), MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y));

    public static SKRect Inflate(SKRect rect, float amount) =>
        new(rect.Left - amount, rect.Top - amount, rect.Right + amount, rect.Bottom + amount);

    public static SKRect OffsetRect(SKRect rect, float dx, float dy) =>
        new(rect.Left + dx, rect.Top + dy, rect.Right + dx, rect.Bottom + dy);

    /// <summary>The rectangle mapped through <paramref name="matrix"/> (normalized; exact for 90° turns and scales).</summary>
    public static SKRect MapRect(SKMatrix matrix, SKRect rect) =>
        RectFromPoints(matrix.MapPoint(rect.Left, rect.Top), matrix.MapPoint(rect.Right, rect.Bottom));

    /// <summary><paramref name="p"/> moved so the line from <paramref name="origin"/> is a multiple of 45°.</summary>
    public static SKPoint SnapAngle(SKPoint origin, SKPoint p)
    {
        var dx = p.X - origin.X;
        var dy = p.Y - origin.Y;
        var length = MathF.Sqrt((dx * dx) + (dy * dy));
        if (length < 1e-3f)
        {
            return p;
        }

        var step = MathF.PI / 4;
        var angle = MathF.Round(MathF.Atan2(dy, dx) / step) * step;
        return new SKPoint(origin.X + (length * MathF.Cos(angle)), origin.Y + (length * MathF.Sin(angle)));
    }

    /// <summary><paramref name="p"/> moved so the rectangle from <paramref name="anchor"/> is a square.</summary>
    public static SKPoint SquareCorner(SKPoint anchor, SKPoint p)
    {
        var dx = p.X - anchor.X;
        var dy = p.Y - anchor.Y;
        var side = MathF.Max(MathF.Abs(dx), MathF.Abs(dy));
        return new SKPoint(anchor.X + (MathF.Sign(dx == 0 ? 1 : dx) * side), anchor.Y + (MathF.Sign(dy == 0 ? 1 : dy) * side));
    }

    /// <summary>The eight resize grips of a box: corners and edge midpoints.</summary>
    public static IReadOnlyList<Handle> BoxHandles(SKRect r) =>
    [
        new(HandleKind.TopLeft, new SKPoint(r.Left, r.Top)),
        new(HandleKind.Top, new SKPoint(r.MidX, r.Top)),
        new(HandleKind.TopRight, new SKPoint(r.Right, r.Top)),
        new(HandleKind.Right, new SKPoint(r.Right, r.MidY)),
        new(HandleKind.BottomRight, new SKPoint(r.Right, r.Bottom)),
        new(HandleKind.Bottom, new SKPoint(r.MidX, r.Bottom)),
        new(HandleKind.BottomLeft, new SKPoint(r.Left, r.Bottom)),
        new(HandleKind.Left, new SKPoint(r.Left, r.MidY)),
    ];

    /// <summary>
    /// <paramref name="rect"/> with the grip <paramref name="handle"/> dragged to <paramref name="p"/>. Dragging past the
    /// opposite side flips the box (the result is normalized). <paramref name="keepAspect"/> keeps the proportions
    /// when a corner is dragged.
    /// </summary>
    public static SKRect ResizeBox(SKRect rect, HandleKind handle, SKPoint p, bool keepAspect)
    {
        SKPoint? anchor = handle switch
        {
            HandleKind.TopLeft => new SKPoint(rect.Right, rect.Bottom),
            HandleKind.TopRight => new SKPoint(rect.Left, rect.Bottom),
            HandleKind.BottomRight => new SKPoint(rect.Left, rect.Top),
            HandleKind.BottomLeft => new SKPoint(rect.Right, rect.Top),
            _ => null,
        };

        if (anchor is { } a)
        {
            if (keepAspect && rect.Width > 0 && rect.Height > 0)
            {
                var dx = p.X - a.X;
                var dy = p.Y - a.Y;
                var scale = MathF.Max(MathF.Abs(dx) / rect.Width, MathF.Abs(dy) / rect.Height);
                p = new SKPoint(
                    a.X + (MathF.Sign(dx == 0 ? 1 : dx) * rect.Width * scale),
                    a.Y + (MathF.Sign(dy == 0 ? 1 : dy) * rect.Height * scale));
            }

            return RectFromPoints(a, p);
        }

        return handle switch
        {
            HandleKind.Top => RectFromPoints(new SKPoint(rect.Left, p.Y), new SKPoint(rect.Right, rect.Bottom)),
            HandleKind.Bottom => RectFromPoints(new SKPoint(rect.Left, rect.Top), new SKPoint(rect.Right, p.Y)),
            HandleKind.Left => RectFromPoints(new SKPoint(p.X, rect.Top), new SKPoint(rect.Right, rect.Bottom)),
            HandleKind.Right => RectFromPoints(new SKPoint(rect.Left, rect.Top), new SKPoint(p.X, rect.Bottom)),
            _ => rect,
        };
    }

    /// <summary>The grip of <paramref name="handles"/> within <paramref name="tolerance"/> of <paramref name="p"/> (closest wins).</summary>
    public static Handle? HandleAt(IReadOnlyList<Handle> handles, SKPoint p, float tolerance)
    {
        Handle? best = null;
        var bestDistance = float.MaxValue;
        foreach (var handle in handles)
        {
            var d = MathF.Max(MathF.Abs(handle.Position.X - p.X), MathF.Abs(handle.Position.Y - p.Y));
            if (d <= tolerance && d < bestDistance)
            {
                best = handle;
                bestDistance = d;
            }
        }

        return best;
    }
}
