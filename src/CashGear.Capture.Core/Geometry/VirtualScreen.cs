namespace CashGear.Capture.Core.Geometry;

/// <summary>
/// Helpers for the virtual screen: the bounding box of all monitors. Its origin is usually but not
/// necessarily (0,0) — a monitor left of or above the primary makes it negative.
/// </summary>
public static class VirtualScreen
{
    /// <summary>The union of the given monitor bounds; empty when there are none.</summary>
    public static PixelRect GetBounds(IEnumerable<PixelRect> monitorBounds)
    {
        ArgumentNullException.ThrowIfNull(monitorBounds);
        return monitorBounds.Aggregate(PixelRect.Empty, static (acc, r) => acc.Union(r));
    }

    /// <summary>Translates a virtual-screen point into coordinates relative to <paramref name="origin"/>'s top-left.</summary>
    public static PixelPoint ToLocal(PixelPoint point, PixelRect origin) =>
        new(point.X - origin.X, point.Y - origin.Y);

    /// <summary>Translates a virtual-screen rectangle into coordinates relative to <paramref name="origin"/>'s top-left.</summary>
    public static PixelRect ToLocal(PixelRect rect, PixelRect origin) =>
        rect.Offset(-origin.X, -origin.Y);
}
