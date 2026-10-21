using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CashGear.Capture.App.Wpf;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Selection;
using CashGear.Capture.Interop.Native;
using Microsoft.Extensions.Logging;
using WpfDpiScale = System.Windows.DpiScale;

namespace CashGear.Capture.App.Region;

/// <summary>
/// A borderless, opaque, topmost window spanning the whole virtual screen and showing the frozen screenshot.
/// <list type="bullet">
/// <item>One window, not one per monitor: once activated it counts as full-screen on every monitor, so the shell
/// lowers every taskbar. A non-active topmost window is kept under the taskbar of its monitor.</item>
/// <item>Pixel-exact at any DPI mix: content lives in a physical canvas (one unit = one device pixel), and
/// per-monitor-v2 windows are never rescaled by DWM, whichever monitor's DPI the window reports.</item>
/// <item>Opaque, not layered (AllowsTransparency): the background is the screenshot itself. That keeps it fast
/// and lets display affinity exclude it from capture.</item>
/// </list>
/// Only low-level input handling lives here; the selection logic is the <see cref="ISelectionModel"/> (one per mode).
/// </summary>
internal sealed partial class RegionOverlayWindow : Window
{
    private const int WheelNotch = 120;

    private readonly PixelRect _virtual;
    private readonly ISelectionModel _model;
    private readonly Grid _canvas;
    private readonly RegionOverlayVisual _visual;
    private readonly ILogger _logger;
    private nint _hwnd;
    private int _wheel; // part of a notch left over (high-resolution wheels)

    /// <param name="background">The frozen virtual-screen image, exactly <paramref name="virtualBounds"/> in pixels.</param>
    public RegionOverlayWindow(
        PixelRect virtualBounds,
        IReadOnlyList<MonitorInfo> monitors,
        BitmapSource background,
        ISelectionModel model,
        Func<PixelPoint, uint?> colorAt,
        ILogger logger)
    {
        _virtual = virtualBounds;
        _model = model;
        _logger = logger;

        Title = "CashGear Capture - select a region";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false; // the selector activates it explicitly once shown
        Topmost = true;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;
        Focusable = true;
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.None); // Tab is the fixed-size frame's key

        // Approximate placement in DIPs; OnSourceInitialized moves the HWND onto the exact physical rectangle.
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.Manual;
        Left = virtualBounds.X;
        Top = virtualBounds.Y;
        Width = virtualBounds.Width;
        Height = virtualBounds.Height;

        var image = new Image
        {
            Source = background,
            Width = virtualBounds.Width,
            Height = virtualBounds.Height,
            Stretch = Stretch.Fill,
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);

        _visual = new RegionOverlayVisual(virtualBounds, monitors, background, model, colorAt);

        // The physical canvas: one unit = one device pixel once the DPI transform is applied.
        _canvas = new Grid
        {
            Width = virtualBounds.Width,
            Height = virtualBounds.Height,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            ClipToBounds = true,
        };
        _canvas.Children.Add(image);
        _canvas.Children.Add(_visual);
        Content = _canvas;

        _model.Changed += OnModelChanged;
    }

    /// <summary>The element whose coordinate space is physical pixels relative to the virtual screen (for tests).</summary>
    internal FrameworkElement PhysicalCanvas => _canvas;

    internal nint Handle => _hwnd;

    /// <summary>Hide the overlay from screen capture (on by default; tests switch it off to grab what it shows).</summary>
    internal bool ExcludeFromCapture { get; init; } = true;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        PlaceOnVirtualScreen();
        ApplyScale(DpiHelper.GetScale(this));

        if (ExcludeFromCapture && !User32.SetWindowDisplayAffinity(_hwnd, User32.WdaExcludeFromCapture))
        {
            LogAffinityFailed(Marshal.GetLastPInvokeError());
        }
    }

    protected override void OnDpiChanged(WpfDpiScale oldDpi, WpfDpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ApplyScale(new Core.Geometry.DpiScale(newDpi.DpiScaleX, newDpi.DpiScaleY));

        // WPF has resized the window to the system-suggested rectangle; put it back exactly over the virtual screen.
        Dispatcher.BeginInvoke(PlaceOnVirtualScreen, DispatcherPriority.Send);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (Hosting.Timings.SinceHotkey() is { } since)
        {
            LogShownAfterHotkey(since.TotalMilliseconds, System.Diagnostics.Stopwatch.GetTimestamp(), Hosting.Timings.StepsSoFar());
        }

        VerifyPlacement();
    }

    protected override void OnClosed(EventArgs e)
    {
        _model.Changed -= OnModelChanged;
        base.OnClosed(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _model.PointerMoved(ToVirtual(e.GetPosition(_canvas)));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var point = ToVirtual(e.GetPosition(_canvas));
        LogPointer("press", point, e.ClickCount);
        if (e.ClickCount >= 2)
        {
            _model.DoubleClicked(point);
        }
        else
        {
            CaptureMouse();
            _model.PointerPressed(point);
        }

        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        var point = ToVirtual(e.GetPosition(_canvas));
        LogPointer("release", point, e.ClickCount);
        ReleaseMouseCapture();
        _model.PointerReleased(point);
        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        _model.Cancel();
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        _wheel += e.Delta;
        var notches = _wheel / WheelNotch;
        if (notches != 0)
        {
            _wheel -= notches * WheelNotch;
            _model.Wheel(notches);
        }

        e.Handled = true;
    }

    /// <summary>Esc cancels; Enter, Backspace, Tab and the arrows go to the model (Tab is caught early, before focus navigation).</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            _model.Cancel();
            e.Handled = true;
            return;
        }

        SelectionKey? selectionKey = key switch
        {
            Key.Enter => SelectionKey.Enter,
            Key.Back => SelectionKey.Backspace,
            Key.Tab => SelectionKey.Tab,
            Key.Left => SelectionKey.Left,
            Key.Right => SelectionKey.Right,
            Key.Up => SelectionKey.Up,
            Key.Down => SelectionKey.Down,
            _ => null,
        };
        if (selectionKey is not { } pressed)
        {
            return;
        }

        var modifiers = (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? SelectionModifiers.Shift : SelectionModifiers.None)
            | (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? SelectionModifiers.Control : SelectionModifiers.None);
        if (!_model.KeyPressed(pressed, modifiers) && pressed is SelectionKey.Left or SelectionKey.Right or SelectionKey.Up or SelectionKey.Down)
        {
            MovePointer(pressed, modifiers);
        }

        e.Handled = true;
    }

    /// <summary>
    /// Arrows the model doesn't use move the mouse cursor by a pixel (Ctrl: 10) for precise placement; the system
    /// turns the move into a mouse-move message, which updates the model.
    /// </summary>
    private void MovePointer(SelectionKey key, SelectionModifiers modifiers)
    {
        var step = modifiers.HasFlag(SelectionModifiers.Control) ? 10 : 1;
        var (dx, dy) = key switch
        {
            SelectionKey.Left => (-step, 0),
            SelectionKey.Right => (step, 0),
            SelectionKey.Up => (0, -step),
            _ => (0, step),
        };
        var cursor = _model.Cursor;
        User32.SetCursorPos(cursor.X + dx, cursor.Y + dy);
    }

    private PixelPoint ToVirtual(Point canvasPoint) => DpiHelper.ToPixel(canvasPoint).Offset(_virtual.X, _virtual.Y);

    private void ApplyScale(Core.Geometry.DpiScale scale) => _canvas.LayoutTransform = DpiHelper.PhysicalPixelTransform(scale);

    private void PlaceOnVirtualScreen()
    {
        if (_hwnd == 0)
        {
            return;
        }

        var b = _virtual;
        if (!User32.SetWindowPos(_hwnd, User32.HwndTopmost, b.X, b.Y, b.Width, b.Height, User32.SwpNoActivate | User32.SwpNoOwnerZOrder))
        {
            LogPlacementFailed(Marshal.GetLastPInvokeError());
        }
    }

    private unsafe void VerifyPlacement()
    {
        RECT rect;
        if (!User32.GetWindowRect(_hwnd, &rect))
        {
            return;
        }

        var actual = PixelRect.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        if (actual != _virtual)
        {
            LogMisplaced(actual, _virtual);
            PlaceOnVirtualScreen();
        }
    }

    private void OnModelChanged(object? sender, EventArgs e) => _visual.InvalidateVisual();

    [LoggerMessage(Level = LogLevel.Information, Message = "Overlay shown {SinceHotkeyMs:0.0} ms after its hotkey (QPC {ShownQpc}): {Steps}")]
    private partial void LogShownAfterHotkey(double sinceHotkeyMs, long shownQpc, string steps);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Overlay {Action} at {Point} (clicks {Clicks})")]
    private partial void LogPointer(string action, PixelPoint point, int clicks);

    [LoggerMessage(Level = LogLevel.Debug, Message = "SetWindowDisplayAffinity on the overlay failed (Win32 error {Error})")]
    private partial void LogAffinityFailed(int error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SetWindowPos for the overlay failed (Win32 error {Error})")]
    private partial void LogPlacementFailed(int error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Overlay was at {Actual} instead of {Expected}; moved it")]
    private partial void LogMisplaced(PixelRect actual, PixelRect expected);
}
