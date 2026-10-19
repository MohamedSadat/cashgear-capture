using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Interop.Native;

namespace CashGear.Capture.App.Recording;

/// <summary>
/// The transparent layer over the recorded area that drawings are made on while recording. Unlike the toolbars it
/// is NOT left out of the capture: what is drawn here is in the recording. While the pen is off the layer lets the
/// mouse through to the apps beneath and only shows what was drawn; while it is on it takes the mouse (pen,
/// highlighter, arrow, rectangle) and the keys Esc (done), Delete (clear), Ctrl+Z (undo) and 1–4 (tools). Strokes
/// fade away after a while when the settings say so.
/// </summary>
public sealed class DrawingLayerWindow : Window
{
    private const long WsExTransparent = 0x20;
    private const long WsExNoActivate = 0x08000000;
    private static readonly Brush Catcher = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); // hit-testable, invisible

    private readonly DrawingViewModel _drawing;
    private readonly PixelRect _area;
    private readonly InkCanvas _ink;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<Stroke, (TimeSpan Drawn, Color Color)> _drawn = [];
    private readonly DispatcherTimer? _fade;
    private Stroke? _shape;
    private Point _shapeStart;

    public DrawingLayerWindow(DrawingViewModel drawing, PixelRect area)
    {
        _drawing = drawing;
        _area = area;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Title = "Drawing";
        _ink = new InkCanvas { Background = Brushes.Transparent, EditingMode = InkCanvasEditingMode.None };
        Content = _ink;
        _ink.StrokeCollected += (_, e) => Keep(e.Stroke);
        _ink.PreviewMouseLeftButtonDown += OnShapeStart;
        _ink.PreviewMouseMove += OnShapeMove;
        _ink.PreviewMouseLeftButtonUp += OnShapeEnd;
        KeyDown += OnKey;
        drawing.PropertyChanged += OnDrawingChanged;
        drawing.ClearRequested += OnClear;
        drawing.UndoRequested += OnUndo;
        SourceInitialized += (_, _) =>
        {
            Place();
            Apply();
        };
        if (drawing.FadeSeconds > 0)
        {
            _fade = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Render, (_, _) => Fade(), Dispatcher);
            _fade.Start();
        }

        Closed += (_, _) =>
        {
            _fade?.Stop();
            drawing.PropertyChanged -= OnDrawingChanged;
            drawing.ClearRequested -= OnClear;
            drawing.UndoRequested -= OnUndo;
        };
    }

    /// <summary>The strokes drawn and still there.</summary>
    public StrokeCollection Strokes => _ink.Strokes;

    /// <summary>Draws a stroke through <paramref name="points"/> (in the layer's own units) with the current pen, as the mouse would.</summary>
    public void Draw(IReadOnlyList<Point> points)
    {
        var stroke = new Stroke(new StylusPointCollection(points), Attributes());
        _ink.Strokes.Add(stroke);
        Keep(stroke);
    }

    private static Color ColorOf(string hex) =>
        OverlaySettings.TryParseColor(hex, out var argb) ? Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb) : Colors.Red;

    /// <summary>Covers the recorded area exactly, in physical pixels whatever the monitor's scale.</summary>
    private void Place() => User32.SetWindowPos(
        new WindowInteropHelper(this).Handle, 0, _area.X, _area.Y, _area.Width, _area.Height, User32.SwpNoActivate | 0x0004 /* NOZORDER */);

    private void OnDrawingChanged(object? sender, PropertyChangedEventArgs e) => Apply();

    /// <summary>Pen on: the layer takes the mouse and the keys. Pen off: it lets them through and only shows the drawings.</summary>
    private void Apply()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0)
        {
            return;
        }

        var style = (long)User32.GetWindowLongPtrW(handle, User32.GwlExStyle);
        style = _drawing.IsActive ? style & ~(WsExTransparent | WsExNoActivate) : style | WsExTransparent | WsExNoActivate;
        User32.SetWindowLongPtrW(handle, User32.GwlExStyle, (nint)style);
        _ink.Background = _drawing.IsActive ? Catcher : Brushes.Transparent;
        _ink.EditingMode = _drawing.IsActive && _drawing.Tool is DrawingTool.Pen or DrawingTool.Highlighter ? InkCanvasEditingMode.Ink : InkCanvasEditingMode.None;
        _ink.DefaultDrawingAttributes = Attributes();
        _ink.Cursor = _drawing.IsActive ? Cursors.Pen : Cursors.Arrow;
        if (_drawing.IsActive)
        {
            Activate();
            Keyboard.Focus(_ink);
        }
    }

    private DrawingAttributes Attributes()
    {
        var highlighter = _drawing.Tool == DrawingTool.Highlighter;
        var width = highlighter ? _drawing.Width * 4 : _drawing.Width;
        return new DrawingAttributes
        {
            Color = ColorOf(_drawing.Color),
            Width = width,
            Height = width,
            IsHighlighter = highlighter,
            StylusTip = highlighter ? StylusTip.Rectangle : StylusTip.Ellipse,
            FitToCurve = !highlighter && _drawing.Tool == DrawingTool.Pen,
        };
    }

    private void Keep(Stroke stroke) => _drawn[stroke] = (_clock.Elapsed, stroke.DrawingAttributes.Color);

    private void OnShapeStart(object sender, MouseButtonEventArgs e)
    {
        if (!_drawing.IsActive || _drawing.Tool is not (DrawingTool.Arrow or DrawingTool.Rectangle))
        {
            return;
        }

        _shapeStart = e.GetPosition(_ink);
        _shape = new Stroke(new StylusPointCollection([_shapeStart, _shapeStart]), Attributes());
        _ink.Strokes.Add(_shape);
        _ink.CaptureMouse();
        e.Handled = true;
    }

    private void OnShapeMove(object sender, MouseEventArgs e)
    {
        if (_shape is null)
        {
            return;
        }

        var to = e.GetPosition(_ink);
        var points = _drawing.Tool == DrawingTool.Arrow ? DrawingShapes.Arrow(_shapeStart, to, _drawing.Width) : DrawingShapes.Rectangle(_shapeStart, to);
        _shape.StylusPoints = new StylusPointCollection(points);
    }

    private void OnShapeEnd(object sender, MouseButtonEventArgs e)
    {
        if (_shape is null)
        {
            return;
        }

        OnShapeMove(sender, e);
        Keep(_shape);
        _shape = null;
        _ink.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (!_drawing.IsActive)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                _drawing.IsActive = false;
                break;
            case Key.Delete or Key.Back:
                _drawing.Clear();
                break;
            case Key.Z when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                _drawing.Undo();
                break;
            case Key.D1 or Key.NumPad1:
                _drawing.Tool = DrawingTool.Pen;
                break;
            case Key.D2 or Key.NumPad2:
                _drawing.Tool = DrawingTool.Highlighter;
                break;
            case Key.D3 or Key.NumPad3:
                _drawing.Tool = DrawingTool.Arrow;
                break;
            case Key.D4 or Key.NumPad4:
                _drawing.Tool = DrawingTool.Rectangle;
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void OnClear(object? sender, EventArgs e)
    {
        _ink.Strokes.Clear();
        _drawn.Clear();
    }

    private void OnUndo(object? sender, EventArgs e)
    {
        if (_ink.Strokes.Count > 0)
        {
            var last = _ink.Strokes[^1];
            _ink.Strokes.Remove(last);
            _drawn.Remove(last);
        }
    }

    /// <summary>Strokes fade out over their last half second, then go.</summary>
    private void Fade()
    {
        var now = _clock.Elapsed;
        foreach (var (stroke, (drawn, color)) in _drawn.ToList())
        {
            var opacity = DrawingShapes.Opacity(now - drawn, _drawing.FadeSeconds);
            if (opacity <= 0)
            {
                _ink.Strokes.Remove(stroke);
                _drawn.Remove(stroke);
            }
            else if (opacity < 1)
            {
                stroke.DrawingAttributes.Color = Color.FromArgb((byte)(color.A * opacity), color.R, color.G, color.B);
            }
        }
    }
}
