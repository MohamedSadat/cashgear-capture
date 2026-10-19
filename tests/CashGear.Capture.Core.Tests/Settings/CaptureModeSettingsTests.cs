using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Hotkeys;
using CashGear.Capture.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.Core.Tests.Settings;

/// <summary>Per-mode delays, the new capture hotkeys, and the fixed size and corner radius in settings.json.</summary>
public sealed class CaptureModeSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cashgear-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Every_mode_uses_the_general_delay_except_an_open_menu()
    {
        var capture = new CaptureSettings(DelaySeconds: 5);

        foreach (var mode in Enum.GetValues<CaptureMode>().Where(m => m != CaptureMode.Menu))
        {
            capture.DelayFor(mode).Should().Be(5, mode.ToString());
        }

        capture.DelayFor(CaptureMode.Menu).Should().Be(ModeDelays.DefaultMenuDelay, "time to open the menu");
    }

    [Fact]
    public void A_mode_delay_overrides_the_general_one_and_null_goes_back_to_it()
    {
        var delays = new ModeDelays().With(CaptureMode.Region, 10).With(CaptureMode.Menu, null);
        var capture = new CaptureSettings(DelaySeconds: 2, Delays: delays);

        capture.DelayFor(CaptureMode.Region).Should().Be(10);
        capture.DelayFor(CaptureMode.Menu).Should().Be(2);
        capture.DelayFor(CaptureMode.FullScreen).Should().Be(2);
    }

    [Fact]
    public void With_and_For_cover_every_mode()
    {
        foreach (var mode in Enum.GetValues<CaptureMode>())
        {
            new ModeDelays().With(mode, 7).For(mode).Should().Be(7, mode.ToString());
        }
    }

    [Fact]
    public void Delays_are_clamped_and_compare_by_value()
    {
        var normalized = new CaptureSettings(Delays: new ModeDelays(Region: -4, Polygon: 999)).Normalize();

        normalized.Delays!.Region.Should().Be(0);
        normalized.Delays.Polygon.Should().Be(CaptureSettings.MaxDelaySeconds);
        new CaptureSettings().Should().Be(new CaptureSettings(), "the delays are a value, not a reference");
    }

    [Fact]
    public async Task Delays_fixed_size_and_radius_round_trip_through_the_settings_file()
    {
        var paths = new AppPaths(Path.Combine(_root, "settings"), Path.Combine(_root, "logs"), Path.Combine(_root, "captures"));
        using (var service = new JsonSettingsService(paths, NullLogger<JsonSettingsService>.Instance, TimeProvider.System, watchForExternalChanges: false))
        {
            await service.UpdateAsync(
                s => s with
                {
                    Capture = s.Capture with
                    {
                        Delays = new ModeDelays().With(CaptureMode.Object, 4).With(CaptureMode.Menu, null),
                        FixedRegionWidth = 1024,
                        FixedRegionHeight = 300,
                        CornerRadius = 30,
                    },
                },
                TestContext.Current.CancellationToken);
        }

        using var reread = new JsonSettingsService(paths, NullLogger<JsonSettingsService>.Instance, TimeProvider.System, watchForExternalChanges: false);
        var capture = reread.Current.Capture;
        capture.DelayFor(CaptureMode.Object).Should().Be(4);
        capture.DelayFor(CaptureMode.Menu).Should().Be(0, "set back to the general delay, which is 0");
        capture.FixedRegionSize.Should().Be(new CashGear.Capture.Core.Geometry.PixelSize(1024, 300));
        capture.CornerRadius.Should().Be(30);
    }

    [Fact]
    public void A_settings_file_from_before_has_the_new_defaults()
    {
        var paths = new AppPaths(Path.Combine(_root, "settings"), Path.Combine(_root, "logs"), Path.Combine(_root, "captures"));
        Directory.CreateDirectory(paths.SettingsDirectory);
        File.WriteAllText(paths.SettingsFile, """{ "schemaVersion": 4, "capture": { "delaySeconds": 1 }, "hotkeys": { "captureRegion": "PrintScreen" } }""");

        using var service = new JsonSettingsService(paths, NullLogger<JsonSettingsService>.Instance, TimeProvider.System, watchForExternalChanges: false);

        service.Current.Capture.DelayFor(CaptureMode.Menu).Should().Be(3);
        service.Current.Capture.DelayFor(CaptureMode.Region).Should().Be(1);
        service.Current.Capture.FixedRegionSize.Should().Be(new CashGear.Capture.Core.Geometry.PixelSize(800, 600));
        service.Current.Hotkeys.RepeatLastCapture.Should().Be("Ctrl+Alt+Shift+PrintScreen");
        service.Current.Hotkeys.CapturePolygon.Should().BeNull("new modes start without a hotkey");
    }

    [Fact]
    public void Every_hotkey_action_is_read_and_written()
    {
        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            new HotkeySettings().With(action, "Ctrl+F7").For(action).Should().Be("Ctrl+F7", action.ToString());
        }

        new HotkeySettings(CaptureFreehand: "  Ctrl+F8 ", CaptureWindow: " ").Normalize()
            .Should().Match<HotkeySettings>(h => h.CaptureFreehand == "Ctrl+F8" && h.CaptureWindow == null);
    }

    [Fact]
    public void The_default_repeat_hotkey_is_safe_and_unused_by_the_other_defaults()
    {
        var defaults = new HotkeySettings();
        HotkeyGesture.Parse(defaults.RepeatLastCapture!).IsSafeAsGlobalHotkey.Should().BeTrue();
        Enum.GetValues<HotkeyAction>().Select(defaults.For).OfType<string>().Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Labels_name_every_mode() =>
        Enum.GetValues<CaptureMode>().Select(m => m.Label()).Should().OnlyHaveUniqueItems().And.NotContain(l => l.Length == 0);
}
