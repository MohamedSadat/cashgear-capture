using System.Windows;
using System.Windows.Input;
using CashGear.Capture.App.Wpf;
using CashGear.Capture.Editor.Interaction;
using CashGear.Capture.Editor.Rendering;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace CashGear.Capture.App.Editor;

/// <summary>
/// The Skia surface the image is edited on. Draws through <see cref="ViewRenderer"/> at physical-pixel resolution
/// and turns mouse input into image coordinates for the <see cref="EditorSession"/>. Panning (middle button, or
/// Space + drag) and wheel zoom are handled here; everything else is the session's.
/// </summary>
internal sealed class EditorCanvas : SKElement
{
    private readonly ViewRenderer _renderer = new();
    private EditorViewModel? _viewModel;
    private SKPoint? _panFrom;
    private MouseButton _panButton;
    private SKPoint _lastImagePoint;
    private bool _spaceHeld;

    public EditorCanvas()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
    }

    /// <summary>Raised when the session asks for text to be typed at a point (the window shows its text box).</summary>
    public event EventHandler<TextEditRequest>? TextEditRequested;

    public SKColor CanvasColor
    {
        get => _renderer.Background;
        set
        {
            _renderer.Background = value;
            InvalidateVisual();
        }
    }

    /// <summary>Space held: left-drag pans instead of editing.</summary>
    public bool SpaceHeld
    {
        get => _spaceHeld;
        set
        {
            _spaceHeld = value;
            UpdateCursor();
        }
    }

    public void Attach(EditorViewModel viewModel)
    {
        _viewModel = viewModel;
        viewModel.Session.Invalidated += (_, _) => InvalidateVisual();
        viewModel.Session.TextEditRequested += (_, request) => TextEditRequested?.Invoke(this, request);
        viewModel.Document.Changed += (_, _) => InvalidateVisual();
        viewModel.Viewport.Changed += (_, _) => InvalidateVisual();
    }

    /// <summary>Physical pixels per DIP for this element (follows the monitor under PerMonitorV2).</summary>
    public float UiScale => (float)DpiHelper.GetScale(this).ScaleX;

    /// <summary>A position in this element (DIPs) as view pixels (the viewport's space).</summary>
    public SKPoint ToViewPixels(Point dip)
    {
        var scale = DpiHelper.GetScale(this);
        return new SKPoint((float)(dip.X * scale.ScaleX), (float)(dip.Y * scale.ScaleY));
    }

    /// <summary>A point in view pixels back to DIPs (to place WPF elements over the canvas).</summary>
    public Point ToDips(SKPoint view)
    {
        var scale = DpiHelper.GetScale(this);
        return new Point(view.X / scale.ScaleX, view.Y / scale.ScaleY);
    }

    public void Release() => _renderer.Dispose();

    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
    {
        base.OnPaintSurface(e);
        if (_viewModel is null)
        {
            return;
        }

        _renderer.UiScale = UiScale;
        _viewModel.Viewport.SetViewSize(new SKSize(e.Info.Width, e.Info.Height));
        _renderer.Render(e.Surface.Canvas, _viewModel.Session, _viewModel.Viewport);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (_viewModel is null)
        {
            return;
        }

        // Clicking away from in-place text first finishes it (focus moves here); that click does nothing else.
        var wasTyping = _viewModel.Session.TextEdit is not null;
        Focus();
        if (wasTyping)
        {
            e.Handled = true;
            return;
        }

        var view = ToViewPixels(e.GetPosition(this));
        if (e.ChangedButton == MouseButton.Middle || (e.ChangedButton == MouseButton.Left && SpaceHeld))
        {
            _panFrom = view;
            _panButton = e.ChangedButton;
            CaptureMouse();
            Cursor = Cursors.SizeAll;
            e.Handled = true;
            return;
        }

        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        CaptureMouse();
        _lastImagePoint = _viewModel.Viewport.ToImage(view);
        _viewModel.Session.PointerDown(_lastImagePoint, Modifiers(), e.ClickCount, _renderer.Tolerance(_viewModel.Viewport));
        UpdateCursor();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_viewModel is null)
        {
            return;
        }

        var view = ToViewPixels(e.GetPosition(this));
        if (_panFrom is { } from)
        {
            _viewModel.Viewport.PanBy(view.X - from.X, view.Y - from.Y);
            _panFrom = view;
            return;
        }

        _lastImagePoint = _viewModel.Viewport.ToImage(view);
        if (_viewModel.Session.IsDragging)
        {
            _viewModel.Session.PointerMove(_lastImagePoint, Modifiers());
        }

        _viewModel.UpdatePointer(_lastImagePoint);
        UpdateCursor();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_viewModel is null)
        {
            return;
        }

        if (_panFrom is not null && e.ChangedButton == _panButton)
        {
            _panFrom = null;
            ReleaseMouseCapture();
            UpdateCursor();
            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Left && _viewModel.Session.IsDragging)
        {
            _lastImagePoint = _viewModel.Viewport.ToImage(ToViewPixels(e.GetPosition(this)));
            _viewModel.Session.PointerUp(_lastImagePoint, Modifiers());
            ReleaseMouseCapture();
            UpdateCursor();
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.Left)
        {
            ReleaseMouseCapture();
        }
    }

    /// <summary>Capture lost mid-drag (Alt+Tab, a dialog): keep what was drawn so far rather than leave the drag hanging.</summary>
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _panFrom = null;
        if (_viewModel?.Session.IsDragging == true)
        {
            _viewModel.Session.PointerUp(_lastImagePoint, PointerModifiers.None);
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _viewModel?.UpdatePointer(null);
    }

    /// <summary>Ctrl+wheel zooms at the mouse; the wheel scrolls (Shift: sideways).</summary>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_viewModel is null)
        {
            return;
        }

        var view = ToViewPixels(e.GetPosition(this));
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (e.Delta > 0)
            {
                _viewModel.Viewport.ZoomIn(view);
            }
            else
            {
                _viewModel.Viewport.ZoomOut(view);
            }
        }
        else
        {
            var step = e.Delta * 0.5f * UiScale;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                _viewModel.Viewport.PanBy(step, 0);
            }
            else
            {
                _viewModel.Viewport.PanBy(0, step);
            }
        }

        e.Handled = true;
    }

    private static PointerModifiers Modifiers()
    {
        var modifiers = PointerModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            modifiers |= PointerModifiers.Shift;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            modifiers |= PointerModifiers.Control;
        }

        return modifiers;
    }

    private void UpdateCursor()
    {
        if (_viewModel is null)
        {
            return;
        }

        if (_panFrom is not null || SpaceHeld)
        {
            Cursor = _panFrom is null ? Cursors.Hand : Cursors.SizeAll;
            return;
        }

        Cursor = _viewModel.Session.CursorAt(_lastImagePoint, _renderer.Tolerance(_viewModel.Viewport)) switch
        {
            EditorCursor.Crosshair => Cursors.Cross,
            EditorCursor.Move => Cursors.SizeAll,
            EditorCursor.SizeNWSE => Cursors.SizeNWSE,
            EditorCursor.SizeNESW => Cursors.SizeNESW,
            EditorCursor.SizeNS => Cursors.SizeNS,
            EditorCursor.SizeWE => Cursors.SizeWE,
            EditorCursor.IBeam => Cursors.IBeam,
            _ => Cursors.Arrow,
        };
    }
}
