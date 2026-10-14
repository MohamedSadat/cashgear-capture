namespace CashGear.Capture.Core.Geometry;

/// <summary>A point in physical pixels, in virtual-screen space unless stated otherwise.</summary>
public readonly record struct PixelPoint(int X, int Y)
{
    public PixelPoint Offset(int dx, int dy) => new(X + dx, Y + dy);

    public override string ToString() => $"({X},{Y})";
}
