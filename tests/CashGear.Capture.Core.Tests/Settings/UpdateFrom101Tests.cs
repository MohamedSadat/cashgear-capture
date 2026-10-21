using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.Core.Tests.Settings;

/// <summary>
/// Updating from 1.0.1 (the last release before Phase 10) keeps every setting a user made: its settings.json is
/// schema 2, with the after-capture switches, the 15–35 quality number and showClicks that later versions moved.
/// </summary>
public sealed class UpdateFrom101Tests : IDisposable
{
    /// <summary>settings.json as 1.0.1 wrote it, with every setting changed from its default.</summary>
    private const string Written101 = """
        {
          "schemaVersion": 2,
          "capture": {
            "backend": "Wgc",
            "includeCursor": true,
            "hideDelayMs": 300,
            "wgcFrameTimeoutMs": 2500,
            "fullScreenOutput": "EachMonitor",
            "includeWindowShadow": true,
            "delaySeconds": 3,
            "scrollSettleMs": 400,
            "scrollMaxHeight": 30000
          },
          "output": {
            "folder": "D:\\Shots",
            "format": "Jpeg",
            "jpegQuality": 80,
            "fileNameTemplate": "Shot_{counter}_{kind}"
          },
          "afterCapture": {
            "save": true,
            "copyToClipboard": false,
            "openEditor": false
          },
          "hotkeys": {
            "captureRegion": "Ctrl+Shift+R",
            "captureActiveWindow": null,
            "captureFullScreen": "Ctrl+PrintScreen",
            "captureScrolling": "",
            "record": "Ctrl+Shift+F9",
            "captureText": "Ctrl+Alt+T"
          },
          "appearance": {
            "theme": "Dark"
          },
          "recording": {
            "fps": 60,
            "quality": 18,
            "includeCursor": false,
            "showClicks": false,
            "systemAudio": false,
            "microphone": "default"
          },
          "ocr": {
            "language": "ar"
          },
          "history": {
            "maxItems": 50
          },
          "updates": {
            "checkAutomatically": false,
            "feedUrl": "\\\\server\\capture"
          }
        }
        """;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cashgear-tests", "from101-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Every_setting_of_101_survives_the_update()
    {
        var paths = new AppPaths(_folder, Path.Combine(_folder, "logs"), _folder);
        Directory.CreateDirectory(_folder);
        File.WriteAllText(paths.SettingsFile, Written101);

        using var service = new JsonSettingsService(paths, NullLogger<JsonSettingsService>.Instance, TimeProvider.System, watchForExternalChanges: false);
        var s = service.Current;

        s.SchemaVersion.Should().Be(AppSettings.CurrentSchemaVersion);
        s.Capture.Should().BeEquivalentTo(new
        {
            Backend = CaptureBackend.Wgc,
            IncludeCursor = true,
            HideDelayMs = 300,
            WgcFrameTimeoutMs = 2500,
            FullScreenOutput = FullScreenOutput.EachMonitor,
            IncludeWindowShadow = true,
            DelaySeconds = 3,
            ScrollSettleMs = 400,
            ScrollMaxHeight = 30000,
        });
        s.Output.Should().Be(new OutputSettings(@"D:\Shots", ImageFileFormat.Jpeg, 80, "Shot_{counter}_{kind}"));
        s.Destinations.Workflow!.Select(step => (step.Destination, step.Enabled)).Should().Equal(
            (DestinationIds.File, true), (DestinationIds.Clipboard, false), (DestinationIds.Editor, false));
        s.Hotkeys.CaptureRegion.Should().Be("Ctrl+Shift+R");
        s.Hotkeys.CaptureActiveWindow.Should().BeNull();
        s.Hotkeys.CaptureFullScreen.Should().Be("Ctrl+PrintScreen");
        s.Hotkeys.CaptureScrolling.Should().BeNullOrEmpty();
        s.Hotkeys.Record.Should().Be("Ctrl+Shift+F9");
        s.Hotkeys.CaptureText.Should().Be("Ctrl+Alt+T");
        s.Appearance.Should().Be(new AppearanceSettings(AppTheme.Dark, UiLanguage.Automatic));
        s.Recording.Fps.Should().Be(60);
        s.Recording.Quality.Should().Be(QualityPreset.High, "quality 18 is nearest to High");
        s.Recording.IncludeCursor.Should().BeFalse();
        s.Overlays.Clicks!.Enabled.Should().BeFalse();
        s.Recording.SystemAudio.Should().BeFalse();
        s.Recording.Microphone.Should().Be(RecordingSettings.DefaultMicrophone);
        s.Ocr.Language.Should().Be("ar");
        s.History.MaxItems.Should().Be(50);
        s.Updates.CheckAutomatically.Should().BeFalse();
        s.Updates.FeedUrl.Should().Be(@"\\server\capture");

        File.ReadAllText(paths.SettingsFile).Should().Contain($"\"schemaVersion\": {AppSettings.CurrentSchemaVersion}", "the file is rewritten once in the current shape");
    }
}
