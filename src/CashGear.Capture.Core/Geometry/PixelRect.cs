namespace CashGear.Capture.Core.Geometry;

/// <summary>
/// An axis-aligned rectangle in physical pixels. <see cref="Right"/> and <see cref="Bottom"/> are exclusive,
/// matching Win32 <c>RECT</c>. Coordinates may be negative (monitors left of / above the primary).
/// A rectangle with a non-positive width or height is empty.
/// </summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public static PixelRect Empty => default;

    public int Left => X;
    public int Top => Y;
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;
    public PixelPoint TopLeft => new(X, Y);
    public long Area => IsEmpty ? 0 : (long)Width * Height;

    public static PixelRect FromLTRB(int left, int top, int right, int bottom) =>
        new(left, top, right - left, bottom - top);

    /// <summary>The rectangle spanned by two corner points, in any order (exclusive of the max corner).</summary>
    public static PixelRect FromPoints(PixelPoint a, PixelPoint b) =>
        FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    public bool Contains(PixelPoint p) =>
        !IsEmpty && p.X >= X && p.X < Right && p.Y >= Y && p.Y < Bottom;

    public bool Contains(PixelRect r) =>
        !IsEmpty && !r.IsEmpty && r.X >= X && r.Y >= Y && r.Right <= Right && r.Bottom <= Bottom;

    public bool IntersectsWith(PixelRect r) =>
        !IsEmpty && !r.IsEmpty && r.X < Right && X < r.Right && r.Y < Bottom && Y < r.Bottom;

    public PixelRect Intersect(PixelRect r) =>
        IntersectsWith(r)
            ? FromLTRB(Math.Max(X, r.X), Math.Max(Y, r.Y), Math.Min(Right, r.Right), Math.Min(Bottom, r.Bottom))
            : Empty;

    public PixelRect Union(PixelRect r)
    {
        if (IsEmpty) return r.IsEmpty ? Empty : r;
        if (r.IsEmpty) return this;
        return FromLTRB(Math.Min(X, r.X), Math.Min(Y, r.Y), Math.Max(Right, r.Right), Math.Max(Bottom, r.Bottom));
    }

    public PixelRect Offset(int dx, int dy) => this with { X = X + dx, Y = Y + dy };

    public override string ToString() => $"({X},{Y} {Width}x{Height})";
}
