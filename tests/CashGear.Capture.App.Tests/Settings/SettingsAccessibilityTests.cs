using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using CashGear.Capture.App.Hotkeys;
using CashGear.Capture.App.Settings;
using CashGear.Capture.App.Tests.Platform;
using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.App.Tests.Settings;

/// <summary>The settings window from the keyboard and for a screen reader (laid out, not shown).</summary>
public sealed class SettingsAccessibilityTests : IDisposable
{
    private readonly MutableSettings _settings = new();
    private readonly HotkeyRegistrar _registrar;
    private readonly AppPaths _paths = new(@"C:\cfg", @"C:\data\logs", @"C:\Users\me\Pictures\CashGear Capture");

    public SettingsAccessibilityTests()
    {
        _registrar = new HotkeyRegistrar(new FakeHotkeyService(), _settings, new FakeNotifier(), new InlineUiDispatcher(), NullLogger<HotkeyRegistrar>.Instance);
        _registrar.Start();
    }

    public void Dispose() => _registrar.Dispose();

    [Fact]
    public void The_arrows_move_the_webcam_a_point_or_ten_and_keep_it_inside()
    {
        var overlays = new OverlaySettingsViewModel(null, []);
        overlays.MoveWebcam(0.5, 0.5);

        overlays.NudgeWebcam(1, 0, 15, 20);
        (overlays.WebcamCenterX, overlays.WebcamCenterY).Should().Be((51.0, 50.0));
        overlays.NudgeWebcam(0, -10, 15, 20);
        (overlays.WebcamCenterX, overlays.WebcamCenterY).Should().Be((51.0, 40.0));
        overlays.NudgeWebcam(0, -10, 15, 20);
        overlays.NudgeWebcam(0, -10, 15, 20);
        overlays.WebcamCenterY.Should().Be(20, "the box stays inside: its centre at least half its height from the top");

        overlays.MoveWebcam(1, 1);
        overlays.NudgeWebcam(-1, 0, 15, 20);
        overlays.WebcamCenterX.Should().Be(84, "a centre set past the edge moves from where the box shows");
        overlays.WebcamCenterY.Should().Be(80);
    }

    [Fact]
    public Task A_hotkey_box_leaves_a_plain_Esc_to_the_window() => StaDispatcher.RunAsync(() =>
    {
        var box = new HotkeyBox();
        TestKeys.Press(box, Key.Escape).Should().BeFalse("Esc reaches the window's Cancel button");
        TestKeys.Press(box, Key.F9).Should().BeTrue();
        box.GestureText.Should().Be("F9");
        return Task.CompletedTask;
    });

    [Fact]
    public Task The_window_names_its_lists_puts_its_buttons_last_and_cancels_with_Esc() => StaDispatcher.RunAsync(() =>
    {
        _settings.Current = new AppSettings
        {
            Destinations = new DestinationSettings(
                Servers: [new ServerDestination("office", "Office", ServerProtocol.Sftp, "o.test", UserName: "me")],
                Uploads: [new HttpDestination("pics", "Pictures", "https://pics.example.test/upload")]),
            Overlays = new OverlaySettings(Webcam: new WebcamSettings(Enabled: true)),
        }.Normalize();
        using var vm = new SettingsViewModel(_settings, _paths, _registrar, new FakeStartupRegistration(), new FakeFolderPicker(), NullLogger<SettingsViewModel>.Instance);
        var window = new SettingsWindow(vm, AppTheme.Light);
        var closed = false;
        window.Closed += (_, _) => closed = true;
        var content = TestLayout.Layout(window);

        // The pages, then the buttons: OK (Enter), Cancel (Esc), Apply.
        var controls = TestLayout.FindAll<Control>(content).Where(c => c.Focusable).ToList();
        var tabs = controls.OfType<TabControl>().Single();
        var cancel = controls.OfType<Button>().Single(b => b.IsCancel);
        controls.OfType<Button>().Single(b => b.IsDefault).Content.Should().Be("OK");
        controls.IndexOf(cancel).Should().BeGreaterThan(controls.IndexOf(tabs), "Tab reaches the buttons after the pages");
        controls.IndexOf(cancel).Should().BeGreaterThan(TestLayout.FindAll<Control>(tabs).Where(c => c.Focusable).Max(controls.IndexOf));

        tabs.SelectedItem = window.FindName("DestinationsTab");
        content = TestLayout.Layout(window);
        var lists = TestLayout.FindAll<ListBox>(content).ToList();
        TestLayout.ItemNames(lists.Single(l => System.Windows.Automation.AutomationProperties.GetName(l) == "Servers"))
            .Should().Equal([vm.Destinations.Servers.Single().Label], "a server is read by its name, not its type");
        TestLayout.ItemNames(lists.Single(l => System.Windows.Automation.AutomationProperties.GetName(l) == "HTTP uploads"))
            .Should().Equal(["Pictures"]);

        tabs.SelectedItem = window.FindName("OverlaysTab");
        content = TestLayout.Layout(window);
        var area = (WebcamPlacementArea)window.FindName("WebcamArea");
        var peer = UIElementAutomationPeer.CreatePeerForElement(area);
        peer.GetName().Should().Be("Webcam placement");
        peer.IsKeyboardFocusable().Should().BeTrue();
        var before = vm.Overlays.WebcamCenterX;
        TestKeys.Press(area, Key.Left).Should().BeTrue();
        vm.Overlays.WebcamCenterX.Should().Be(before - 1);
        ((IValueProvider)peer.GetPattern(PatternInterface.Value)).Value.Should().StartWith($"Centre {before - 1:0}% from the left");

        cancel.Command.Execute(null);
        closed.Should().BeTrue("Cancel, which Esc presses, closes the window");
        return Task.CompletedTask;
    });
}
