using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using CashGear.Capture.App.Wpf;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Engine.Capture.Wgc;
using CashGear.Capture.Interop.Native;

namespace CashGear.Capture.App.Tools;

/// <summary>
/// The screen magnifier: a live lens centred on the pointer, showing the screen at 2× to 8× with the centre pixel
/// marked and its position and colour.
/// <list type="bullet">
/// <item>While it is open it has the mouse, as in FastStone: a low-level mouse hook takes the wheel (zoom) and the
/// buttons (a click closes it) before any window gets them; the pointer still moves freely. It has the keyboard
/// too: + and − zoom, arrows move the pointer a pixel (Ctrl: 10), Esc closes it.</item>
/// <item>Every frame it reads the square under the pointer with GDI (Windows.Graphics.Capture would show its yellow
/// border on Windows 10) and shows it at a whole zoom, pixel for pixel, on the monitor's scale. On a monitor in HDR
/// mode GDI's copy is washed out, so there it reads a live Windows.Graphics.Capture stream of that monitor, tone-mapped
/// to SDR (with the yellow border on Windows 10), as long as the square is on it. The lens is an
/// opaque window left out of captures, so it never sees itself and the pixels it reads are exact. (A see-through
/// window over the screen to take the mouse would not do: while one is up, Windows composes the screen with some
/// pixels a level off, even in captures that leave it out.)</item>
/// </list>
/// One at a time; UI thread.
/// </summary>
public sealed unsafe class ScreenMagnifier
{
    /// <summary>The lens's side at 100% scale (DIPs).</summary>
    public const int LensDips = 240;

    private static ScreenMagnifier? _active; // the one the mouse hook serves

    private readonly IMonitorService _monitors;
    private readonly IAppStateStore _state;
    private readonly LensWindow _lens;
    private readonly ScreenSampler _sampler = new();
    private readonly ILiveMonitorCapture? _live;
    private ILiveMonitorStream? _hdrStream;
    private nint _hook;
    private int _wheel;
    private bool _closed;

    private ScreenMagnifier(IMonitorService monitors, IAppStateStore state, ILiveMonitorCapture? live)
    {
        _monitors = monitors;
        _live = live;
        _state = state;
        Zoom = Magnifier.ClampZoom(state.Current.MagnifierZoom);
        _lens = new LensWindow(this);
    }

    /// <summary>Raised once when it closes.</summary>
    public event EventHandler? Closed;

    public int Zoom { get; private set; }

    /// <summary>The screen pixels shown last (virtual-screen pixels).</summary>
    public PixelRect Source { get; private set; }

    /// <summary>Where the lens is (physical pixels).</summary>
    public PixelRect LensBounds { get; private set; }

    /// <summary>The pixels shown last, before zooming (BGRA, for tests).</summary>
    public WriteableBitmap? Pixels { get; private set; }

    public bool IsOpen => !_closed;

    /// <summary>Opens it (UI thread). It closes itself on a click or Esc.</summary>
    /// <param name="live">Reads monitors in HDR mode (null: GDI everywhere).</param>
    public static ScreenMagnifier Open(IMonitorService monitors, IAppStateStore state, ILiveMonitorCapture? live = null)
    {
        _active?.Close();
        var magnifier = new ScreenMagnifier(monitors, state, live);
        magnifier.Start();
        return magnifier;
    }

    public void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        CompositionTarget.Rendering -= OnRendering;
        if (_hook != 0)
        {
            User32.UnhookWindowsHookEx(_hook);
            _hook = 0;
        }

        if (ReferenceEquals(_active, this))
        {
            _active = null;
        }

        _lens.Close();
        _sampler.Dispose();
        _hdrStream?.Dispose();
        _hdrStream = null;
        var zoom = Zoom;
        _ = _state.UpdateAsync(s => s with { MagnifierZoom = zoom });
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Changes the zoom by <paramref name="steps"/> (kept within 2× to 8×).</summary>
    public void ZoomBy(int steps) => Zoom = Magnifier.ClampZoom(Zoom + steps);

    private void Start()
    {
        _active = this;
        delegate* unmanaged[Stdcall]<int, nint, nint, nint> procedure = &MouseProc;
        _hook = User32.SetWindowsHookExW(User32.WhMouseLl, procedure, Kernel32.GetModuleHandleW(null), 0);
        _lens.Show();
        _lens.Activate(); // for the keys
        CompositionTarget.Rendering += OnRendering;
        Update();
    }

    private void OnRendering(object? sender, EventArgs e) => Update();

    private void Update()
    {
        if (_closed)
        {
            return;
        }

        var cursor = ToolWindowStyle.Cursor();
        var monitors = _monitors.GetMonitors();
        var monitor = monitors.FirstOrDefault(m => m.Bounds.Contains(cursor)) ?? monitors[0];
        var size = (int)Math.Round(LensDips * monitor.Scale.ScaleX);
        var source = Magnifier.Source(cursor, size, Zoom);
        var pixels = SampleHdr(monitor, source) is { IsEmpty: false } hdr ? hdr : _sampler.Sample(source);
        if (pixels.IsEmpty)
        {
            return;
        }

        if (Pixels is null || Pixels.PixelWidth != source.Width)
        {
            Pixels = new WriteableBitmap(source.Width, source.Height, 96, 96, PixelFormats.Bgra32, null);
        }

        fixed (byte* p = pixels)
        {
            Pixels.WritePixels(new Int32Rect(0, 0, source.Width, source.Height), (nint)p, pixels.Length, source.Width * 4);
        }

        Source = source;
        LensBounds = Magnifier.Lens(cursor, size, monitor.Bounds);
        var centre = pixels.Slice(((source.Height / 2 * source.Width) + (source.Width / 2)) * 4, 4);
        _lens.Present(Pixels, source, Zoom, size, cursor, (centre[2], centre[1], centre[0]));
        ToolWindowStyle.Place(_lens, LensBounds);
    }

    /// <summary>The square from the live stream of <paramref name="monitor"/> when it is in HDR mode and holds the square; else empty (GDI reads it).</summary>
    private ReadOnlySpan<byte> SampleHdr(MonitorInfo monitor, PixelRect source)
    {
        if (_live is null || monitor.Hdr is not { } hdr || !monitor.Bounds.Contains(source))
        {
            return [];
        }

        if (_hdrStream is null || _hdrStream.Monitor.DeviceName != monitor.DeviceName || _hdrStream.Monitor.Bounds != monitor.Bounds)
        {
            _hdrStream?.Dispose();
            _hdrStream = _live.Open(monitor, hdr);
        }

        return _hdrStream is null ? [] : _hdrStream.Sample(source);
    }

    private void OnWheel(int delta)
    {
        _wheel += delta;
        var notches = _wheel / 120;
        _wheel -= notches * 120;
        ZoomBy(notches);
    }

    /// <summary>
    /// The mouse while the magnifier is open (on the UI thread, which installed the hook): the wheel zooms, a button
    /// closes it; neither reaches any window. Kept short, as Windows skips a hook that keeps it waiting.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint MouseProc(int code, nint wParam, nint lParam)
    {
        if (code == User32.HcAction && _active is { } magnifier)
        {
            switch ((uint)wParam)
            {
                case User32.WmMouseWheel:
                    magnifier.OnWheel((short)(((MSLLHOOKSTRUCT*)lParam)->mouseData >> 16));
                    return 1;
                case User32.WmLButtonDown or User32.WmRButtonDown or User32.WmMButtonDown:
                    magnifier._lens.Dispatcher.BeginInvoke(magnifier.Close);
                    return 1;
                case User32.WmLButtonUp or User32.WmRButtonUp or User32.WmMButtonUp:
                    return 1; // its press was taken too
            }
        }

        return User32.CallNextHookEx(0, code, wParam, lParam);
    }

    /// <summary>The lens: the zoomed pixels on a physical canvas, the centre pixel's frame, and its position and colour.</summary>
    private sealed class LensWindow : Window, IScreenToolWindow
    {
        private static readonly Brush Panel = Frozen(new SolidColorBrush(Color.FromArgb(0xE6, 0x1C, 0x1C, 0x1C)));

        private readonly ScreenMagnifier _owner;
        private readonly Canvas _canvas = new() { ClipToBounds = true };
        private readonly Image _image = new();
        private readonly Rectangle _marker = new() { Stroke = Brushes.White, StrokeThickness = 1 };
        private readonly Rectangle _markerShadow = new() { Stroke = Brushes.Black, StrokeThickness = 1 };
        private readonly Border _frame = new() { BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x90, 0xFF)), BorderThickness = new Thickness(2) };
        private readonly TextBlock _info = new() { Foreground = Brushes.White, Padding = new Thickness(6, 2, 6, 3), FontFamily = new FontFamily("Segoe UI"), FontSize = 12 };

        public LensWindow(ScreenMagnifier owner)
        {
            _owner = owner;
            Title = "CashGear Capture - magnifier";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Background = Brushes.Black;
            ShowInTaskbar = false;
            Topmost = true;
            Cursor = Cursors.None; // the lens marks the pixel under the pointer
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -32000;
            Top = -32000;
            Width = 10;
            Height = 10;
            RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.NearestNeighbor);
            _canvas.Children.Add(_image);
            _canvas.Children.Add(_markerShadow);
            _canvas.Children.Add(_marker);
            var root = new Grid();
            root.Children.Add(_canvas);
            root.Children.Add(_frame);
            root.Children.Add(new Border
            {
                Background = Panel,
                Child = _info,
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Left,
            });
            Content = root;
            SourceInitialized += (_, _) => ToolWindowStyle.Apply(this, clickThrough: false, popup: true);
        }

        /// <summary>Shows <paramref name="pixels"/> (the <paramref name="source"/> square) at <paramref name="zoom"/>, cropped to <paramref name="size"/> pixels.</summary>
        public void Present(WriteableBitmap pixels, PixelRect source, int zoom, int size, PixelPoint cursor, (byte R, byte G, byte B) centre)
        {
            _canvas.LayoutTransform = DpiHelper.PhysicalPixelTransform(DpiHelper.GetScale(this));
            _canvas.Width = size;
            _canvas.Height = size;
            var shown = source.Width * zoom;
            var offset = (shown - size) / 2.0;
            _image.Source = pixels;
            _image.Width = shown;
            _image.Height = shown;
            Canvas.SetLeft(_image, -offset);
            Canvas.SetTop(_image, -offset);

            var middle = (source.Width / 2 * zoom) - offset;
            Place(_marker, middle - 1, zoom + 2);
            Place(_markerShadow, middle - 2, zoom + 4);
            _info.Text = string.Create(CultureInfo.InvariantCulture, $"{cursor.X}, {cursor.Y}   #{centre.R:X2}{centre.G:X2}{centre.B:X2}   {zoom}×");
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);
            var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 10 : 1;
            var cursor = ToolWindowStyle.Cursor();
            switch (e.Key)
            {
                case Key.Escape or Key.Enter or Key.Space:
                    _owner.Close();
                    break;
                case Key.Add or Key.OemPlus or Key.PageUp:
                    _owner.ZoomBy(1);
                    break;
                case Key.Subtract or Key.OemMinus or Key.PageDown:
                    _owner.ZoomBy(-1);
                    break;
                case Key.Left:
                    User32.SetCursorPos(cursor.X - step, cursor.Y);
                    break;
                case Key.Right:
                    User32.SetCursorPos(cursor.X + step, cursor.Y);
                    break;
                case Key.Up:
                    User32.SetCursorPos(cursor.X, cursor.Y - step);
                    break;
                case Key.Down:
                    User32.SetCursorPos(cursor.X, cursor.Y + step);
                    break;
                default:
                    return;
            }

            e.Handled = true;
        }

        protected override void OnDeactivated(EventArgs e)
        {
            base.OnDeactivated(e);
            _owner.Close(); // another window came to the front (Alt+Tab, the Win key): let go of the mouse
        }

        private static void Place(Rectangle rectangle, double at, double side)
        {
            rectangle.Width = side;
            rectangle.Height = side;
            Canvas.SetLeft(rectangle, at);
            Canvas.SetTop(rectangle, at);
        }

        private static T Frozen<T>(T freezable)
            where T : Freezable
        {
            freezable.Freeze();
            return freezable;
        }
    }
}
