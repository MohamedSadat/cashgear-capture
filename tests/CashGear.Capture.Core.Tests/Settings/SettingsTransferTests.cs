using System.Text.Json.Nodes;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.Core.Tests.Settings;

public sealed class SettingsTransferTests
{
    private static readonly DateTimeOffset When = new(2026, 10, 22, 9, 30, 0, TimeSpan.FromHours(3));

    [Fact]
    public void Settings_and_presets_come_back_as_they_were_exported()
    {
        var settings = new AppSettings
        {
            Appearance = new AppearanceSettings(AppTheme.Dark, UiLanguage.Arabic),
            Output = new OutputSettings(@"D:\Shots", ImageFileFormat.Jpeg, 80, "Shot_{counter}"),
            Recording = new RecordingSettings(Fps: 60, Quality: QualityPreset.High, Microphone: RecordingSettings.DefaultMicrophone),
            Hotkeys = new HotkeySettings(CaptureRegion: "Ctrl+Shift+R"),
        }.Normalize();

        var text = SettingsTransfer.Write(settings, """{ "effects": [ { "name": "Mine" } ] }""", "1.1.0", When);
        var root = JsonNode.Parse(text)!.AsObject();
        root["format"]!.GetValue<string>().Should().Be("cashgear-capture-settings");
        root["version"]!.GetValue<int>().Should().Be(1);
        root["exported"]!.GetValue<string>().Should().Be("2026-10-22T09:30:00.0000000+03:00");
        text.Should().Contain("\"captureRegion\": \"Ctrl+Shift+R\"", "the file is readable, + not escaped");

        var back = SettingsTransfer.Read(text);
        back.Settings.Should().Be(settings);
        back.AppVersion.Should().Be("1.1.0");
        JsonNode.Parse(back.PresetsJson!)!["effects"]![0]!["name"]!.GetValue<string>().Should().Be("Mine");
    }

    [Fact]
    public void A_file_without_presets_imports_its_settings_only()
    {
        var back = SettingsTransfer.Read(SettingsTransfer.Write(new AppSettings(), null, "1.1.0", When));

        back.PresetsJson.Should().BeNull();
        back.Settings.Should().Be(new AppSettings().Normalize());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("""{ "theme": "dark" }""")]
    [InlineData("""{ "format": "something-else", "settings": {} }""")]
    public void Other_files_are_refused_with_a_reason(string text)
    {
        var read = () => SettingsTransfer.Read(text);

        read.Should().Throw<SettingsImportException>().WithMessage("This isn't a settings file exported by CashGear Capture.");
    }

    [Fact]
    public void A_file_without_settings_says_so()
    {
        var read = () => SettingsTransfer.Read("""{ "format": "cashgear-capture-settings", "version": 1 }""");

        read.Should().Throw<SettingsImportException>().WithMessage("The file has no settings in it.");
    }

    [Fact]
    public void Settings_exported_by_an_older_version_are_brought_up_to_date()
    {
        var text = """
            {
              "format": "cashgear-capture-settings", "version": 1, "appVersion": "1.0.1",
              "settings": { "schemaVersion": 2, "afterCapture": { "save": true, "copyToClipboard": false, "openEditor": false },
                            "recording": { "quality": 30, "showClicks": false } }
            }
            """;

        var settings = SettingsTransfer.Read(text).Settings;

        settings.SchemaVersion.Should().Be(AppSettings.CurrentSchemaVersion);
        settings.Recording.Quality.Should().Be(QualityPreset.Small);
        settings.Overlays.Clicks!.Enabled.Should().BeFalse();
        settings.Destinations.Workflow!.Select(s => (s.Destination, s.Enabled)).Should().Equal(
            (DestinationIds.File, true), (DestinationIds.Clipboard, false), (DestinationIds.Editor, false));
    }

    [Fact]
    public void A_reset_keeps_the_language_and_an_import_takes_the_files()
    {
        var current = new AppSettings { Appearance = new AppearanceSettings(AppTheme.Dark, UiLanguage.Arabic) };
        var incoming = new AppSettings { Appearance = new AppearanceSettings(AppTheme.Light, UiLanguage.English) };

        SettingsTransfer.KeepLocal(new AppSettings(), current, keepLanguage: true).Appearance
            .Should().Be(new AppearanceSettings(AppTheme.System, UiLanguage.Arabic));
        SettingsTransfer.KeepLocal(incoming, current, keepLanguage: false).Appearance
            .Should().Be(new AppearanceSettings(AppTheme.Light, UiLanguage.English));
    }
}
