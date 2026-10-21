using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Resources;

namespace CashGear.Capture.Core.Selection;

/// <summary>
/// Picks one pixel (<see cref="SelectionMode.Point"/>, the colour picker): the guides and the loupe follow the
/// cursor over the undimmed screen, and a click or Enter picks the pixel under it. Arrows are left to the overlay,
/// which moves the pointer a pixel at a time.
/// </summary>
public sealed class PointSelectionModel : ISelectionModel
{
    private bool _pressed;

    public PointSelectionModel(IReadOnlyList<PixelRect> monitorBounds, PixelPoint initialCursor)
    {
        VirtualBounds = VirtualScreen.GetBounds(SelectionMath.ValidMonitors(monitorBounds));
        Cursor = SelectionMath.Clamp(initialCursor, VirtualBounds);
    }

    public event EventHandler? Changed;

    public PixelRect VirtualBounds { get; }

    public SelectionPhase Phase { get; private set; } = SelectionPhase.Hovering;

    public PixelPoint Cursor { get; private set; }

    public bool IsFinished => Phase is SelectionPhase.Confirmed or SelectionPhase.Cancelled;

    public RegionSelectionResult? Result { get; private set; }

    public SelectionView View => new()
    {
        Hint = CoreStrings.ColourPickerHint,
        Tracking = !IsFinished,
        Dim = false,
    };

    public void PointerMoved(PixelPoint point)
    {
        if (IsFinished)
        {
            return;
        }

        Cursor = SelectionMath.Clamp(point, VirtualBounds);
        Raise();
    }

    public void PointerPressed(PixelPoint point)
    {
        PointerMoved(point);
        _pressed = !IsFinished;
    }

    public void PointerReleased(PixelPoint point)
    {
        if (!_pressed)
        {
            return;
        }

        _pressed = false;
        PointerMoved(point);
        Confirm();
    }

    public void DoubleClicked(PixelPoint point)
    {
    }

    public void Wheel(int notches)
    {
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
        if (IsFinished)
        {
            return false;
        }

        Result = new RegionSelectionResult(new PixelRect(Cursor.X, Cursor.Y, 1, 1), null);
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

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}
