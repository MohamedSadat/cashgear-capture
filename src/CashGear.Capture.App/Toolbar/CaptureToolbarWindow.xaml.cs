using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CashGear.Capture.App.Platform;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Interop.Native;
using WpfDpiScale = System.Windows.DpiScale;

namespace CashGear.Capture.App.Toolbar;

/// <summary>
/// The floating capture toolbar, like FastStone's capture panel: a button per capture mode, repeat, record, the
/// delay, what happens after a capture, settings.
/// <list type="bullet">
/// <item>Always on top, never takes the focus (so "active window" is still the user's window, and a menu stays
/// open), and is left out of every capture and recording.</item>
/// <item>Dragged by its grip; dropped near an edge of a monitor's work area it docks flush against it, in a column
/// on the left and right edges and in a row at the top and bottom. Where it was is remembered.</item>
/// <item>When a column (or row) is longer than the monitor's work area (20 buttons at 150% on a 1080p screen), it
/// wraps into a second one.</item>
/// </list>
/// Positions are physical pixels (<c>SetWindowPos</c>), so it lands exactly at any mix of monitor scales.
/// </summary>
public partial class CaptureToolbarWindow : Window, IThemedWindow
{
    private const string GripVertical = "";
    private const string GripHorizontal = "";

    private readonly IAppStateStore _state;
    private readonly IMonitorService _monitors;
    private readonly ToolbarPlacement? _placement;
    private nint _hwnd;
    private bool _dragging;
    private PixelPoint _grab; // where in the window (physical pixels) the grip was pressed

    /// <param name="viewModel">A <see cref="CaptureToolbarViewModel"/> (tests give any object).</param>
    public CaptureToolbarWindow(object viewModel, AppTheme theme, IAppStateStore state, IMonitorService monitors)
    {
        InitializeComponent();
        DataContext = viewModel;
        _state = state;
        _monitors = monitors;
        _placement = state.Current.Toolbar;
        Compact();
        SetVertical(_placement?.Vertical ?? false);
        Edge = _placement?.Edge ?? DockEdge.Top;
        ApplyTheme(theme);

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            var exStyle = (long)User32.GetWindowLongPtrW(_hwnd, User32.GwlExStyle);
            User32.SetWindowLongPtrW(_hwnd, User32.GwlExStyle, (nint)(exStyle | User32.WsExNoActivate | User32.WsExToolWindow));
            User32.SetWindowDisplayAffinity(_hwnd, User32.WdaExcludeFromCapture);
        };
        Loaded += (_, _) => Dispatcher.BeginInvoke(Restore, DispatcherPriority.Loaded);
    }

    /// <summary>The edge it is docked to.</summary>
    public DockEdge Edge { get; private set; }

    /// <summary>Its buttons are in a column.</summary>
    public bool IsVertical { get; private set; }

    internal nint Handle => _hwnd;

    internal FrameworkElement GripElement => Grip;

    public void ApplyTheme(AppTheme theme) => Theming.Apply(this, theme);

    /// <summary>Where it is on the screen (physical pixels).</summary>
    internal unsafe PixelRect Bounds()
    {
        RECT rect;
        return _hwnd != 0 && User32.GetWindowRect(_hwnd, &rect) ? PixelRect.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom) : PixelRect.Empty;
    }

    protected override void OnDpiChanged(WpfDpiScale oldDpi, WpfDpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (!_dragging)
        {
            // On another monitor's scale it has another size: fit it to that monitor and put it flush against its edge again.
            var at = Bounds().TopLeft;
            FitTo(MonitorOf(Bounds()));
            Dispatcher.BeginInvoke(() => Snap(at, save: false), DispatcherPriority.Loaded);
        }
    }

    private void Compact()
    {
        foreach (var button in Bar.Children.OfType<Button>())
        {
            button.Width = 34;
            button.Height = 34;
            button.MinWidth = 0;
            button.MinHeight = 0;
            button.Margin = new Thickness(1);
            button.Padding = new Thickness(0);
            button.Focusable = false;
            button.Background = Brushes.Transparent;
            button.BorderThickness = new Thickness(0);
            ToolTipService.SetInitialShowDelay(button, 400);
        }
    }

    private void SetVertical(bool vertical)
    {
        IsVertical = vertical;
        Bar.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        Bar.MaxWidth = double.PositiveInfinity;
        Bar.MaxHeight = double.PositiveInfinity;
        Grip.Width = vertical ? 34 : 14;
        Grip.Height = vertical ? 14 : 34;
        GripGlyph.Text = vertical ? GripVertical : GripHorizontal;
        foreach (var separator in Bar.Children.OfType<Border>().Where(b => Equals(b.Tag, "Separator")))
        {
            separator.Width = vertical ? 24 : 1;
            separator.Height = vertical ? 1 : 24;
            separator.Margin = vertical ? new Thickness(0, 3, 0, 3) : new Thickness(3, 0, 3, 0);
        }
    }

    /// <summary>Wraps it into more columns (or rows) when one would be longer than <paramref name="monitor"/>'s work area.</summary>
    private void FitTo(MonitorInfo monitor)
    {
        var scale = monitor.Scale;
        if (IsVertical)
        {
            Bar.MaxWidth = double.PositiveInfinity;
            Bar.MaxHeight = Math.Max(100, (monitor.WorkArea.Height / scale.ScaleY) - 8);
        }
        else
        {
            Bar.MaxHeight = double.PositiveInfinity;
            Bar.MaxWidth = Math.Max(100, (monitor.WorkArea.Width / scale.ScaleX) - 8);
        }
    }

    /// <summary>Where it was last time (or the top centre of the primary monitor).</summary>
    private void Restore()
    {
        // Onto its monitor first: that monitor's scale decides its size (a move there changes its DPI at once).
        var monitors = _monitors.GetMonitors();
        var monitor = monitors.FirstOrDefault(m => m.DeviceName == _placement?.Monitor) ?? monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
        Move(new PixelRect(monitor.WorkArea.X, monitor.WorkArea.Y, 1, 1));
        FitTo(monitor);

        // Created as a narrow column it can start at Windows' minimum window width; fitting it to its buttons again
        // once shown makes it its real size.
        SizeToContent = SizeToContent.Manual;
        SizeToContent = SizeToContent.WidthAndHeight;
        Dispatcher.BeginInvoke(
            () =>
            {
                var (bounds, _) = ToolbarDocking.Restore(_placement, _monitors.GetMonitors(), Size());
                Move(bounds);
            },
            DispatcherPriority.Loaded);
    }

    private void OnGripDown(object sender, MouseButtonEventArgs e)
    {
        // From the press itself: by the time it is handled the pointer may have moved on.
        var scale = Wpf.DpiHelper.GetScale(this);
        var at = e.GetPosition(this);
        _grab = new PixelPoint((int)Math.Round(at.X * scale.ScaleX), (int)Math.Round(at.Y * scale.ScaleY));
        _dragging = true;
        Grip.CaptureMouse();
        e.Handled = true;
    }

    private void OnGripMove(object sender, MouseEventArgs e)
    {
        if (_dragging)
        {
            FollowCursor();
        }
    }

    private void OnGripUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        FollowCursor(); // moves come in merged: the last may not have arrived before the release
        _dragging = false;
        Grip.ReleaseMouseCapture();
        Dock();
        e.Handled = true;
    }

    private void FollowCursor()
    {
        var cursor = CursorPosition();
        User32.SetWindowPos(
            _hwnd, 0, cursor.X - _grab.X, cursor.Y - _grab.Y, 0, 0, User32.SwpNoActivate | User32.SwpNoSize | User32.SwpNoZOrder);
    }

    /// <summary>After a drop: the edge it is near (if any) decides the orientation, then it goes flush against it.</summary>
    private void Dock()
    {
        var bounds = Bounds();
        var monitor = MonitorOf(bounds);
        Edge = ToolbarDocking.EdgeFor(bounds, monitor.WorkArea);
        var vertical = ToolbarDocking.VerticalFor(Edge) ?? IsVertical;
        if (vertical != IsVertical)
        {
            SetVertical(vertical);
        }

        FitTo(monitor);

        // After the layout (and the window's new size) for a new orientation.
        Dispatcher.BeginInvoke(() => Snap(bounds.TopLeft, save: true), DispatcherPriority.Loaded);
    }

    private void Snap(PixelPoint near, bool save)
    {
        var monitor = MonitorOf(new PixelRect(near.X, near.Y, 1, 1));
        var placed = ToolbarDocking.Place(near, Size(), Edge, monitor.WorkArea);
        Move(placed);
        if (save)
        {
            _ = _state.UpdateAsync(s => s with { Toolbar = ToolbarDocking.Describe(placed, Edge, IsVertical, monitor) });
        }
    }

    private void Move(PixelRect bounds) =>
        User32.SetWindowPos(_hwnd, 0, bounds.X, bounds.Y, 0, 0, User32.SwpNoActivate | User32.SwpNoSize | User32.SwpNoZOrder);

    private PixelSize Size()
    {
        var bounds = Bounds();
        return new PixelSize(Math.Max(1, bounds.Width), Math.Max(1, bounds.Height));
    }

    /// <summary>The monitor with the centre of <paramref name="bounds"/> (or the most of it, or the primary).</summary>
    private MonitorInfo MonitorOf(PixelRect bounds)
    {
        var monitors = _monitors.GetMonitors();
        var centre = new PixelPoint(bounds.X + (bounds.Width / 2), bounds.Y + (bounds.Height / 2));
        if (monitors.FirstOrDefault(m => m.Bounds.Contains(centre)) is { } inside)
        {
            return inside;
        }

        var most = monitors.MaxBy(m => m.Bounds.Intersect(bounds).Area);
        return most is not null && most.Bounds.IntersectsWith(bounds) ? most : monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
    }

    private static unsafe PixelPoint CursorPosition()
    {
        POINT point;
        return User32.GetCursorPos(&point) ? new PixelPoint(point.X, point.Y) : default;
    }

    private void OnDelay(object sender, RoutedEventArgs e) => Open((ContextMenu)Resources["DelayMenu"], DelayButton);

    private void OnAfterCapture(object sender, RoutedEventArgs e)
    {
        ((CaptureToolbarViewModel)DataContext).RefreshAfterSteps();
        Open((ContextMenu)Resources["AfterMenu"], AfterButton);
    }

    private void OnTools(object sender, RoutedEventArgs e) => Open((ContextMenu)Resources["ToolsMenu"], ToolsButton);

    private void OnFixedSizes(object sender, MouseButtonEventArgs e)
    {
        Open((ContextMenu)Resources["SizesMenu"], FixedButton);
        e.Handled = true;
    }

    /// <summary>Opens a menu by its button: below it in a row, beside it in a column.</summary>
    private void Open(ContextMenu menu, FrameworkElement button)
    {
        menu.DataContext = DataContext;
        menu.PlacementTarget = button;
        menu.Placement = IsVertical ? PlacementMode.Right : PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
