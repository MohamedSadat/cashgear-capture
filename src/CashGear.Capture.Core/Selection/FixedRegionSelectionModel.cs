using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Resources;

namespace CashGear.Capture.Core.Selection;

/// <summary>
/// A frame of a set size (<see cref="SelectionMode.FixedSize"/>):
/// <list type="bullet">
/// <item>While hovering, its top-left corner follows the cursor (so the loupe shows that corner's pixel), kept
/// inside the virtual screen.</item>
/// <item>A click places it; pressing inside it and dragging moves it (outside: it jumps there first); arrows nudge
/// it once placed, Ctrl by 10 px.</item>
/// <item>Tab and Shift+Tab switch to the next or previous size, keeping the top-left corner.</item>
/// <item>Enter, or a double-click inside it, finishes.</item>
/// </list>
/// A size larger than the virtual screen is cut down to it.
/// </summary>
public sealed class FixedRegionSelectionModel : ISelectionModel
{
    private readonly IReadOnlyList<PixelSize> _sizes;
    private int _index;
    private PixelPoint _grab; // cursor minus the frame's top-left while moving it

    public FixedRegionSelectionModel(IReadOnlyList<PixelRect> monitorBounds, PixelPoint initialCursor, IReadOnlyList<PixelSize> sizes, int sizeIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(sizes);
        var monitors = SelectionMath.ValidMonitors(monitorBounds);
        _sizes = sizes.Where(s => !s.IsEmpty).ToList() is { Count: > 0 } valid ? valid : [new PixelSize(800, 600)];
        _index = Math.Clamp(sizeIndex, 0, _sizes.Count - 1);
        VirtualBounds = VirtualScreen.GetBounds(monitors);
        Cursor = SelectionMath.Clamp(initialCursor, VirtualBounds);
        Selection = FrameAt(Cursor);
    }

    public event EventHandler? Changed;

    public PixelRect VirtualBounds { get; }

    public SelectionPhase Phase { get; private set; } = SelectionPhase.Hovering;

    public PixelPoint Cursor { get; private set; }

    /// <summary>The frame: where it would be placed while hovering, else where it is.</summary>
    public PixelRect Selection { get; private set; }

    /// <summary>The size in use (as chosen; the frame may be cut down to the virtual screen).</summary>
    public PixelSize Size => _sizes[_index];

    public IReadOnlyList<PixelSize> Sizes => _sizes;

    public bool IsFinished => Phase is SelectionPhase.Confirmed or SelectionPhase.Cancelled;

    public RegionSelectionResult? Result { get; private set; }

    public SelectionView View => new()
    {
        Lit = Selection,
        Label = $"{Selection.Width} × {Selection.Height}",
        Hint = Phase == SelectionPhase.Hovering
            ? CoreStrings.FixedRegionHintHovering
            : CoreStrings.FixedRegionHintPlaced,
        Tracking = Phase is SelectionPhase.Hovering or SelectionPhase.Dragging,
    };

    public void PointerMoved(PixelPoint point)
    {
        if (IsFinished)
        {
            return;
        }

        Cursor = SelectionMath.Clamp(point, VirtualBounds);
        if (Phase == SelectionPhase.Hovering)
        {
            Selection = FrameAt(Cursor);
        }
        else if (Phase == SelectionPhase.Dragging)
        {
            Selection = FrameAt(new PixelPoint(Cursor.X - _grab.X, Cursor.Y - _grab.Y));
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
        if (Phase != SelectionPhase.Selected || !Selection.Contains(Cursor))
        {
            Selection = FrameAt(Cursor);
        }

        _grab = new PixelPoint(Cursor.X - Selection.X, Cursor.Y - Selection.Y);
        Phase = SelectionPhase.Dragging;
        Raise();
    }

    public void PointerReleased(PixelPoint point)
    {
        if (Phase != SelectionPhase.Dragging)
        {
            return;
        }

        PointerMoved(point);
        Phase = SelectionPhase.Selected;
        Raise();
    }

    public void DoubleClicked(PixelPoint point)
    {
        if (!IsFinished && Selection.Contains(SelectionMath.Clamp(point, VirtualBounds)))
        {
            Confirm();
        }
    }

    public void Wheel(int notches)
    {
    }

    public bool KeyPressed(SelectionKey key, SelectionModifiers modifiers)
    {
        if (IsFinished)
        {
            return false;
        }

        switch (key)
        {
            case SelectionKey.Enter:
                Confirm();
                return true;

            case SelectionKey.Tab:
                _index = (_index + (modifiers.HasFlag(SelectionModifiers.Shift) ? _sizes.Count - 1 : 1)) % _sizes.Count;
                Selection = FrameAt(Selection.TopLeft);
                Raise();
                return true;

            case var arrow when SelectionMath.IsArrow(arrow) && Phase == SelectionPhase.Selected:
                var (dx, dy) = SelectionMath.Direction(arrow);
                var step = SelectionMath.Step(modifiers);
                Selection = FrameAt(Selection.TopLeft.Offset(dx * step, dy * step));
                Raise();
                return true;

            default:
                return false; // arrows while hovering move the pointer, and the frame with it
        }
    }

    public bool Confirm()
    {
        if (IsFinished)
        {
            return false;
        }

        Result = new RegionSelectionResult(Selection, null);
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

    /// <summary>The frame with its top-left at <paramref name="topLeft"/>, moved back inside the virtual screen.</summary>
    private PixelRect FrameAt(PixelPoint topLeft)
    {
        var width = Math.Min(Size.Width, VirtualBounds.Width);
        var height = Math.Min(Size.Height, VirtualBounds.Height);
        var x = Math.Clamp(topLeft.X, VirtualBounds.X, VirtualBounds.Right - width);
        var y = Math.Clamp(topLeft.Y, VirtualBounds.Y, VirtualBounds.Bottom - height);
        return new PixelRect(x, y, width, height);
    }

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}
