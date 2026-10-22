using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CashGear.Capture.App.Hotkeys;
using CashGear.Capture.App.Settings;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Engine.Tests;
using CashGear.Capture.Interop.Native;
using CashGear.Capture.Overlays.Tests;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.App.Tests.Settings;

/// <summary>The settings window driven by real key presses on the desktop: the webcam's placement with the arrows, Esc to cancel.</summary>
[Trait("Category", InteractiveDesktop.Category)]
[Collection(DesktopCollection.Name)]
public sealed class SettingsKeyboardTests : IDisposable
{
    private readonly MutableSettings _settings = new();
    private readonly HotkeyRegistrar _registrar;
    private readonly AppPaths _paths = new(@"C:\cfg", @"C:\data\logs", @"C:\Users\me\Pictures\CashGear Capture");

    public SettingsKeyboardTests()
    {
        _registrar = new HotkeyRegistrar(new FakeHotkeyService(), _settings, new FakeNotifier(), new InlineUiDispatcher(), NullLogger<HotkeyRegistrar>.Instance);
        _registrar.Start();
    }

    public void Dispose() => _registrar.Dispose();

    [Fact]
    public Task The_arrows_move_the_webcam_in_its_preview_and_Esc_cancels() => StaDispatcher.RunAsync(async () =>
    {
        InteractiveDesktop.Require();
        _settings.Current = new AppSettings { Overlays = new OverlaySettings(Webcam: new WebcamSettings(Enabled: true)) };
        using var vm = Create();
        var (window, closed) = await ShowAsync(vm);
        try
        {
            var tabs = (TabControl)window.FindName("Tabs");
            tabs.SelectedItem = window.FindName("OverlaysTab");
            await Task.Delay(500);
            var area = (WebcamPlacementArea)window.FindName("WebcamArea");
            var box = (Border)window.FindName("WebcamBox");
            area.BringIntoView();
            await Task.Delay(400);

            using var _ = TestInput.Exclusive();

            // A click in the preview's bottom-left corner, away from the webcam (bottom right): the arrows then move it.
            var corner = area.PointToScreen(new Point(6, area.ActualHeight - 6));
            (await Task.Run(() => TestInput.Click((int)corner.X, (int)corner.Y))).Should().BeTrue();
            await Task.Delay(300);
            area.IsKeyboardFocused.Should().BeTrue("a click in the preview gives it the keyboard");

            var (x, y) = (vm.Overlays.WebcamCenterX, vm.Overlays.WebcamCenterY);
            var left = Canvas.GetLeft(box);
            await Press(Key.Left);
            vm.Overlays.WebcamCenterX.Should().Be(x - 1, "an arrow moves it a percentage point");
            Canvas.GetLeft(box).Should().BeApproximately(left - 3.2, 0.01, "a point of the 320-wide preview");
            await Press(Key.Up, Key.LeftShift);
            vm.Overlays.WebcamCenterY.Should().Be(y - 10, "Shift moves it ten");
            await Press(Key.Right);
            await Press(Key.Down);
            (vm.Overlays.WebcamCenterX, vm.Overlays.WebcamCenterY).Should().Be((x, y - 9));
            area.IsKeyboardFocused.Should().BeTrue("the arrows stay in the preview");

            await Press(Key.Escape);
            closed().Should().BeTrue("Esc presses Cancel");
            _settings.Current.Overlays.WebcamSettings.CenterX.Should().Be(WebcamSettings.DefaultCenterX, "Cancel keeps the saved place");
        }
        finally
        {
            if (!closed())
            {
                window.Close();
            }
        }
    });

    [Fact]
    public Task Esc_in_a_hotkey_box_cancels_the_window() => StaDispatcher.RunAsync(async () =>
    {
        InteractiveDesktop.Require();
        using var vm = Create();
        var (window, closed) = await ShowAsync(vm);
        try
        {
            var tabs = (TabControl)window.FindName("Tabs");
            tabs.SelectedItem = tabs.Items.Cast<TabItem>().Single(t => (string)t.Header == "Hotkeys");
            await Task.Delay(500);
            var box = FindAll<HotkeyBox>(window).First();

            using var _ = TestInput.Exclusive();
            var (bx, by) = TestInput.Centre(box);
            (await Task.Run(() => TestInput.Click(bx, by))).Should().BeTrue();
            await Task.Delay(300);
            box.IsKeyboardFocused.Should().BeTrue();

            await Press(Key.Escape);
            closed().Should().BeTrue("Esc isn't taken as a hotkey: it cancels the window");
        }
        finally
        {
            if (!closed())
            {
                window.Close();
            }
        }
    });

    private SettingsViewModel Create() =>
        new(_settings, _paths, _registrar, new FakeStartupRegistration(), new FakeFolderPicker(), NullLogger<SettingsViewModel>.Instance);

    private static async Task<(SettingsWindow Window, Func<bool> Closed)> ShowAsync(SettingsViewModel vm)
    {
        var window = new SettingsWindow(vm, AppTheme.Light)
        {
            Topmost = true,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 120,
            Top = 60,
        };
        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show();
        await Task.Delay(600);
        return (window, () => closed);
    }

    private static async Task Press(Key key, params Key[] held)
    {
        var vk = (ushort)KeyInterop.VirtualKeyFromKey(key);
        var modifiers = held.Select(k => (ushort)KeyInterop.VirtualKeyFromKey(k)).ToArray();
        (await Task.Run(() => SendKey(vk, modifiers))).Should().BeTrue();
        await Task.Delay(200);
    }

    /// <summary>
    /// A key with modifiers held, the arrows sent as the extended keys a keyboard sends: sent plain they are the
    /// number pad's, and with NumLock on Windows lets go of Shift for them.
    /// </summary>
    private static unsafe bool SendKey(ushort key, ushort[] held)
    {
        const uint ExtendedKey = 0x0001; // KEYEVENTF_EXTENDEDKEY
        var arrow = key is >= 0x25 and <= 0x28;
        var inputs = new List<INPUT>(held.Select(k => INPUT.Key(k, up: false)));
        foreach (var up in new[] { false, true })
        {
            var input = INPUT.Key(key, up);
            input.ki.dwFlags |= arrow ? ExtendedKey : 0;
            inputs.Add(input);
        }

        inputs.AddRange(held.Reverse().Select(k => INPUT.Key(k, up: true)));
        var array = inputs.ToArray();
        fixed (INPUT* p = array)
        {
            return User32.SendInput((uint)array.Length, p, sizeof(INPUT)) == array.Length;
        }
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject root)
        where T : DependencyObject =>
        Platform.TestLayout.FindAll<T>(root);
}
