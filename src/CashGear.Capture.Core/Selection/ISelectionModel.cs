using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;

namespace CashGear.Capture.Core.Selection;

/// <summary>Keys the selection overlay passes on to its model (Esc always cancels).</summary>
public enum SelectionKey
{
    Enter,
    Backspace,
    Tab,
    Left,
    Right,
    Up,
    Down,
}

[Flags]
public enum SelectionModifiers
{
    None = 0,
    Shift = 1,
    Control = 2,
}

/// <summary>
/// What the overlay draws for a selection model, in virtual-screen physical pixels. Everything outside
/// <see cref="Lit"/> (or <see cref="LitShape"/>) is dimmed when <see cref="Dim"/> is on.
/// </summary>
public sealed record SelectionView
{
    /// <summary>The rectangle that is lit, framed and labelled; empty when nothing is.</summary>
    public PixelRect Lit { get; init; }

    /// <summary>The lit area when it isn't the whole of <see cref="Lit"/> (a rounded rectangle, a polygon).</summary>
    public CaptureShape? LitShape { get; init; }

    /// <summary>A path being drawn: the polygon's points so far, or the freehand trail.</summary>
    public IReadOnlyList<PixelPoint>? Path { get; init; }

    /// <summary>Draw a line from the path's last point to the cursor (the next polygon side).</summary>
    public bool RubberBand { get; init; }

    /// <summary>The cursor is on the polygon's first point: a click there closes it.</summary>
    public bool CanClosePath { get; init; }

    /// <summary>The size label, e.g. <c>640 × 480</c>; null for none.</summary>
    public string? Label { get; init; }

    /// <summary>A second, dimmer line under the label (or on its own at the top of the cursor's monitor when nothing is lit): what the mouse and keys do.</summary>
    public string? Hint { get; init; }

    /// <summary>Show the full-screen guides through the cursor and the loupe beside it.</summary>
    public bool Tracking { get; init; }

    /// <summary>Dim what isn't lit (off for picking a colour).</summary>
    public bool Dim { get; init; } = true;
}

/// <summary>
/// A selection state machine behind the overlay, in virtual-screen physical pixels and free of any UI type. The
/// overlay feeds it pointer and key input and draws <see cref="View"/>. Not thread-safe; drive it from one thread.
/// </summary>
public interface ISelectionModel
{
    /// <summary>Raised after any state change.</summary>
    event EventHandler? Changed;

    PixelRect VirtualBounds { get; }

    SelectionPhase Phase { get; }

    /// <summary>The cursor pixel, clamped to <see cref="VirtualBounds"/>.</summary>
    PixelPoint Cursor { get; }

    bool IsFinished { get; }

    /// <summary>What was chosen, once <see cref="SelectionPhase.Confirmed"/>.</summary>
    RegionSelectionResult? Result { get; }

    SelectionView View { get; }

    void PointerMoved(PixelPoint point);

    void PointerPressed(PixelPoint point);

    void PointerReleased(PixelPoint point);

    void DoubleClicked(PixelPoint point);

    /// <summary>The mouse wheel turned by <paramref name="notches"/> (positive: away from the user).</summary>
    void Wheel(int notches);

    /// <summary>A key; false when the model has no use for it (the overlay then moves the pointer for an arrow).</summary>
    bool KeyPressed(SelectionKey key, SelectionModifiers modifiers);

    /// <summary>Finishes with what is selected; false when there is nothing to confirm.</summary>
    bool Confirm();

    void Cancel();
}

/// <summary>How the overlay is set up for one capture.</summary>
/// <param name="Mode">What is selected.</param>
public sealed record SelectionOptions(SelectionMode Mode = SelectionMode.RegionOrWindow)
{
    /// <summary><see cref="SelectionMode.FixedSize"/>: the sizes Tab cycles through.</summary>
    public IReadOnlyList<PixelSize> FixedSizes { get; init; } = [];

    /// <summary><see cref="SelectionMode.FixedSize"/>: the size to start with (an index into <see cref="FixedSizes"/>).</summary>
    public int FixedSizeIndex { get; init; }

    /// <summary><see cref="SelectionMode.RoundedRectangle"/>: the corner radius to start with (pixels).</summary>
    public int CornerRadius { get; init; } = 16;
}

/// <summary>Creates the model for a <see cref="SelectionOptions"/>.</summary>
public static class SelectionModels
{
    public static ISelectionModel Create(
        SelectionOptions options, IReadOnlyList<PixelRect> monitorBounds, IReadOnlyList<WindowInfo> windows, PixelPoint cursor)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Mode switch
        {
            SelectionMode.FixedSize => new FixedRegionSelectionModel(monitorBounds, cursor, options.FixedSizes, options.FixedSizeIndex),
            SelectionMode.Freehand or SelectionMode.Polygon => new ShapeSelectionModel(monitorBounds, cursor, options.Mode),
            SelectionMode.Point => new PointSelectionModel(monitorBounds, cursor),
            _ => new RegionSelectionModel(monitorBounds, windows, cursor, mode: options.Mode, cornerRadius: options.CornerRadius),
        };
    }
}

/// <summary>Ready-made sizes for a fixed-size region.</summary>
public static class FixedSizes
{
    public static IReadOnlyList<PixelSize> Presets { get; } =
    [
        new(320, 240),
        new(640, 480),
        new(800, 600),
        new(1024, 768),
        new(1280, 720),
        new(1280, 1024),
        new(1366, 768),
        new(1600, 900),
        new(1920, 1080),
    ];

    /// <summary>The presets plus <paramref name="current"/> (in size order when it isn't one), and where <paramref name="current"/> is.</summary>
    public static (IReadOnlyList<PixelSize> Sizes, int Index) With(PixelSize current)
    {
        var sizes = Presets.Contains(current) || current.IsEmpty
            ? Presets.ToList()
            : Presets.Append(current).OrderBy(s => (long)s.Width * s.Height).ThenBy(s => s.Width).ToList();
        var index = sizes.IndexOf(current);
        return (sizes, index < 0 ? sizes.IndexOf(new PixelSize(800, 600)) : index);
    }
}

/// <summary>Shared helpers for the models.</summary>
internal static class SelectionMath
{
    public static PixelPoint Clamp(PixelPoint point, PixelRect bounds) => new(
        Math.Clamp(point.X, bounds.X, bounds.Right - 1),
        Math.Clamp(point.Y, bounds.Y, bounds.Bottom - 1));

    public static int Distance(PixelPoint a, PixelPoint b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    public static int Step(SelectionModifiers modifiers) => modifiers.HasFlag(SelectionModifiers.Control) ? 10 : 1;

    public static (int Dx, int Dy) Direction(SelectionKey key) => key switch
    {
        SelectionKey.Left => (-1, 0),
        SelectionKey.Right => (1, 0),
        SelectionKey.Up => (0, -1),
        SelectionKey.Down => (0, 1),
        _ => (0, 0),
    };

    public static bool IsArrow(SelectionKey key) => key is SelectionKey.Left or SelectionKey.Right or SelectionKey.Up or SelectionKey.Down;

    public static IReadOnlyList<PixelRect> ValidMonitors(IReadOnlyList<PixelRect> monitorBounds)
    {
        ArgumentNullException.ThrowIfNull(monitorBounds);
        var monitors = monitorBounds.Where(m => !m.IsEmpty).ToList();
        return monitors.Count == 0 ? throw new ArgumentException("At least one monitor is required.", nameof(monitorBounds)) : monitors;
    }
}
