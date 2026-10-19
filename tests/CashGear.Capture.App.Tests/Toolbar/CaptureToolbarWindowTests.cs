using System.IO;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using CashGear.Capture.App.Toolbar;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Engine.Display;
using CashGear.Capture.Engine.Tests;
using CashGear.Capture.Interop.Native;
using CashGear.Capture.Overlays.Tests;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.App.Tests.Toolbar;

/// <summary>The capture toolbar on the real desktop: placement, docking by dragging its grip, memory, focus and capture exclusion.</summary>
[Trait("Category", InteractiveDesktop.Category)]
[Collection(DesktopCollection.Name)]
public sealed class CaptureToolbarWindowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cashgear-tests", Guid.NewGuid().ToString("N"));
    private readonly Win32MonitorService _monitors = new(NullLogger<Win32MonitorService>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public Task It_starts_at_the_top_centre_on_top_left_out_of_captures_and_never_takes_the_focus() => StaDispatcher.RunAsync(async () =>
    {
        InteractiveDesktop.Require();
        var state = State("first");
        var window = await ShowAsync(state);
        try
        {
            var primary = _monitors.GetMonitors().First(m => m.IsPrimary);
            var bounds = window.Bounds();
            bounds.Y.Should().Be(primary.WorkArea.Y);
            Math.Abs(bounds.X + (bounds.Width / 2) - (primary.WorkArea.X + (primary.WorkArea.Width / 2))).Should().BeLessThanOrEqualTo(1);
            window.IsVertical.Should().BeFalse();
            Affinity(window.Handle).Should().Be(User32.WdaExcludeFromCapture);
            var exStyle = (long)User32.GetWindowLongPtrW(window.Handle, User32.GwlExStyle);
            (exStyle & User32.WsExNoActivate).Should().NotBe(0);
            (exStyle & User32.WsExToolWindow).Should().NotBe(0, "not in Alt+Tab");
            window.Topmost.Should().BeTrue();

            // A click on a button leaves the window in front where it was.
            var before = User32.GetForegroundWindow();
            var after = window.FindName("AfterButton") as Button;
            var (x, y) = TestInput.Centre(after!);
            using (TestInput.Exclusive())
            {
                (await Task.Run(() => TestInput.Click(x, y))).Should().BeTrue();
                await Task.Delay(400);
            }

            User32.GetForegroundWindow().Should().Be(before);
            ((ContextMenu)window.Resources["AfterMenu"]).IsOpen.Should().BeTrue("the button opened its menu");
            ((ContextMenu)window.Resources["AfterMenu"]).IsOpen = false;
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Dragged_to_the_left_edge_of_each_monitor_it_docks_there_in_a_column_and_comes_back_there() => StaDispatcher.RunAsync(async () =>
    {
        InteractiveDesktop.Require();
        foreach (var monitor in _monitors.GetMonitors())
        {
            var state = State(monitor.ShortName);
            var window = await ShowAsync(state);
            try
            {
                var grip = window.GripElement;
                var (x, y) = TestInput.Centre(grip);
                var target = new PixelPoint(monitor.WorkArea.X + 15, monitor.WorkArea.Y + 300);
                using (TestInput.Exclusive())
                {
                    var path = Enumerable.Range(0, 11).Select(i => (x + ((target.X - x) * i / 10), y + ((target.Y - y) * i / 10))).ToArray();
                    (await Task.Run(() => TestInput.Drag(path))).Should().BeTrue();
                    await Task.Delay(800);
                }

                window.Edge.Should().Be(DockEdge.Left, $"dropped 15 px from {monitor.DeviceName}'s left edge {monitor.WorkArea} (at {window.Bounds()}, cursor {Cursor()})");
                window.IsVertical.Should().BeTrue();
                var docked = window.Bounds();
                docked.X.Should().Be(monitor.WorkArea.X, "flush against the edge");
                docked.Height.Should().BeGreaterThan(docked.Width);
                monitor.WorkArea.Contains(docked).Should().BeTrue();
                state.Current.Toolbar.Should().Be(new ToolbarPlacement(monitor.DeviceName, 0, docked.Y - monitor.WorkArea.Y, DockEdge.Left, true));

                window.Close();
                window = await ShowAsync(state);
                window.Bounds().Should().Be(docked, "it comes back where it was");
                window.IsVertical.Should().BeTrue();
            }
            finally
            {
                window.Close();
            }
        }
    });

    private JsonAppStateStore State(string name) =>
        new(new AppPaths(Path.Combine(_root, name, "settings"), Path.Combine(_root, name, "data", "logs"), _root), NullLogger<JsonAppStateStore>.Instance);

    private async Task<CaptureToolbarWindow> ShowAsync(IAppStateStore state)
    {
        var window = new CaptureToolbarWindow(new object(), AppTheme.Light, state, _monitors);
        window.Show();
        await Task.Delay(700);
        return window;
    }

    private static unsafe PixelPoint Cursor()
    {
        POINT point;
        User32.GetCursorPos(&point);
        return new PixelPoint(point.X, point.Y);
    }

    private static unsafe uint Affinity(nint hwnd)
    {
        uint affinity;
        User32.GetWindowDisplayAffinity(hwnd, &affinity).Should().BeTrue();
        return affinity;
    }
}
