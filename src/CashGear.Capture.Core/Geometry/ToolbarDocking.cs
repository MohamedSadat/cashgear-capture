using CashGear.Capture.Core.Display;

namespace CashGear.Capture.Core.Geometry;

/// <summary>Which edge of a monitor's work area a toolbar is docked to.</summary>
public enum DockEdge
{
    /// <summary>Floating: where it was put.</summary>
    None,
    Left,
    Top,
    Right,
    Bottom,
}

/// <summary>Where the capture toolbar was, remembered between runs.</summary>
/// <param name="Monitor">The monitor's device name (<c>\\.\DISPLAY2</c>).</param>
/// <param name="X">Its left edge from the monitor's work area's left edge (physical pixels).</param>
/// <param name="Y">Its top edge from the work area's top.</param>
/// <param name="Edge">The edge it is docked to.</param>
/// <param name="Vertical">Its buttons are in a column (always when docked left or right).</param>
public sealed record ToolbarPlacement(string? Monitor, int X, int Y, DockEdge Edge, bool Vertical);

/// <summary>
/// Docking the capture toolbar: dropped within <see cref="SnapDistance"/> of an edge of the monitor's work area, it
/// goes flush against that edge (the nearest, if two); docked left or right its buttons go in a column, top or
/// bottom in a row. It is always kept inside the work area.
/// </summary>
public static class ToolbarDocking
{
    public const int SnapDistance = 24;

    /// <summary>The edge <paramref name="dropped"/> docks to in <paramref name="workArea"/>, or <see cref="DockEdge.None"/>.</summary>
    public static DockEdge EdgeFor(PixelRect dropped, PixelRect workArea, int distance = SnapDistance)
    {
        var gaps = new (DockEdge Edge, int Gap)[]
        {
            (DockEdge.Left, Math.Abs(dropped.Left - workArea.Left)),
            (DockEdge.Top, Math.Abs(dropped.Top - workArea.Top)),
            (DockEdge.Right, Math.Abs(workArea.Right - dropped.Right)),
            (DockEdge.Bottom, Math.Abs(workArea.Bottom - dropped.Bottom)),
        };
        var nearest = gaps.MinBy(g => g.Gap);
        return nearest.Gap <= distance ? nearest.Edge : DockEdge.None;
    }

    /// <summary>Whether a toolbar docked to <paramref name="edge"/> is vertical (null: as it was).</summary>
    public static bool? VerticalFor(DockEdge edge) => edge switch
    {
        DockEdge.Left or DockEdge.Right => true,
        DockEdge.Top or DockEdge.Bottom => false,
        _ => null,
    };

    /// <summary>
    /// Where a toolbar of <paramref name="size"/> goes: flush against <paramref name="edge"/> near
    /// <paramref name="near"/>'s position along it, or at <paramref name="near"/>; inside <paramref name="workArea"/>.
    /// </summary>
    public static PixelRect Place(PixelPoint near, PixelSize size, DockEdge edge, PixelRect workArea)
    {
        var width = Math.Min(size.Width, workArea.Width);
        var height = Math.Min(size.Height, workArea.Height);
        var x = Math.Clamp(near.X, workArea.Left, workArea.Right - width);
        var y = Math.Clamp(near.Y, workArea.Top, workArea.Bottom - height);
        switch (edge)
        {
            case DockEdge.Left:
                x = workArea.Left;
                break;
            case DockEdge.Right:
                x = workArea.Right - width;
                break;
            case DockEdge.Top:
                y = workArea.Top;
                break;
            case DockEdge.Bottom:
                y = workArea.Bottom - height;
                break;
        }

        return new PixelRect(x, y, width, height);
    }

    /// <summary>The placement to remember for a toolbar at <paramref name="bounds"/> on <paramref name="monitor"/>.</summary>
    public static ToolbarPlacement Describe(PixelRect bounds, DockEdge edge, bool vertical, MonitorInfo monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        return new ToolbarPlacement(monitor.DeviceName, bounds.X - monitor.WorkArea.X, bounds.Y - monitor.WorkArea.Y, edge, vertical);
    }

    /// <summary>
    /// Where a remembered toolbar of <paramref name="size"/> goes now: on its monitor if that is still there (else the
    /// primary), at the same offset, docked to the same edge. With nothing remembered: the top centre of the primary
    /// monitor's work area.
    /// </summary>
    public static (PixelRect Bounds, MonitorInfo Monitor) Restore(ToolbarPlacement? placement, IReadOnlyList<MonitorInfo> monitors, PixelSize size)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        if (monitors.Count == 0)
        {
            throw new ArgumentException("At least one monitor is required.", nameof(monitors));
        }

        var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
        if (placement is null)
        {
            var area = primary.WorkArea;
            return (Place(new PixelPoint(area.X + ((area.Width - size.Width) / 2), area.Y), size, DockEdge.Top, area), primary);
        }

        var monitor = monitors.FirstOrDefault(m => m.DeviceName == placement.Monitor) ?? primary;
        var work = monitor.WorkArea;
        return (Place(new PixelPoint(work.X + placement.X, work.Y + placement.Y), size, placement.Edge, work), monitor);
    }
}
