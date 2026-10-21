using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CashGear.Capture.App.Wpf;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;

namespace CashGear.Capture.App.Tools;

/// <summary>
/// The crosshair: a line across and a line down every monitor through the pointer, and a small readout of where it
/// is — on the virtual screen and on its monitor, with the monitor's scale. It follows the pointer every frame, lets
/// every click through (the apps underneath work as usual), never takes the focus, and is left out of captures.
/// The hotkey, the tray or the toolbar turns it off.
/// </summary>
public sealed class Crosshair
{
    private static readonly Brush LineBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x23)));

    private readonly IMonitorService _monitors;
    private readonly ToolOverlay _across = new(LineBrush, clickThrough: true);
    private readonly ToolOverlay _down = new(LineBrush, clickThrough: true);
    private readonly ToolOverlay _readout = new(Frozen(new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C))), clickThrough: false); // never under the pointer
    private readonly TextBlock _text = new() { Foreground = Brushes.White, FontFamily = new FontFamily("Segoe UI"), FontSize = 12, Padding = new Thickness(6, 3, 6, 4) };
    private PixelPoint? _last;
    private bool _closed;

    private Crosshair(IMonitorService monitors)
    {
        _monitors = monitors;
        _readout.Content = _text;
        _readout.SizeToContent = SizeToContent.WidthAndHeight;
    }

    /// <summary>Where the lines cross now.</summary>
    public PixelPoint? At => _last;

    /// <summary>What the readout says.</summary>
    public string Readout => _text.Text;

    internal Window Across => _across;

    internal Window Down => _down;

    internal Window Label => _readout;

    public static Crosshair Show(IMonitorService monitors)
    {
        var crosshair = new Crosshair(monitors);
        crosshair._across.Show();
        crosshair._down.Show();
        crosshair._readout.Show();
        CompositionTarget.Rendering += crosshair.OnRendering;
        crosshair.Update();
        return crosshair;
    }

    public void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        CompositionTarget.Rendering -= OnRendering;
        _across.Close();
        _down.Close();
        _readout.Close();
    }

    private void OnRendering(object? sender, EventArgs e) => Update();

    private void Update()
    {
        var cursor = ToolWindowStyle.Cursor();
        if (_closed || cursor == _last)
        {
            return;
        }

        _last = cursor;
        var monitors = _monitors.GetMonitors();
        var screen = VirtualScreen.GetBounds(monitors.Select(m => m.Bounds));
        var monitor = monitors.FirstOrDefault(m => m.Bounds.Contains(cursor)) ?? monitors[0];
        _across.PlaceAt(new PixelRect(screen.X, cursor.Y, screen.Width, 1));
        _down.PlaceAt(new PixelRect(cursor.X, screen.Y, 1, screen.Height));

        var local = VirtualScreen.ToLocal(cursor, monitor.Bounds);
        _text.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"{cursor.X}, {cursor.Y}\n{monitor.ShortName} {monitor.Scale.ScaleX:P0}: {local.X}, {local.Y}");
        _readout.UpdateLayout();
        var scale = DpiHelper.GetScale(_readout);
        var size = new PixelSize((int)Math.Ceiling(_readout.ActualWidth * scale.ScaleX), (int)Math.Ceiling(_readout.ActualHeight * scale.ScaleY));
        var gap = (int)Math.Round(16 * monitor.Scale.ScaleX);

        // Below right of the pointer, flipped to stay on its monitor.
        var x = cursor.X + gap + size.Width > monitor.Bounds.Right ? cursor.X - gap - size.Width : cursor.X + gap;
        var y = cursor.Y + gap + size.Height > monitor.Bounds.Bottom ? cursor.Y - gap - size.Height : cursor.Y + gap;
        _readout.PlaceAt(new PixelRect(x, y, size.Width, size.Height));
    }

    private static T Frozen<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    /// <summary>A borderless, top-most window of the crosshair, placed in physical pixels.</summary>
    private sealed class ToolOverlay : Window, IScreenToolWindow
    {
        private PixelRect _bounds;

        public ToolOverlay(Brush background, bool clickThrough)
        {
            Title = "CashGear Capture - crosshair";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Background = background;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Focusable = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -32000;
            Top = -32000;
            Width = 1;
            Height = 1;
            SourceInitialized += (_, _) => ToolWindowStyle.Apply(this, clickThrough, popup: true);
        }

        public void PlaceAt(PixelRect bounds)
        {
            _bounds = bounds;
            ToolWindowStyle.Place(this, bounds);
        }

        /// <summary>
        /// A line spanning monitors changes DPI when most of it moves onto a monitor of another scale, and WPF then
        /// scales the window (a 1-px line became 2 × 1.5 times as long): put it back.
        /// </summary>
        protected override void OnDpiChanged(System.Windows.DpiScale oldDpi, System.Windows.DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            Dispatcher.BeginInvoke(() => ToolWindowStyle.Place(this, _bounds), System.Windows.Threading.DispatcherPriority.Send);
        }
    }
}
