using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using CashGear.Capture.App.Recording;
using CashGear.Capture.App.Tests.Recording;
using CashGear.Capture.App.Toolbar;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Engine.Display;
using CashGear.Capture.Engine.Tests;
using CashGear.Capture.Interop.Native;
using CashGear.Capture.Overlays.Tests;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.App.Tests.Toolbar;

/// <summary>
/// The "Use the toolbar from the keyboard" hotkey, on the real desktop: called as the router does, then driven with
/// real keys. A test window of this process stands for the user's window.
/// </summary>
[Trait("Category", InteractiveDesktop.Category)]
[Collection(DesktopCollection.Name)]
public sealed class ToolbarKeyboardTests : IDisposable
{
    private const ushort VkTab = 0x09;
    private const ushort VkReturn = 0x0D;
    private const ushort VkShift = 0x10;
    private const ushort VkEscape = 0x1B;
    private const ushort VkLeft = 0x25;
    private const ushort VkRight = 0x27;
    private const ushort VkDown = 0x28;
    private const ushort VkF10 = 0x79;

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
    public Task The_capture_toolbar_takes_the_keyboard_tab_moves_and_esc_gives_the_foreground_back() => StaDispatcher.RunAsync(async () =>
    {
        InteractiveDesktop.Require();
        using var input = TestInput.Exclusive();
        var (user, userHandle) = await UserWindowAsync();
        var toolbar = await ShowToolbarAsync();
        try
        {
            var buttons = toolbar.Bar.Children.OfType<Button>().ToList();
            toolbar.UseKeyboard();
            await Task.Delay(300);

            toolbar.UsesKeyboard.Should().BeTrue();
            toolbar.IsActive.Should().BeTrue();
            User32.GetForegroundWindow().Should().Be(toolbar.Handle);
            (ExStyle(toolbar.Handle) & User32.WsExNoActivate).Should().Be(0, "it can take the focus now");
            Keyboard.FocusedElement.Should().BeSameAs(buttons[0], "the first button");

            // The hotkey's keys coming up (here a Shift, as if Ctrl+Alt+Shift+T were let go) show the focus rectangle.
            await KeysAsync(VkShift);
            Keyboard.FocusedElement.Should().BeSameAs(buttons[0]);
            InputManager.Current.MostRecentInputDevice.Should().BeAssignableTo<KeyboardDevice>();
            var adorners = AdornerLayer.GetAdornerLayer(buttons[0]);
            adorners.Should().NotBeNull();
            adorners!.GetAdorners(buttons[0]).Should().NotBeNullOrEmpty("the focus rectangle");

            await KeysAsync(VkTab);
            Keyboard.FocusedElement.Should().BeSameAs(buttons[1]);
            await KeysAsync([VkTab], VkShift);
            Keyboard.FocusedElement.Should().BeSameAs(buttons[0]);
            await KeysAsync(toolbar.FlowDirection == FlowDirection.LeftToRight ? VkRight : VkLeft);
            Keyboard.FocusedElement.Should().BeSameAs(buttons[1], "the arrows move along the bar");

            await KeysAsync(VkEscape);
            toolbar.UsesKeyboard.Should().BeFalse();
            User32.GetForegroundWindow().Should().Be(userHandle, "Esc gives the foreground back");
            (ExStyle(toolbar.Handle) & User32.WsExNoActivate).Should().NotBe(0, "it never takes the focus again");
            buttons.Should().OnlyContain(b => !b.Focusable);
        }
        finally
        {
            toolbar.Close();
            user.Close();
        }
    });

    [Fact]
    public Task Enter_opens_a_menu_with_its_first_item_focused_and_a_capture_button_gives_the_foreground_back_first() => StaDispatcher.RunAsync(async () =>
    {
        InteractiveDesktop.Require();
        using var input = TestInput.Exclusive();
        var (user, userHandle) = await UserWindowAsync();
        var toolbar = await ShowToolbarAsync();
        try
        {
            toolbar.UseKeyboard();
            await Task.Delay(300);
            var tools = (Button)toolbar.FindName("ToolsButton");
            tools.Focus().Should().BeTrue();

            await KeysAsync(VkReturn);
            var menu = (ContextMenu)toolbar.Resources["ToolsMenu"];
            menu.IsOpen.Should().BeTrue("Enter presses the Tools button");
            Keyboard.FocusedElement.Should().BeSameAs(menu.Items[0], "its first item");
            await KeysAsync(VkDown);
            Keyboard.FocusedElement.Should().BeSameAs(menu.Items[1], "the arrows move in the menu");
            toolbar.UsesKeyboard.Should().BeTrue("a menu of its own doesn't take the focus away");

            await KeysAsync(VkEscape);
            menu.IsOpen.Should().BeFalse("the first Esc closes the menu");
            toolbar.UsesKeyboard.Should().BeTrue();
            Keyboard.FocusedElement.Should().BeSameAs(tools, "back on its button");

            // Shift+F10 on the fixed-size button: its sizes, which a right click shows.
            var fixedButton = (Button)toolbar.FindName("FixedButton");
            fixedButton.Focus().Should().BeTrue();
            await KeysAsync([VkF10], VkShift);
            var sizes = (ContextMenu)toolbar.Resources["SizesMenu"];
            sizes.IsOpen.Should().BeTrue();
            await KeysAsync(VkEscape);
            sizes.IsOpen.Should().BeFalse();

            // A capture button gives the focus back before its command runs (so "active window" is the user's).
            var first = toolbar.Bar.Children.OfType<Button>().First();
            first.Focus().Should().BeTrue();
            var foregroundWhenPressed = (nint)0;
            first.Click += (_, _) => foregroundWhenPressed = User32.GetForegroundWindow();
            await KeysAsync(VkReturn);
            toolbar.UsesKeyboard.Should().BeFalse();
            User32.GetForegroundWindow().Should().Be(userHandle);
            foregroundWhenPressed.Should().Be(toolbar.Handle, "the button's own handlers ran first, then the toolbar let go");
        }
        finally
        {
            toolbar.Close();
            user.Close();
        }
    });

    [Fact]
    public Task Losing_the_focus_ends_it_without_taking_the_foreground_back() => StaDispatcher.RunAsync(async () =>
    {
        InteractiveDesktop.Require();
        using var input = TestInput.Exclusive();
        var (user, _) = await UserWindowAsync();
        var toolbar = await ShowToolbarAsync();
        try
        {
            toolbar.UseKeyboard();
            await Task.Delay(300);
            toolbar.UsesKeyboard.Should().BeTrue();

            var other = TestInput.Form();
            try
            {
                var (x, y) = TestInput.Centre(other.Text);
                (await Task.Run(() => TestInput.Click(x, y))).Should().BeTrue();
                await Task.Delay(300);

                toolbar.UsesKeyboard.Should().BeFalse("the toolbar was deactivated");
                User32.GetForegroundWindow().Should().Be(new WindowInteropHelper(other.Window).Handle, "where the user went, not back");
                (ExStyle(toolbar.Handle) & User32.WsExNoActivate).Should().NotBe(0);
            }
            finally
            {
                other.Window.Close();
            }
        }
        finally
        {
            toolbar.Close();
            user.Close();
        }
    });

    [Fact]
    public Task The_recording_toolbar_takes_the_keyboard_and_esc_gives_it_back() => StaDispatcher.RunAsync(async () =>
    {
        InteractiveDesktop.Require();
        using var input = TestInput.Exclusive();
        var (user, userHandle) = await UserWindowAsync();
        var recorder = new FakeRecorder(RecordingSound.Microphone);
        var viewModel = new RecordingToolbarViewModel(recorder, () => recorder.IsPaused = !recorder.IsPaused, () => { });
        var primary = _monitors.GetMonitors().First(m => m.IsPrimary);
        var toolbar = new RecordingToolbarWindow(viewModel, AppTheme.Light, primary.WorkArea);
        toolbar.Show();
        await Task.Delay(500);
        try
        {
            toolbar.UseKeyboard();
            await Task.Delay(300);

            toolbar.UsesKeyboard.Should().BeTrue();
            toolbar.IsActive.Should().BeTrue();
            var mute = Keyboard.FocusedElement.Should().BeOfType<Button>().Subject;
            mute.Content.Should().Be(viewModel.MicrophoneText, "the first button: mute the microphone");

            // Space presses it; the toolbar keeps the keyboard.
            await KeysAsync(0x20);
            recorder.MicrophoneMuted.Should().BeTrue();
            toolbar.UsesKeyboard.Should().BeTrue();
            await KeysAsync(VkTab);
            toolbar.IsKeyboardFocusWithin.Should().BeTrue();
            Keyboard.FocusedElement.Should().NotBeSameAs(mute, "Tab moved to the next control");

            await KeysAsync(VkEscape);
            toolbar.UsesKeyboard.Should().BeFalse();
            User32.GetForegroundWindow().Should().Be(userHandle);
        }
        finally
        {
            toolbar.Close();
            user.Close();
        }
    });

    /// <summary>A window of this process in front, clicked into, standing for the user's.</summary>
    private static async Task<(Window Window, nint Handle)> UserWindowAsync()
    {
        var (window, _, text) = TestInput.Form();
        await Task.Delay(300);
        var (x, y) = TestInput.Centre(text);
        (await Task.Run(() => TestInput.Click(x, y))).Should().BeTrue();
        await Task.Delay(300);
        var handle = new WindowInteropHelper(window).Handle;
        User32.GetForegroundWindow().Should().Be(handle, "the test window should be in front");
        return (window, handle);
    }

    private async Task<CaptureToolbarWindow> ShowToolbarAsync()
    {
        var state = new JsonAppStateStore(
            new AppPaths(Path.Combine(_root, "settings"), Path.Combine(_root, "data", "logs"), _root), NullLogger<JsonAppStateStore>.Instance);
        var window = new CaptureToolbarWindow(new object(), AppTheme.Light, state, _monitors);
        window.Show();
        await Task.Delay(700);
        return window;
    }

    private static Task KeysAsync(ushort key) => KeysAsync([key]);

    private static async Task KeysAsync(ushort[] keys, params ushort[] held)
    {
        (await Task.Run(() => TestInput.Keys(keys, held))).Should().BeTrue();
        await Task.Delay(250);
    }

    private static long ExStyle(nint hwnd) => (long)User32.GetWindowLongPtrW(hwnd, User32.GwlExStyle);
}
