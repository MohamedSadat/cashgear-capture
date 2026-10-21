using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Resources;

namespace CashGear.Capture.Core.Selection;

/// <summary>
/// An outline drawn by hand:
/// <list type="bullet">
/// <item><see cref="SelectionMode.Freehand"/>: press, draw with the button held, let go to finish. The trail is
/// closed straight back to where it started. A trail smaller than <see cref="MinimumSize"/> is ignored.</item>
/// <item><see cref="SelectionMode.Polygon"/>: each click adds a corner. A click on the first corner (within
/// <see cref="CloseDistance"/>), a double-click or Enter finishes; Backspace takes the last corner away.</item>
/// </list>
/// The result is a <see cref="ShapeKind.Polygon"/> through the cursor pixels.
/// </summary>
public sealed class ShapeSelectionModel : ISelectionModel
{
    /// <summary>The smallest outline (width and height, pixels) that is kept.</summary>
    public const int MinimumSize = 3;

    /// <summary>How near the first corner a click must be to close a polygon (pixels).</summary>
    public const int CloseDistance = 8;

    private readonly List<PixelPoint> _points = [];

    public ShapeSelectionModel(IReadOnlyList<PixelRect> monitorBounds, PixelPoint initialCursor, SelectionMode mode)
    {
        if (mode is not (SelectionMode.Freehand or SelectionMode.Polygon))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Freehand or Polygon.");
        }

        Mode = mode;
        VirtualBounds = VirtualScreen.GetBounds(SelectionMath.ValidMonitors(monitorBounds));
        Cursor = SelectionMath.Clamp(initialCursor, VirtualBounds);
    }

    public event EventHandler? Changed;

    public SelectionMode Mode { get; }

    public PixelRect VirtualBounds { get; }

    /// <summary>Hovering before the first point, <see cref="SelectionPhase.Dragging"/> while the outline is being drawn.</summary>
    public SelectionPhase Phase { get; private set; } = SelectionPhase.Hovering;

    public PixelPoint Cursor { get; private set; }

    /// <summary>The trail or the corners so far.</summary>
    public IReadOnlyList<PixelPoint> Points => _points;

    public bool IsFinished => Phase is SelectionPhase.Confirmed or SelectionPhase.Cancelled;

    public RegionSelectionResult? Result { get; private set; }

    private bool IsPolygon => Mode == SelectionMode.Polygon;

    /// <summary>The polygon's corners with the cursor as the next one (the outline a click would make).</summary>
    private IReadOnlyList<PixelPoint> Outline => IsPolygon && Phase == SelectionPhase.Dragging && _points[^1] != Cursor ? [.. _points, Cursor] : _points;

    private bool CursorOnFirst => IsPolygon && _points.Count >= 3 && SelectionMath.Distance(_points[0], Cursor) <= CloseDistance;

    public SelectionView View
    {
        get
        {
            var outline = Outline;
            var shape = outline.Count >= 3 ? CaptureShape.Polygon(outline) : null;
            var bounds = shape?.Bounds ?? (outline.Count > 0 ? PixelRect.FromLTRB(outline.Min(p => p.X), outline.Min(p => p.Y), outline.Max(p => p.X) + 1, outline.Max(p => p.Y) + 1) : PixelRect.Empty);
            return new SelectionView
            {
                Lit = shape is null ? PixelRect.Empty : bounds,
                LitShape = shape,
                Path = _points.Count > 0 ? _points.ToArray() : null,
                RubberBand = IsPolygon && Phase == SelectionPhase.Dragging,
                CanClosePath = CursorOnFirst,
                Label = bounds.IsEmpty ? null : $"{bounds.Width} × {bounds.Height}",
                Hint = IsPolygon
                    ? CoreStrings.PolygonHint
                    : CoreStrings.FreehandHint,
                Tracking = !IsFinished,
            };
        }
    }

    public void PointerMoved(PixelPoint point)
    {
        if (IsFinished)
        {
            return;
        }

        Cursor = SelectionMath.Clamp(point, VirtualBounds);
        if (!IsPolygon && Phase == SelectionPhase.Dragging && _points[^1] != Cursor)
        {
            _points.Add(Cursor);
        }

        Raise();
    }

    public void PointerPressed(PixelPoint point)
    {
        if (IsFinished)
        {
            return;
        }

        Cursor = SelectionMath.Clamp(point, VirtualBounds);
        if (!IsPolygon)
        {
            _points.Clear();
            _points.Add(Cursor);
            Phase = SelectionPhase.Dragging;
            Raise();
            return;
        }

        if (CursorOnFirst)
        {
            Confirm();
            return;
        }

        if (_points.Count == 0 || _points[^1] != Cursor)
        {
            _points.Add(Cursor);
        }

        Phase = SelectionPhase.Dragging;
        Raise();
    }

    public void PointerReleased(PixelPoint point)
    {
        if (IsFinished || IsPolygon || Phase != SelectionPhase.Dragging)
        {
            return;
        }

        PointerMoved(point);
        if (!Confirm())
        {
            // Too small to be meant: start again.
            _points.Clear();
            Phase = SelectionPhase.Hovering;
            Raise();
        }
    }

    /// <summary>A polygon's double-click: its first click added the last corner; this finishes.</summary>
    public void DoubleClicked(PixelPoint point)
    {
        if (IsPolygon && !IsFinished)
        {
            Cursor = SelectionMath.Clamp(point, VirtualBounds);
            Confirm();
        }
    }

    public void Wheel(int notches)
    {
    }

    public bool KeyPressed(SelectionKey key, SelectionModifiers modifiers)
    {
        if (IsFinished || !IsPolygon)
        {
            return false;
        }

        switch (key)
        {
            case SelectionKey.Enter:
                Confirm();
                return true;

            case SelectionKey.Backspace when _points.Count > 0:
                _points.RemoveAt(_points.Count - 1);
                if (_points.Count == 0)
                {
                    Phase = SelectionPhase.Hovering;
                }

                Raise();
                return true;

            default:
                return false;
        }
    }

    /// <summary>Finishes with the outline; false (nothing changes) when it has fewer than three points or is too small.</summary>
    public bool Confirm()
    {
        if (IsFinished)
        {
            return false;
        }

        var points = IsPolygon ? Deduplicated(_points) : PathSimplifier.Simplify(Deduplicated(_points), PathSimplifier.FreehandTolerance);
        if (points.Count < 3)
        {
            return false;
        }

        var shape = CaptureShape.Polygon(points);
        if (shape.Bounds.Width < MinimumSize || shape.Bounds.Height < MinimumSize)
        {
            return false;
        }

        Result = new RegionSelectionResult(shape.Bounds, null) { Shape = shape };
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

    /// <summary>Without points repeated one after the other, nor a last point equal to the first.</summary>
    private static List<PixelPoint> Deduplicated(IReadOnlyList<PixelPoint> points)
    {
        var result = new List<PixelPoint>(points.Count);
        foreach (var p in points)
        {
            if (result.Count == 0 || result[^1] != p)
            {
                result.Add(p);
            }
        }

        while (result.Count > 1 && result[^1] == result[0])
        {
            result.RemoveAt(result.Count - 1);
        }

        return result;
    }

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}
