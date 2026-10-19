using System.Buffers;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Engine.Capture;
using CashGear.Capture.Engine.Capture.Gdi;
using CashGear.Capture.Engine.Display;
using CashGear.Capture.Engine.Objects;
using CashGear.Capture.Engine.Tests;
using CashGear.Capture.Interop.Native;
using CashGear.Capture.Overlays.Tests;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.App.Tests.Capture;

/// <summary>
/// Capture Object (UI Automation, read through the source-generated COM interfaces) and Capture Open Menu, on
/// every monitor in turn, against a test window of known buttons and real menus (a classic Win32 menu and a WPF
/// context menu).
/// </summary>
[Trait("Category", InteractiveDesktop.Category)]
[Collection(DesktopCollection.Name)]
public sealed class ObjectAndMenuCaptureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cashgear-tests", Guid.NewGuid().ToString("N"));
    private readonly Win32MonitorService _monitors = new(NullLogger<Win32MonitorService>.Instance);
    private readonly GdiFrameGrabber _gdi = new(ArrayPool<byte>.Shared, TimeProvider.System, NullLogger<GdiFrameGrabber>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public Task The_finder_reads_the_buttons_rectangle_name_and_type_and_the_way_out_to_the_window() => OnEveryMonitor(async (_, monitor, form) =>
    {
        using var finder = new UiaObjectFinder(NullLogger<UiaObjectFinder>.Instance);
        var found = await finder.FindAtAsync(form.Handle, Centre(form.Second));

        found.Should().HaveCountGreaterThanOrEqualTo(2, "the button and the window (WPF panels have no UI Automation element)");
        found[0].Name.Should().Be("Second");
        found[0].Kind.Should().NotBeEmpty();
        found[0].Bounds.Should().Match<PixelRect>(b => Near(b, ScreenRect(form.Second)), $"UI Automation's rectangle of the button on {monitor.DeviceName} is where WPF put it");
        found[^1].Bounds.Should().Match<PixelRect>(b => b.Contains(ScreenRect(form.Panel)), "the window comes last");
        found.Select(o => o.Bounds.Area).Should().BeInAscendingOrder("each object contains the one before");

        var outside = await finder.FindAtAsync(form.Handle, new PixelPoint(-100000, -100000));
        outside.Should().ContainSingle("only the window: no part of it is there");
    });

    /// <summary>Another process's controls, read across processes: the taskbar's Start button (Explorer). Nothing is clicked.</summary>
    [Fact]
    public async Task The_finder_reads_another_apps_controls()
    {
        InteractiveDesktop.Require();
        var taskbar = NativeEnumerators.EnumWindows().FirstOrDefault(h => ClassName(h) == "Shell_TrayWnd");
        Assert.SkipWhen(taskbar == 0, "No taskbar (Explorer isn't the shell).");
        var bounds = WindowBounds(taskbar);
        using var finder = new UiaObjectFinder(NullLogger<UiaObjectFinder>.Instance);

        var found = await finder.FindAtAsync(taskbar, new PixelPoint(bounds.X + 20, bounds.Y + 20), TestContext.Current.CancellationToken);

        found.Should().HaveCountGreaterThanOrEqualTo(2);
        found[0].Kind.Should().NotBeEmpty();
        found[0].Bounds.Area.Should().BeLessThan(bounds.Area / 4, $"the Start button, not the whole taskbar (found {found[0].Description})");
        bounds.Contains(found[0].Bounds).Should().BeTrue();
    }

    [Fact]
    public Task Object_capture_takes_the_button_under_the_cursor_and_the_wheel_its_container() => OnEveryMonitor(async (harness, monitor, form) =>
    {
        using var finder = new UiaObjectFinder(NullLogger<UiaObjectFinder>.Instance);
        var chain = await finder.FindAtAsync(form.Handle, Centre(form.First));
        harness.Windows.Extra.Add(form.Handle);
        await MoveAway(form); // so the frozen screen shows the buttons as they are without the pointer

        var capture = harness.Coordinator.CaptureObjectAsync();
        await harness.WaitForOverlayAsync();
        var point = Centre(form.First);
        await Input(() => TestInput.Move(point.X - 3, point.Y) && TestInput.Move(point.X, point.Y));
        await Task.Delay(800); // the lookup crosses into UI Automation
        await Input(() => TestInput.Click(point.X, point.Y));
        await capture;
        await MoveAway(form); // the button lights up under the pointer

        var button = harness.Output.Last;
        button.Metadata.Kind.Should().Be(CaptureKind.Object);
        button.Metadata.SourceBounds.Should().Be(chain[0].Bounds, "exactly the rectangle UI Automation reports for the button");
        await AssertShowsTheScreenAsync(button, monitor);

        // Repeat: the same pixels.
        await harness.Coordinator.RepeatLastCaptureAsync();
        harness.Output.Last.Metadata.SourceBounds.Should().Be(chain[0].Bounds);
        harness.Output.Last.Pixels.SequenceEqual(button.Pixels).Should().BeTrue();

        // The wheel goes out to what holds the button.
        capture = harness.Coordinator.CaptureObjectAsync();
        await harness.WaitForOverlayAsync();
        await Input(() => TestInput.Move(point.X + 2, point.Y) && TestInput.Move(point.X, point.Y));
        await Task.Delay(800);
        await Input(() => TestInput.Wheel(1) && TestInput.Click(point.X, point.Y));
        await capture;

        var frame = new Win32WindowService().GetWindow(form.Handle)!.Bounds;
        harness.Output.Last.Metadata.SourceBounds.Should().Be(frame, "the button is straight in the window (panels have no element): its visible frame, without the invisible borders");
    });

    [Fact]
    public Task An_open_classic_menu_is_captured_exactly_with_no_selection() => OnEveryMonitor(async (harness, monitor, form) =>
    {
        harness.Windows.MenusOf = form.Handle;
        var menu = User32.CreatePopupMenu();
        try
        {
            foreach (var (id, text) in new[] { (1, "&Open"), (2, "&Save as…"), (3, "E&xit") })
            {
                User32.AppendMenuW(menu, 0 /* MF_STRING */, (nuint)id, text);
            }

            var at = Centre(form.First);
            var closed = form.Window.Dispatcher.InvokeAsync(() => User32.TrackPopupMenu(menu, 0x0100 /* RETURNCMD */, at.X, at.Y, 0, form.Handle, 0));
            await Task.Delay(700); // runs inside the menu's modal loop

            var open = new Win32WindowService().GetOpenMenus(includeOwnProcess: true, form.Handle);
            open.Should().ContainSingle().Which.ClassName.Should().Be(OpenMenus.MenuClass);
            harness.Settings.Current.Capture.DelayFor(CaptureMode.Menu).Should().Be(3, "the default: time to open the menu");

            await harness.Coordinator.CaptureMenuAsync();
            User32.EndMenu();
            await closed.Task;

            var image = harness.Output.Last;
            image.Metadata.Kind.Should().Be(CaptureKind.Menu);
            image.Metadata.SourceBounds.Should().Be(open[0].Bounds);
            image.Metadata.IsNonRectangular.Should().BeFalse("one menu is a rectangle");
            harness.Countdown.Requested.Should().Equal((3, CaptureMode.Menu.Label()));
            await AssertShowsTheScreenAsync(image, monitor, ignore: open[0].Bounds); // the menu is closed by now
        }
        finally
        {
            User32.DestroyMenu(menu);
        }
    });

    [Fact]
    public Task An_open_WPF_context_menu_is_found_as_a_popup_of_the_app_in_front() => OnEveryMonitor(async (harness, monitor, form) =>
    {
        harness.Windows.MenusOf = form.Handle;
        var menu = new ContextMenu { PlacementTarget = form.First, Placement = PlacementMode.Bottom };
        menu.Items.Add(new MenuItem { Header = "Copy" });
        menu.Items.Add(new MenuItem { Header = "Paste" });
        menu.IsOpen = true;
        await Task.Delay(700);
        try
        {
            var bounds = WindowBounds(((HwndSource)PresentationSource.FromVisual(menu)).Handle);
            await harness.Coordinator.CaptureMenuAsync();

            var image = harness.Output.Last;
            image.Metadata.Kind.Should().Be(CaptureKind.Menu);
            image.Metadata.SourceBounds.Should().Be(bounds);
            await AssertShowsTheScreenAsync(image, monitor);
        }
        finally
        {
            menu.IsOpen = false;
        }
    });

    [Fact]
    public Task With_no_menu_open_the_user_is_told_how() => StaDispatcher.RunAsync(async () =>
    {
        InteractiveDesktop.Require();
        using var harness = new CaptureHarness(Path.Combine(_root, "none"));
        harness.Windows.MenusOf = -1; // a window that doesn't exist is in front: only classic menus could count, and none is open

        await harness.Coordinator.CaptureMenuAsync();

        harness.Output.Images.Should().BeEmpty();
        harness.Notifier.Messages.Should().ContainSingle().Which.Text.Should().Contain("Open the menu during the countdown (3 s");
    });

    private async Task AssertShowsTheScreenAsync(CapturedImage image, MonitorInfo monitor, PixelRect? ignore = null)
    {
        if (ignore is not null)
        {
            return; // what was captured has gone from the screen
        }

        using var screen = await _gdi.CaptureAsync(
            new FrameRequest(monitor, image.Metadata.SourceBounds, false, TimeSpan.FromSeconds(2)), TestContext.Current.CancellationToken);
        screen.Pixels.SequenceEqual(image.Pixels).Should().BeTrue($"the capture of {image.Metadata.SourceBounds} must equal the screen there");
    }

    private async Task OnEveryMonitor(Func<CaptureHarness, MonitorInfo, TestForm, Task> body)
    {
        InteractiveDesktop.Require();
        foreach (var monitor in _monitors.GetMonitors())
        {
            await StaDispatcher.RunAsync(async () =>
            {
                using var harness = new CaptureHarness(Path.Combine(_root, monitor.ShortName + Guid.NewGuid().ToString("N")[..6]));
                var form = await TestForm.ShowAsync(monitor);
                try
                {
                    await body(harness, monitor, form);
                }
                finally
                {
                    form.Window.Close();
                }
            });
        }
    }

    /// <summary>Takes the pointer off the form, so nothing in it shows a hover state.</summary>
    private static Task MoveAway(TestForm form)
    {
        var bounds = WindowBounds(form.Handle);
        return Input(() => TestInput.Move(bounds.Right + 40, bounds.Bottom + 40));
    }

    private static async Task Input(Func<bool> send)
    {
        using var exclusive = TestInput.Exclusive();
        var sent = await Task.Run(send);
        Assert.SkipUnless(sent, "SendInput was blocked.");
        await Task.Delay(300);
    }

    private static PixelPoint Centre(FrameworkElement element)
    {
        var (x, y) = TestInput.Centre(element);
        return new PixelPoint(x, y);
    }

    /// <summary>Where WPF put the element, in physical screen pixels.</summary>
    private static PixelRect ScreenRect(FrameworkElement element)
    {
        var a = element.PointToScreen(new Point(0, 0));
        var b = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        return PixelRect.FromLTRB((int)Math.Round(a.X), (int)Math.Round(a.Y), (int)Math.Round(b.X), (int)Math.Round(b.Y));
    }

    private static unsafe string ClassName(nint hwnd)
    {
        var buffer = stackalloc char[256];
        var length = User32.GetClassNameW(hwnd, buffer, 256);
        return new string(buffer, 0, Math.Max(length, 0));
    }

    private static unsafe PixelRect WindowBounds(nint hwnd)
    {
        RECT rect;
        User32.GetWindowRect(hwnd, &rect).Should().BeTrue();
        return PixelRect.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    private static bool Near(PixelRect a, PixelRect b) =>
        Math.Abs(a.X - b.X) <= 1 && Math.Abs(a.Y - b.Y) <= 1 && Math.Abs(a.Right - b.Right) <= 1 && Math.Abs(a.Bottom - b.Bottom) <= 1;

    /// <summary>A top-most window with a padded panel of two buttons.</summary>
    private sealed record TestForm(Window Window, nint Handle, StackPanel Panel, Button First, Button Second)
    {
        public static async Task<TestForm> ShowAsync(MonitorInfo monitor)
        {
            var first = new Button { Content = "First", Width = 120, Height = 40, Margin = new Thickness(10) };
            var second = new Button { Content = "Second", Width = 120, Height = 40, Margin = new Thickness(10) };
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.LightSteelBlue };
            panel.Children.Add(first);
            panel.Children.Add(second);
            var window = new Window
            {
                Title = "CashGear object test",
                Content = new Border { Padding = new Thickness(30), Background = Brushes.White, Child = panel },
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStyle = WindowStyle.ToolWindow,
                Topmost = true,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = (monitor.WorkArea.X + 100) / monitor.Scale.ScaleX,
                Top = (monitor.WorkArea.Y + 100) / monitor.Scale.ScaleY,
            };
            window.Show();
            var hwnd = new WindowInteropHelper(window).Handle;
            User32.SetWindowPos(hwnd, User32.HwndTopmost, monitor.WorkArea.X + 100, monitor.WorkArea.Y + 100, 0, 0, User32.SwpNoActivate | User32.SwpNoSize);
            await Task.Delay(600);
            return new TestForm(window, hwnd, panel, first, second);
        }
    }
}
