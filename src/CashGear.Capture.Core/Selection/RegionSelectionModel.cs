using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;

namespace CashGear.Capture.Core.Selection;

public enum SelectionPhase
{
    /// <summary>Nothing pressed: the window (or monitor) under the cursor is highlighted.</summary>
    Hovering,

    /// <summary>Left button held and moved past the drag threshold.</summary>
    Dragging,

    /// <summary>A rectangle is chosen and can be nudged/resized before confirming.</summary>
    Selected,

    Confirmed,

    Cancelled,
}

public enum SelectionMode
{
    /// <summary>Drag a rectangle, or click a window or monitor.</summary>
    RegionOrWindow,

    /// <summary>Only windows can be picked; a click on one confirms at once.</summary>
    WindowOnly,
}

/// <param name="Bounds">The chosen rectangle in virtual-screen physical pixels.</param>
/// <param name="Window">The window it was picked from, when it was picked by clicking a window and left unchanged.</param>
public sealed record RegionSelectionResult(PixelRect Bounds, WindowInfo? Window);

/// <summary>
/// The region-selection state machine behind the overlay, in virtual-screen physical pixels and free of any UI
/// type. The overlay windows feed it pointer/keyboard input and render from its state.
/// <list type="bullet">
/// <item>Hover highlights the topmost window under the cursor, or the monitor when there is none.</item>
/// <item>A click (press + release within the drag threshold) selects the highlighted target; a drag selects the
/// rectangle spanned by the press and release pixels, both inclusive.</item>
/// <item>While selected: nudge / resize by keyboard, double-click inside or <see cref="Confirm"/> to finish.</item>
/// </list>
/// In <see cref="SelectionMode.WindowOnly"/> there is no dragging and no monitor fallback: clicking a highlighted
/// window confirms it immediately.
/// Not thread-safe; drive it from one thread.
/// </summary>
public sealed class RegionSelectionModel
{
    public const int DefaultDragThreshold = 3;

    private readonly IReadOnlyList<PixelRect> _monitors;
    private readonly IReadOnlyList<WindowInfo> _windows;
    private readonly int _dragThreshold;
    private PixelPoint _anchor;
    private bool _pressed;
    private WindowInfo? _selectedWindow;

    public RegionSelectionModel(
        IReadOnlyList<PixelRect> monitorBounds,
        IReadOnlyList<WindowInfo> windows,
        PixelPoint initialCursor,
        int dragThreshold = DefaultDragThreshold,
        SelectionMode mode = SelectionMode.RegionOrWindow)
    {
        ArgumentNullException.ThrowIfNull(monitorBounds);
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentOutOfRangeException.ThrowIfNegative(dragThreshold);

        _monitors = monitorBounds.Where(m => !m.IsEmpty).ToList();
        if (_monitors.Count == 0)
        {
            throw new ArgumentException("At least one monitor is required.", nameof(monitorBounds));
        }

        // Topmost first, so the first hit is the visible window.
        _windows = windows.Where(w => !w.IsMinimized && !w.Bounds.IsEmpty).OrderBy(w => w.ZOrder).ToList();
        _dragThreshold = dragThreshold;
        Mode = mode;
        VirtualBounds = VirtualScreen.GetBounds(_monitors);
        Cursor = Clamp(initialCursor);
        UpdateHover();
    }

    /// <summary>Raised after any state change.</summary>
    public event EventHandler? Changed;

    public PixelRect VirtualBounds { get; }

    public SelectionMode Mode { get; }

    public SelectionPhase Phase { get; private set; } = SelectionPhase.Hovering;

    /// <summary>The cursor pixel, clamped to <see cref="VirtualBounds"/>.</summary>
    public PixelPoint Cursor { get; private set; }

    /// <summary>The selected (or being-dragged) rectangle; empty while hovering.</summary>
    public PixelRect Selection { get; private set; }

    /// <summary>What a click would select right now (hovering only).</summary>
    public PixelRect HoverBounds { get; private set; }

    public WindowInfo? HoverWindow { get; private set; }

    /// <summary>The rectangle to show as "lit": the hover target while hovering, otherwise the selection.</summary>
    public PixelRect Highlight => Phase == SelectionPhase.Hovering ? HoverBounds : Selection;

    public RegionSelectionResult? Result { get; private set; }

    public bool IsFinished => Phase is SelectionPhase.Confirmed or SelectionPhase.Cancelled;

    public void PointerMoved(PixelPoint point)
    {
        if (IsFinished)
        {
            return;
        }

        MoveCursor(point);
        Raise();
    }

    public void PointerPressed(PixelPoint point)
    {
        if (IsFinished)
        {
            return;
        }

        MoveCursor(point);
        _pressed = true;
        _anchor = Cursor;
        Raise();
    }

    public void PointerReleased(PixelPoint point)
    {
        if (IsFinished || !_pressed)
        {
            return;
        }

        MoveCursor(point);
        _pressed = false;

        if (Mode == SelectionMode.WindowOnly)
        {
            if (HoverWindow is not null)
            {
                Confirm();
                return;
            }

            Raise(); // clicked where there is no window: keep hovering
            return;
        }

        if (Phase == SelectionPhase.Dragging)
        {
            Selection = DragRect(_anchor, Cursor);
            Phase = SelectionPhase.Selected;
        }
        else if (Phase == SelectionPhase.Selected && Selection.Contains(Cursor))
        {
            // A click inside the current selection keeps it (and is the first half of a confirming double-click).
        }
        else
        {
            var (bounds, window) = TargetAt(Cursor);
            Selection = bounds;
            _selectedWindow = window;
            HoverBounds = PixelRect.Empty;
            HoverWindow = null;
            Phase = SelectionPhase.Selected;
        }

        Raise();
    }

    /// <summary>A double-click inside the selection confirms it.</summary>
    public void DoubleClicked(PixelPoint point)
    {
        if (Phase == SelectionPhase.Selected && Selection.Contains(Clamp(point)))
        {
            Confirm();
        }
    }

    /// <summary>Moves the selection, keeping it inside the virtual screen.</summary>
    public void Nudge(int dx, int dy)
    {
        if (Phase != SelectionPhase.Selected)
        {
            return;
        }

        var x = Math.Clamp(Selection.X + dx, VirtualBounds.X, VirtualBounds.Right - Selection.Width);
        var y = Math.Clamp(Selection.Y + dy, VirtualBounds.Y, VirtualBounds.Bottom - Selection.Height);
        SetSelection(Selection with { X = x, Y = y });
    }

    /// <summary>Grows or shrinks the selection from its right/bottom edge (minimum 1×1, never past the virtual screen).</summary>
    public void ResizeBy(int dWidth, int dHeight)
    {
        if (Phase != SelectionPhase.Selected)
        {
            return;
        }

        var width = Math.Clamp(Selection.Width + dWidth, 1, VirtualBounds.Right - Selection.X);
        var height = Math.Clamp(Selection.Height + dHeight, 1, VirtualBounds.Bottom - Selection.Y);
        SetSelection(Selection with { Width = width, Height = height });
    }

    /// <summary>
    /// Finishes with the current selection, or — while hovering — with the highlighted target.
    /// Returns false when there is nothing to confirm.
    /// </summary>
    public bool Confirm()
    {
        switch (Phase)
        {
            case SelectionPhase.Selected:
                Result = new RegionSelectionResult(Selection, _selectedWindow);
                break;
            case SelectionPhase.Hovering when !HoverBounds.IsEmpty:
                Selection = HoverBounds;
                Result = new RegionSelectionResult(HoverBounds, HoverWindow);
                break;
            default:
                return false;
        }

        _pressed = false;
        Phase = SelectionPhase.Confirmed;
        Raise();
        return true;
    }

    public void Cancel()
    {
        if (IsFinished)
        {
            return;
        }

        _pressed = false;
        Result = null;
        Phase = SelectionPhase.Cancelled;
        Raise();
    }

    private void MoveCursor(PixelPoint point)
    {
        Cursor = Clamp(point);
        switch (Phase)
        {
            case SelectionPhase.Hovering or SelectionPhase.Selected
                when _pressed && Mode == SelectionMode.RegionOrWindow && Distance(_anchor, Cursor) > _dragThreshold:
                Phase = SelectionPhase.Dragging;
                _selectedWindow = null;
                HoverBounds = PixelRect.Empty;
                HoverWindow = null;
                Selection = DragRect(_anchor, Cursor);
                break;
            case SelectionPhase.Dragging:
                Selection = DragRect(_anchor, Cursor);
                break;
            case SelectionPhase.Hovering:
                UpdateHover();
                break;
        }
    }

    private void SetSelection(PixelRect selection)
    {
        if (selection == Selection)
        {
            return;
        }

        Selection = selection;
        _selectedWindow = null; // no longer exactly the window's rectangle
        Raise();
    }

    private void UpdateHover() => (HoverBounds, HoverWindow) = TargetAt(Cursor);

    private (PixelRect Bounds, WindowInfo? Window) TargetAt(PixelPoint point)
    {
        foreach (var window in _windows)
        {
            if (window.Bounds.Contains(point))
            {
                return (window.Bounds.Intersect(VirtualBounds), window);
            }
        }

        if (Mode == SelectionMode.WindowOnly)
        {
            return (PixelRect.Empty, null);
        }

        foreach (var monitor in _monitors)
        {
            if (monitor.Contains(point))
            {
                return (monitor, null);
            }
        }

        return (PixelRect.Empty, null); // in a gap between monitors
    }

    /// <summary>Both corner pixels are inside: dragging from (10,10) to (19,19) selects 10×10.</summary>
    private static PixelRect DragRect(PixelPoint a, PixelPoint b) =>
        PixelRect.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X) + 1, Math.Max(a.Y, b.Y) + 1);

    private static int Distance(PixelPoint a, PixelPoint b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    private PixelPoint Clamp(PixelPoint point) => new(
        Math.Clamp(point.X, VirtualBounds.X, VirtualBounds.Right - 1),
        Math.Clamp(point.Y, VirtualBounds.Y, VirtualBounds.Bottom - 1));

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}
