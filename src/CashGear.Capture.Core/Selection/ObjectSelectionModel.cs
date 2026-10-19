using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;

namespace CashGear.Capture.Core.Selection;

/// <summary>Asks for the objects of <paramref name="Window"/> at <paramref name="Point"/> (see <see cref="ObjectSelectionModel"/>).</summary>
public sealed record ObjectLookup(WindowInfo Window, PixelPoint Point);

/// <summary>
/// Capture Object (<see cref="SelectionMode.Object"/>): the part of a window under the cursor is lit — a button, a
/// text box, a toolbar, a panel — as UI Automation reports it:
/// <list type="bullet">
/// <item>The topmost window under the cursor (from the snapshot taken before the overlay) is lit at once; the model
/// raises <see cref="LookupNeeded"/>, and <see cref="SetObjects"/> brings the objects inside it (a lookup crosses
/// into the other app, so it is asynchronous; a late answer for somewhere else is ignored).</item>
/// <item>The innermost object containing the cursor is lit. The mouse wheel goes out to what contains it (away
/// from you) and back in.</item>
/// <item>A click (or Enter) captures the lit object.</item>
/// </list>
/// </summary>
public sealed class ObjectSelectionModel : ISelectionModel
{
    private readonly IReadOnlyList<WindowInfo> _windows;
    private IReadOnlyList<ScreenObject> _objects = []; // innermost first, the window last
    private WindowInfo? _window;
    private bool _pressed;
    private PixelPoint _pressedAt;
    private int _level; // 0: the innermost object containing the cursor

    public ObjectSelectionModel(IReadOnlyList<PixelRect> monitorBounds, IReadOnlyList<WindowInfo> windows, PixelPoint initialCursor)
    {
        ArgumentNullException.ThrowIfNull(windows);
        VirtualBounds = VirtualScreen.GetBounds(SelectionMath.ValidMonitors(monitorBounds));
        _windows = windows.Where(w => !w.IsMinimized && !w.Bounds.IsEmpty).OrderBy(w => w.ZOrder).ToList();
        Cursor = SelectionMath.Clamp(initialCursor, VirtualBounds);
        Retarget();
    }

    public event EventHandler? Changed;

    /// <summary>Raised when the objects at the cursor are needed (the cursor left the innermost one, or another window is under it).</summary>
    public event EventHandler<ObjectLookup>? LookupNeeded;

    public PixelRect VirtualBounds { get; }

    public SelectionPhase Phase { get; private set; } = SelectionPhase.Hovering;

    public PixelPoint Cursor { get; private set; }

    public bool IsFinished => Phase is SelectionPhase.Confirmed or SelectionPhase.Cancelled;

    public RegionSelectionResult? Result { get; private set; }

    /// <summary>The window under the cursor (null over the desktop or a gap).</summary>
    public WindowInfo? HoverWindow => _window;

    /// <summary>The objects around the cursor, innermost first, the window last.</summary>
    public IReadOnlyList<ScreenObject> Around => _objects.Where(o => o.Bounds.Contains(Cursor)).ToList() is { Count: > 0 } around
        ? around
        : _window is null ? [] : [WindowObject(_window)];

    /// <summary>What a click captures now.</summary>
    public ScreenObject? Lit => Around is { Count: > 0 } around ? around[Math.Min(_level, around.Count - 1)] : null;

    public SelectionView View
    {
        get
        {
            var lit = Lit;
            var bounds = lit?.Bounds.Intersect(VirtualBounds) ?? PixelRect.Empty;
            return new SelectionView
            {
                Lit = bounds,
                Label = lit is null ? null : $"{lit.Description}   {bounds.Width} × {bounds.Height}",
                Hint = "Click to capture · mouse wheel up: what it is in · wheel down: back in",
                Tracking = false,
            };
        }
    }

    /// <summary>The objects of <paramref name="window"/> at <paramref name="point"/>, innermost first (from <see cref="IObjectFinder"/>).</summary>
    public void SetObjects(nint window, PixelPoint point, IReadOnlyList<ScreenObject> objects)
    {
        ArgumentNullException.ThrowIfNull(objects);
        if (IsFinished || _window?.Handle != window)
        {
            return; // the cursor has moved to another window since
        }

        // The window's own element (UI Automation gives it with the invisible resize borders) is replaced by the window's visible frame.
        var innermost = Lit;
        var found = new List<ScreenObject>();
        foreach (var o in objects.Where(o => !o.Bounds.IsEmpty && !o.Bounds.Contains(_window.Bounds)).Append(WindowObject(_window)))
        {
            if (found.Count == 0 || found[^1].Bounds != o.Bounds)
            {
                found.Add(o); // the same rectangle again (a pane filling its parent) would be a wheel step that shows nothing new
            }
        }

        _objects = found;
        if (Lit != innermost)
        {
            _level = 0;
        }

        Raise();
    }

    public void PointerMoved(PixelPoint point)
    {
        if (IsFinished)
        {
            return;
        }

        Cursor = SelectionMath.Clamp(point, VirtualBounds);
        Retarget();
        Raise();
    }

    public void PointerPressed(PixelPoint point)
    {
        PointerMoved(point);
        _pressed = !IsFinished;
        _pressedAt = Cursor;
    }

    public void PointerReleased(PixelPoint point)
    {
        if (!_pressed)
        {
            return;
        }

        _pressed = false;
        PointerMoved(point);
        if (SelectionMath.Distance(_pressedAt, Cursor) <= RegionSelectionModel.DefaultDragThreshold)
        {
            Confirm();
        }
    }

    public void DoubleClicked(PixelPoint point)
    {
    }

    /// <summary>Up (positive): the object that contains the lit one; down: back towards the innermost.</summary>
    public void Wheel(int notches)
    {
        if (IsFinished || notches == 0)
        {
            return;
        }

        var level = Math.Clamp(_level + notches, 0, Math.Max(0, Around.Count - 1));
        if (level != _level)
        {
            _level = level;
            Raise();
        }
    }

    public bool KeyPressed(SelectionKey key, SelectionModifiers modifiers)
    {
        if (key != SelectionKey.Enter)
        {
            return false;
        }

        Confirm();
        return true;
    }

    public bool Confirm()
    {
        if (IsFinished || Lit is not { } lit || lit.Bounds.Intersect(VirtualBounds).IsEmpty)
        {
            return false;
        }

        Result = new RegionSelectionResult(lit.Bounds.Intersect(VirtualBounds), _window) { ObjectName = lit.Description };
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

        Result = null;
        Phase = SelectionPhase.Cancelled;
        Raise();
    }

    /// <summary>Follows the cursor: another window, or out of the innermost object known, needs a lookup.</summary>
    private void Retarget()
    {
        var window = _windows.FirstOrDefault(w => w.Bounds.Contains(Cursor));
        if (window?.Handle != _window?.Handle)
        {
            _window = window;
            _objects = window is null ? [] : [WindowObject(window)];
            _level = 0;
        }
        else if (_objects.Count == 0 || _objects[0].Bounds.Contains(Cursor))
        {
            return; // still inside the innermost object known: nothing new to find
        }

        if (_window is not null)
        {
            LookupNeeded?.Invoke(this, new ObjectLookup(_window, Cursor));
        }
    }

    private static ScreenObject WindowObject(WindowInfo window) => new(window.Bounds, window.Title, "window");

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}
