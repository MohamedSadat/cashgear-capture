using CashGear.Capture.Core.Hotkeys;
using CashGear.Capture.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.Core.Tests.Hotkeys;

/// <summary>The "Use the toolbar from the keyboard" hotkey in settings.json.</summary>
public sealed class UseToolbarHotkeyTests : IDisposable
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
    public void It_is_ctrl_alt_shift_t_by_default_safe_and_no_other_default()
    {
        var defaults = new HotkeySettings();

        defaults.UseToolbar.Should().Be(HotkeySettings.DefaultUseToolbar).And.Be("Ctrl+Alt+Shift+T");
        var gesture = HotkeyGesture.Parse(defaults.UseToolbar!);
        gesture.Should().Be(new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, 'T'));
        gesture.IsSafeAsGlobalHotkey.Should().BeTrue();
        Enum.GetValues<HotkeyAction>().Where(a => a != HotkeyAction.UseToolbar).Select(defaults.For).Should().NotContain(defaults.UseToolbar);
        defaults.With(HotkeyAction.UseToolbar, " ").Normalize().UseToolbar.Should().BeNull("blank turns it off");
    }

    [Fact]
    public void A_settings_file_from_before_gets_the_default()
    {
        var paths = Paths();
        Directory.CreateDirectory(paths.SettingsDirectory);
        File.WriteAllText(paths.SettingsFile, """{ "schemaVersion": 4, "hotkeys": { "captureRegion": "PrintScreen", "crosshair": "Ctrl+F12" } }""");

        using var service = new JsonSettingsService(paths, NullLogger<JsonSettingsService>.Instance, TimeProvider.System, watchForExternalChanges: false);

        service.Current.Hotkeys.UseToolbar.Should().Be("Ctrl+Alt+Shift+T");
        service.Current.Hotkeys.Crosshair.Should().Be("Ctrl+F12");
    }

    [Fact]
    public async Task It_round_trips_through_the_settings_file_as_useToolbar_and_can_be_turned_off()
    {
        var paths = Paths();
        using (var service = new JsonSettingsService(paths, NullLogger<JsonSettingsService>.Instance, TimeProvider.System, watchForExternalChanges: false))
        {
            await service.UpdateAsync(s => s with { Hotkeys = s.Hotkeys.With(HotkeyAction.UseToolbar, "Ctrl+Shift+F9") }, TestContext.Current.CancellationToken);
        }

        File.ReadAllText(paths.SettingsFile).Should().Contain("\"useToolbar\": \"Ctrl+Shift+F9\"");
        using (var reread = new JsonSettingsService(paths, NullLogger<JsonSettingsService>.Instance, TimeProvider.System, watchForExternalChanges: false))
        {
            reread.Current.Hotkeys.For(HotkeyAction.UseToolbar).Should().Be("Ctrl+Shift+F9");
            await reread.UpdateAsync(s => s with { Hotkeys = s.Hotkeys with { UseToolbar = null } }, TestContext.Current.CancellationToken);
        }

        using var off = new JsonSettingsService(paths, NullLogger<JsonSettingsService>.Instance, TimeProvider.System, watchForExternalChanges: false);
        off.Current.Hotkeys.UseToolbar.Should().BeNull("null in the file means no hotkey, not the default");
    }

    private AppPaths Paths() => new(Path.Combine(_root, "settings"), Path.Combine(_root, "logs"), Path.Combine(_root, "captures"));
}
