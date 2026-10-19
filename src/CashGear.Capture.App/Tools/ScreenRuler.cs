using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Wpf;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Interop.Native;
using WpfDpiScale = System.Windows.DpiScale;

namespace CashGear.Capture.App.Tools;

/// <summary>
/// The screen ruler: a translucent strip with marks in pixels, inches or centimetres, and a red line where the
/// pointer is along it with the distance from its zero end.
/// <list type="bullet">
/// <item>Drag it to move it, drag its far end to make it longer or shorter; double-click turns it (across or down);
/// right-click for the unit; arrows move it a pixel (Ctrl: 10), Shift+arrows change its length; Esc closes it.</item>
/// <item>It is drawn in physical pixels, so a pixel mark is a screen pixel at any scale; inches and centimetres use
/// the DPI of the monitor it is on (96 per inch at 100%, 144 at 150%), or the DPI set in Settings to match the real
/// screen.</item>
/// <item>Left out of captures. Where it was, its length, direction and unit are remembered.</item>
/// </list>
/// </summary>
public sealed class RulerWindow : Window, IThemedWindow, IScreenToolWindow
{
    public const int MinLength = 100;
    private const int DefaultLength = 800;
    private const int ThicknessDips = 56;
    private const int EndGrip = 14;

    private readonly IAppStateStore _state;
    private readonly IMonitorService _monitors;
    private readonly ISettingsService _settings;
    private readonly RulerVisual _visual;
    private readonly Grid _canvas = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly DispatcherTimer _pointer;
    private DragKind _drag;
    private PixelPoint _grab;

    public RulerWindow(IAppStateStore state, IMonitorService monitors, ISettingsService settings)
    {
        _state = state;
        _monitors = monitors;
        _settings = settings;
        var placement = state.Current.Ruler;
        Length = Math.Max(MinLength, placement?.Length ?? DefaultLength);
        IsVertical = placement?.Vertical ?? false;
        Unit = placement?.Unit ?? RulerUnit.Pixels;
        Origin = placement is null ? null : new PixelPoint(placement.X, placement.Y);

        Title = "CashGear Capture - ruler";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Background = RulerVisual.Body;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000;
        Top = -32000;
        Width = 10;
        Height = 10;
        Focusable = true;
        _visual = new RulerVisual(this);
        _canvas.Children.Add(_visual);
        Content = _canvas;
        ContextMenu = BuildMenu();
        ApplyTheme(settings.Current.Appearance.Theme);

        SourceInitialized += (_, _) => ToolWindowStyle.Apply(this, clickThrough: false, popup: true);
        Loaded += (_, _) => Place(Origin ?? DefaultOrigin());
        _pointer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, (_, _) => FollowPointer(), Dispatcher);
        _pointer.Start();
        Closed += (_, _) => _pointer.Stop();
    }

    private enum DragKind
    {
        None,
        Move,
        Resize,
    }

    /// <summary>Its length along its direction (physical pixels).</summary>
    public int Length { get; private set; }

    /// <summary>It runs down the screen instead of across.</summary>
    public bool IsVertical { get; private set; }

    public RulerUnit Unit { get; private set; }

    /// <summary>Its top-left on the screen (virtual-screen pixels), once placed.</summary>
    public PixelPoint? Origin { get; private set; }

    /// <summary>The pointer's distance from the zero end, when the pointer is beside the ruler.</summary>
    public double? Pointer { get; private set; }

    /// <summary>The pixels per inch used for inches and centimetres.</summary>
    public double Dpi => _settings.Current.Tools.RulerDpi > 0 ? _settings.Current.Tools.RulerDpi : Monitor().DpiX;

    /// <summary>Its thickness across (physical pixels).</summary>
    internal int Thickness => (int)Math.Round(ThicknessDips * DpiHelper.GetScale(this).ScaleX);

    public void ApplyTheme(AppTheme theme)
    {
        Theming.Apply(this, theme);
        _visual?.InvalidateVisual();
    }

    /// <summary>Puts it at <paramref name="origin"/> with its current length and direction, and remembers that.</summary>
    public void Place(PixelPoint origin)
    {
        Origin = origin;
        var scale = DpiHelper.GetScale(this);
        _canvas.LayoutTransform = DpiHelper.PhysicalPixelTransform(scale);
        var (width, height) = IsVertical ? (Thickness, Length) : (Length, Thickness);
        _visual.Width = width;
        _visual.Height = height;
        ToolWindowStyle.Place(this, new PixelRect(origin.X, origin.Y, width, height));
        _visual.InvalidateVisual();
        var placement = new RulerPlacement(origin.X, origin.Y, Length, IsVertical, Unit);
        _ = _state.UpdateAsync(s => s with { Ruler = placement });
    }

    public void Turn()
    {
        IsVertical = !IsVertical;
        Place(Origin ?? DefaultOrigin());
    }

    public void SetUnit(RulerUnit unit)
    {
        Unit = unit;
        Place(Origin ?? DefaultOrigin());
    }

    public void SetLength(int length)
    {
        Length = Math.Max(MinLength, length);
        Place(Origin ?? DefaultOrigin());
    }

    protected override void OnDpiChanged(WpfDpiScale oldDpi, WpfDpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (Origin is { } origin && _drag == DragKind.None)
        {
            Dispatcher.BeginInvoke(() => Place(origin), DispatcherPriority.Loaded); // the same pixels on the new monitor
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ClickCount == 2)
        {
            Turn();
            e.Handled = true;
            return;
        }

        var scale = DpiHelper.GetScale(this);
        var at = e.GetPosition(this);
        _grab = new PixelPoint((int)Math.Round(at.X * scale.ScaleX), (int)Math.Round(at.Y * scale.ScaleY));
        var along = IsVertical ? _grab.Y : _grab.X;
        _drag = along >= Length - EndGrip ? DragKind.Resize : DragKind.Move;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var cursor = ToolWindowStyle.Cursor();
        if (_drag == DragKind.Move)
        {
            var origin = new PixelPoint(cursor.X - _grab.X, cursor.Y - _grab.Y);
            Origin = origin;
            ToolWindowStyle.Place(this, new PixelRect(origin.X, origin.Y, IsVertical ? Thickness : Length, IsVertical ? Length : Thickness));
        }
        else if (_drag == DragKind.Resize && Origin is { } o)
        {
            Length = Math.Max(MinLength, (IsVertical ? cursor.Y - o.Y : cursor.X - o.X) + (EndGrip / 2));
            Place(o);
        }
        else
        {
            var at = e.GetPosition(this);
            var scale = DpiHelper.GetScale(this);
            var along = (IsVertical ? at.Y * scale.ScaleY : at.X * scale.ScaleX);
            Cursor = along >= Length - EndGrip ? (IsVertical ? Cursors.SizeNS : Cursors.SizeWE) : Cursors.SizeAll;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_drag != DragKind.None)
        {
            _drag = DragKind.None;
            ReleaseMouseCapture();
            Place(Origin ?? DefaultOrigin());
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 10 : 1;
        var resize = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var origin = Origin ?? DefaultOrigin();
        var (dx, dy) = e.Key switch
        {
            Key.Left => (-step, 0),
            Key.Right => (step, 0),
            Key.Up => (0, -step),
            Key.Down => (0, step),
            _ => (0, 0),
        };

        if (e.Key == Key.Escape)
        {
            Close();
        }
        else if ((dx, dy) == (0, 0))
        {
            return;
        }
        else if (resize)
        {
            SetLength(Length + (IsVertical ? dy : dx));
        }
        else
        {
            Place(origin.Offset(dx, dy));
        }

        e.Handled = true;
    }

    private void FollowPointer()
    {
        if (Origin is not { } origin)
        {
            return;
        }

        var cursor = ToolWindowStyle.Cursor();
        var along = IsVertical ? cursor.Y - origin.Y : cursor.X - origin.X;
        double? pointer = along >= 0 && along <= Length ? along : null;
        if (pointer != Pointer)
        {
            Pointer = pointer;
            _visual.InvalidateVisual();
        }
    }

    private MonitorInfo Monitor()
    {
        var monitors = _monitors.GetMonitors();
        var at = Origin ?? DefaultOrigin();
        return monitors.FirstOrDefault(m => m.Bounds.Contains(at)) ?? monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
    }

    /// <summary>A third of the way down the primary monitor's work area, centred.</summary>
    private PixelPoint DefaultOrigin()
    {
        var monitors = _monitors.GetMonitors();
        var area = (monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0]).WorkArea;
        return new PixelPoint(area.X + Math.Max(0, (area.Width - Length) / 2), area.Y + (area.Height / 3));
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        var across = new MenuItem { Header = "_Across" };
        across.Click += (_, _) => { if (IsVertical) Turn(); };
        var down = new MenuItem { Header = "_Down" };
        down.Click += (_, _) => { if (!IsVertical) Turn(); };
        menu.Items.Add(across);
        menu.Items.Add(down);
        menu.Items.Add(new Separator());
        foreach (var unit in Enum.GetValues<RulerUnit>())
        {
            var item = new MenuItem { Header = unit switch { RulerUnit.Pixels => "_Pixels", RulerUnit.Inches => "_Inches", _ => "_Centimetres" } };
            item.Click += (_, _) => SetUnit(unit);
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        var close = new MenuItem { Header = "C_lose", InputGestureText = "Esc" };
        close.Click += (_, _) => Close();
        menu.Items.Add(close);
        menu.Opened += (_, _) =>
        {
            across.IsChecked = !IsVertical;
            down.IsChecked = IsVertical;
            foreach (var (item, unit) in menu.Items.OfType<MenuItem>().Skip(2).Take(3).Zip(Enum.GetValues<RulerUnit>()))
            {
                item.IsChecked = unit == Unit;
            }
        };
        return menu;
    }

    /// <summary>Draws the ruler on its physical canvas: one unit is one screen pixel.</summary>
    private sealed class RulerVisual(RulerWindow ruler) : FrameworkElement
    {
        public static readonly Brush Body = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xF2, 0xB3)));
        private static readonly Brush Ink = Frozen(new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30)));
        private static readonly Brush Marker = Frozen(new SolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x23)));
        private static readonly Brush Readout = Frozen(new SolidColorBrush(Color.FromArgb(0xF0, 0x1C, 0x1C, 0x1C)));
        private static readonly Typeface Face = new("Segoe UI");

        protected override void OnRender(DrawingContext dc)
        {
            var length = ruler.Length;
            var thickness = ruler.Thickness;
            var vertical = ruler.IsVertical;
            var scale = DpiHelper.GetScale(this);
            var dpi = ruler.Dpi;
            var (w, h) = vertical ? (thickness, length) : (length, thickness);
            dc.DrawRectangle(Body, new Pen(Ink, 1), new Rect(0.5, 0.5, w - 1, h - 1));

            foreach (var tick in RulerScale.Ticks(length, ruler.Unit, dpi))
            {
                var size = thickness * tick.Level switch { 0 => 0.42, 1 => 0.28, _ => 0.16 };
                var at = Math.Floor(tick.Position) + 0.5;
                if (vertical)
                {
                    dc.DrawLine(new Pen(Ink, 1), new Point(0, at), new Point(size, at));
                }
                else
                {
                    dc.DrawLine(new Pen(Ink, 1), new Point(at, 0), new Point(at, size));
                }

                if (tick.Label is { } label)
                {
                    var text = Text(label, 10, scale, Ink);
                    if (at + 2 + (vertical ? text.Height : text.Width) > length - EndGrip)
                    {
                        continue; // would run into the grip at the far end
                    }

                    var origin = vertical
                        ? new Point(size + 2, Math.Min(at + 1, length - text.Height - 1))
                        : new Point(Math.Min(at + 2, length - text.Width - 2), size - 1);
                    dc.DrawText(text, origin);
                }
            }

            var unitText = Text($"{UnitName(ruler.Unit)}{(ruler.Unit == RulerUnit.Pixels ? string.Empty : string.Create(CultureInfo.InvariantCulture, $" at {dpi:0} dpi"))}", 9, scale, Ink);
            dc.DrawText(unitText, vertical ? new Point(thickness - unitText.Width - 3, length - unitText.Height - 16) : new Point(length - unitText.Width - 18, thickness - unitText.Height - 2));

            if (ruler.Pointer is { } pointer)
            {
                var at = pointer + 0.5;
                var readout = Text(RulerScale.Format(pointer, ruler.Unit, dpi), 11, scale, Brushes.White);
                if (vertical)
                {
                    dc.DrawLine(new Pen(Marker, 1), new Point(0, at), new Point(thickness, at));
                    var y = Math.Clamp(at - readout.Height - 2, 0, length - readout.Height - 4);
                    dc.DrawRectangle(Readout, null, new Rect(thickness - readout.Width - 6, y, readout.Width + 4, readout.Height + 2));
                    dc.DrawText(readout, new Point(thickness - readout.Width - 4, y + 1));
                }
                else
                {
                    dc.DrawLine(new Pen(Marker, 1), new Point(at, 0), new Point(at, thickness));
                    var x = Math.Clamp(at + 3, 0, length - readout.Width - 6);
                    dc.DrawRectangle(Readout, null, new Rect(x, thickness - readout.Height - 4, readout.Width + 4, readout.Height + 2));
                    dc.DrawText(readout, new Point(x + 2, thickness - readout.Height - 3));
                }
            }

            // The far end's grip: two short lines across.
            var end = length - (EndGrip / 2.0);
            for (var i = -1; i <= 1; i += 2)
            {
                var p = end + (i * 2) + 0.5;
                if (vertical)
                {
                    dc.DrawLine(new Pen(Ink, 1), new Point(thickness * 0.35, p), new Point(thickness * 0.65, p));
                }
                else
                {
                    dc.DrawLine(new Pen(Ink, 1), new Point(p, thickness * 0.35), new Point(p, thickness * 0.65));
                }
            }
        }

        private static string UnitName(RulerUnit unit) => unit switch
        {
            RulerUnit.Pixels => "px",
            RulerUnit.Inches => "in",
            _ => "cm",
        };

        private FormattedText Text(string text, double dips, Core.Geometry.DpiScale scale, Brush brush) =>
            new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, DpiHelper.DipsToPhysical(dips, scale), brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

        private static T Frozen<T>(T freezable)
            where T : Freezable
        {
            freezable.Freeze();
            return freezable;
        }
    }
}
