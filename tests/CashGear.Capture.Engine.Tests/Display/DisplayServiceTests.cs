using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Engine.Display;
using CashGear.Capture.Interop.Native;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.Engine.Tests.Display;

[Trait("Category", InteractiveDesktop.Category)]
public sealed class DisplayServiceTests
{
    [Fact]
    public void Test_process_is_per_monitor_v2_dpi_aware()
    {
        // Without this every physical-pixel assertion below is meaningless on a scaled monitor.
        User32.AreDpiAwarenessContextsEqual(User32.GetThreadDpiAwarenessContext(), User32.DpiAwarenessContextPerMonitorAwareV2)
            .Should().BeTrue("the test exe carries the same PerMonitorV2 manifest as the app");
    }

    [Fact]
    public void Monitors_have_exactly_one_primary_listed_first_at_origin()
    {
        InteractiveDesktop.Require();

        var monitors = new Win32MonitorService(NullLogger<Win32MonitorService>.Instance).GetMonitors();

        monitors.Should().NotBeEmpty();
        monitors.Count(m => m.IsPrimary).Should().Be(1);
        monitors[0].IsPrimary.Should().BeTrue();
        monitors[0].Bounds.TopLeft.Should().Be(new PixelPoint(0, 0));
    }

    [Fact]
    public void Monitor_details_are_sane()
    {
        InteractiveDesktop.Require();

        var monitors = new Win32MonitorService(NullLogger<Win32MonitorService>.Instance).GetMonitors();

        monitors.Should().AllSatisfy(m =>
        {
            m.Handle.Should().NotBe(0);
            m.DeviceName.Should().StartWith(@"\\.\DISPLAY");
            m.ShortName.Should().StartWith("DISPLAY");
            m.Bounds.IsEmpty.Should().BeFalse();
            m.Bounds.Contains(m.WorkArea).Should().BeTrue();
            m.DpiX.Should().BeGreaterThanOrEqualTo(96);
            m.DpiY.Should().Be(m.DpiX);
        });
        monitors.Select(m => m.DeviceName).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Monitor_union_matches_system_virtual_screen()
    {
        InteractiveDesktop.Require();

        var monitors = new Win32MonitorService(NullLogger<Win32MonitorService>.Instance).GetMonitors();
        var expected = new PixelRect(
            User32.GetSystemMetrics(User32.SmXVirtualScreen),
            User32.GetSystemMetrics(User32.SmYVirtualScreen),
            User32.GetSystemMetrics(User32.SmCxVirtualScreen),
            User32.GetSystemMetrics(User32.SmCyVirtualScreen));

        VirtualScreen.GetBounds(monitors.Select(m => m.Bounds)).Should().Be(expected);
        monitors.Should().HaveCount(User32.GetSystemMetrics(User32.SmCMonitors));
    }

    [Fact]
    public void Visible_windows_are_unique_non_empty_and_in_z_order()
    {
        InteractiveDesktop.Require();

        var windows = new Win32WindowService().GetVisibleWindows();

        windows.Should().NotBeEmpty("a logged-in desktop always has at least the taskbar's siblings or an app window");
        windows.Select(w => w.Handle).Should().OnlyHaveUniqueItems();
        windows.Select(w => w.ZOrder).Should().BeInAscendingOrder();
        windows.Should().AllSatisfy(w =>
        {
            w.Bounds.IsEmpty.Should().BeFalse();
            w.ProcessId.Should().NotBe(Environment.ProcessId);
            w.ClassName.Should().NotBeNullOrEmpty();
        });
        windows.Should().NotContain(w => w.Handle == User32.GetShellWindow());
    }

    [Fact]
    public void Window_from_point_returns_a_top_level_window_containing_the_point()
    {
        InteractiveDesktop.Require();
        var service = new Win32WindowService();
        var target = service.GetVisibleWindows().FirstOrDefault(w => !w.IsMinimized);
        Assert.SkipWhen(target is null, "No non-minimized window to probe.");

        var center = new PixelPoint(target!.Bounds.X + target.Bounds.Width / 2, target.Bounds.Y + target.Bounds.Height / 2);
        var hit = service.WindowFromPoint(center);

        hit.Should().NotBeNull();
        hit!.Bounds.Contains(center).Should().BeTrue();
    }
}
