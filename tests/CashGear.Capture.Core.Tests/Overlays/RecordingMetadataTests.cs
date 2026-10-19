using System.Text.Json.Nodes;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Overlays;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.Core.Tests.Overlays;

public sealed class RecordingMetadataTests : IDisposable
{
    private static readonly MetadataHeader Header = new(30, new MetadataArea(100, 200, 640, 360), 1.5, CursorInVideo: true, OverlaysInVideo: false, KeystrokeMode.Shortcuts);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cashgear-tests", Guid.NewGuid().ToString("N"));

    public RecordingMetadataTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private string Journal(Action<MetadataJournal> write)
    {
        var path = Path.Combine(_folder, MetadataJournal.FileName);
        using var journal = new MetadataJournal(path, Header);
        write(journal);
        return path;
    }

    [Fact]
    public void A_journal_becomes_the_metadata_file_with_positions_relative_to_the_area()
    {
        var path = Journal(j =>
        {
            j.Cursor(new MetadataCursor(1, 2, 1, 0, 0, Convert.ToBase64String(new byte[8])));
            j.Focus(new FocusChange(S(0), "chrome.exe", "Docs – \"quoted\" <tag>"));
            j.Frame(0, new PixelPoint(110, 220), 1, visible: true);
            j.Frame(1, new PixelPoint(120, 230), 1, visible: true);
            j.ButtonDown(S(0.04), PointerButton.Left, new PixelPoint(120, 230));
            j.ButtonUp(S(0.12), PointerButton.Left);
            j.ButtonDown(S(0.5), PointerButton.Right, new PixelPoint(101, 201));
            j.Key(new KeyCaption(1, S(0.25), "Ctrl + S"));
            j.Drawing(S(0.3), on: true);
        });

        var metadata = MetadataJournal.Read(path, @"D:\Videos\Rec.mp4", [new MetadataPart("Rec.mp4", 0)])!;

        metadata.Format.Should().Be(RecordingMetadata.FormatName);
        metadata.Video.Should().Be("Rec.mp4");
        (metadata.Fps, metadata.Area, metadata.Scale, metadata.CursorInVideo, metadata.OverlaysInVideo, metadata.Keystrokes)
            .Should().Be((30, Header.Area, 1.5, true, false, KeystrokeMode.Shortcuts));
        metadata.Frames.Should().BeEquivalentTo(new[] { new[] { 0, 10, 20, 1, 1 }, [1, 20, 30, 1, 1] }, o => o.WithStrictOrdering());
        metadata.Clicks.Should().Equal(new MetadataClick(0.04, 0.12, PointerButton.Left, 20, 30), new MetadataClick(0.5, null, PointerButton.Right, 1, 1));
        metadata.Keys.Should().Equal(new MetadataKey(0.25, 1, "Ctrl + S"));
        metadata.Focus.Should().Equal(new MetadataFocus(0, "chrome.exe", "Docs – \"quoted\" <tag>"));
        metadata.Drawing.Should().Equal(new MetadataToggle(0.3, true));
        metadata.Cursors.Should().ContainSingle().Which.Pixels().Should().HaveCount(8);

        var file = Path.Combine(_folder, "Rec" + RecordingMetadata.Extension);
        metadata.Save(file);
        File.ReadAllText(file).Should().Contain("Docs – \\\"quoted\\\" <tag>", "titles are written as they are, not HTML-escaped");
        var loaded = RecordingMetadata.Load(file);
        loaded.Keys.Should().Equal(metadata.Keys);
        loaded.Clicks.Should().Equal(metadata.Clicks);
        loaded.Frames.Should().BeEquivalentTo(metadata.Frames, o => o.WithStrictOrdering());
        JsonNode.Parse(File.ReadAllText(file))!["keystrokes"]!.GetValue<string>().Should().Be("Shortcuts");
    }

    [Fact]
    public void A_journal_cut_short_by_a_crash_keeps_what_was_written()
    {
        var path = Journal(j =>
        {
            j.Frame(0, new PixelPoint(100, 200), 0, visible: true);
            j.Key(new KeyCaption(1, S(0.5), "Esc"));
        });
        File.AppendAllText(path, "{\"key\":{\"t\":0.9,\"id\":2,\"te");

        var metadata = MetadataJournal.Read(path, null, []);

        metadata!.Keys.Should().ContainSingle().Which.Text.Should().Be("Esc");
        metadata.Frames.Should().HaveCount(1);
        MetadataJournal.Read(Path.Combine(_folder, "missing.jsonl"), null, []).Should().BeNull();
    }

    [Fact]
    public void The_metadata_rebuilds_the_timeline_on_the_virtual_screen()
    {
        var path = Journal(j =>
        {
            j.Frame(0, new PixelPoint(110, 220), 2, visible: true);
            j.Frame(30, new PixelPoint(300, 400), 3, visible: false);
            j.ButtonDown(S(0.5), PointerButton.Middle, new PixelPoint(150, 250));
            j.ButtonUp(S(0.6), PointerButton.Middle);
            j.Key(new KeyCaption(4, S(0.7), "F5"));
        });

        var timeline = MetadataJournal.Read(path, null, [])!.ToTimeline();

        timeline.Pointer(S(0.5)).Should().Be(new PointerState(new PixelPoint(110, 220), S(0), 2, true));
        timeline.Pointer(S(1)).Should().Be(new PointerState(new PixelPoint(300, 400), S(1), 3, false));
        timeline.Clicks(S(0.55), S(0.3)).Should().ContainSingle().Which.At.Should().Be(new PixelPoint(150, 250));
        timeline.Captions(S(0.8), S(1)).Should().ContainSingle().Which.Text.Should().Be("F5");
    }

    [Fact]
    public void Something_else_is_refused_as_metadata()
    {
        var path = Path.Combine(_folder, "other.json");
        File.WriteAllText(path, """{ "format": "something", "version": 1 }""");
        var load = () => RecordingMetadata.Load(path);
        load.Should().Throw<InvalidDataException>();

        File.WriteAllText(path, "not json");
        load.Should().Throw<InvalidDataException>();
        RecordingMetadata.PathFor(@"D:\Videos\Rec 1.mp4").Should().Be(@"D:\Videos\Rec 1.cgmeta.json");
    }
}

public sealed class OverlaySettingsTests
{
    [Fact]
    public void Defaults_burn_in_clicks_and_keep_metadata_with_keystrokes_off()
    {
        var settings = new OverlaySettings();

        settings.BurnIn.Should().BeTrue();
        settings.WriteMetadata.Should().BeTrue();
        settings.ClickSettings.Enabled.Should().BeTrue();
        settings.KeystrokeSettings.Mode.Should().Be(KeystrokeMode.Off);
        settings.KeystrokeSettings.ExcludedApps.Should().Contain("keepass.exe");
        settings.WebcamSettings.Enabled.Should().BeFalse();
        settings.DrawsLive.Should().BeTrue();
        settings.NeedsInput.Should().BeTrue();
        settings.Should().Be(new OverlaySettings().Normalize(), "normalizing the defaults changes nothing");

        var nothing = new OverlaySettings(WriteMetadata: false, Clicks: new ClickOverlaySettings(Enabled: false));
        (nothing.DrawsLive, nothing.NeedsInput).Should().Be((false, false));
    }

    [Fact]
    public void Odd_values_are_clamped_and_bad_colours_replaced()
    {
        var odd = new OverlaySettings(
            Clicks: new ClickOverlaySettings(LeftColor: "red", RightColor: "#12345678", Size: 500),
            Pointer: new PointerOverlaySettings((PointerEffect)9, Scale: 7, IdleSeconds: double.NaN),
            Keystrokes: new KeystrokeOverlaySettings(FontSize: 2, FadeSeconds: 99, ExcludedApps: [" KeePass.EXE ", "", "keepass.exe", "App.exe"]),
            Webcam: new WebcamSettings(Size: 99, CenterX: -5, BorderColor: "#abc")).Normalize();

        odd.ClickSettings.LeftColor.Should().Be(ClickOverlaySettings.DefaultLeftColor);
        odd.ClickSettings.RightColor.Should().Be("#12345678");
        odd.ClickSettings.Size.Should().Be(ClickOverlaySettings.MaxSize);
        odd.PointerSettings.Effect.Should().Be(PointerEffect.None);
        odd.PointerSettings.Scale.Should().Be(PointerOverlaySettings.MaxScale);
        odd.PointerSettings.IdleSeconds.Should().Be(PointerOverlaySettings.DefaultIdleSeconds);
        odd.KeystrokeSettings.FontSize.Should().Be(10);
        odd.KeystrokeSettings.FadeSeconds.Should().Be(10);
        odd.KeystrokeSettings.ExcludedApps.Should().Equal("app.exe", "keepass.exe");
        odd.KeystrokeSettings.Excludes("KeePass.exe").Should().BeTrue();
        odd.KeystrokeSettings.Excludes(null).Should().BeFalse();
        odd.WebcamSettings.Size.Should().Be(60);
        odd.WebcamSettings.CenterX.Should().Be(0);
        odd.WebcamSettings.BorderColor.Should().Be(WebcamSettings.DefaultBorderColor);
    }

    [Fact]
    public void Colours_parse_with_or_without_alpha()
    {
        OverlaySettings.TryParseColor("#FF8000", out var orange).Should().BeTrue();
        orange.Should().Be(0xFFFF8000);
        OverlaySettings.TryParseColor("#80FF8000", out var half).Should().BeTrue();
        half.Should().Be(0x80FF8000);
        OverlaySettings.TryParseColor("FF8000", out _).Should().BeFalse();
        OverlaySettings.TryParseColor(null, out _).Should().BeFalse();
    }

    [Fact]
    public void Pointer_scale_or_hiding_means_the_app_draws_the_pointer()
    {
        new PointerOverlaySettings().DrawsOwnPointer.Should().BeFalse();
        new PointerOverlaySettings(Scale: 1.5).DrawsOwnPointer.Should().BeTrue();
        new PointerOverlaySettings(HideWhenIdle: true).DrawsOwnPointer.Should().BeTrue();
    }

    [Fact]
    public void Excluded_apps_compare_by_content()
    {
        new KeystrokeOverlaySettings(ExcludedApps: ["a.exe"]).Should().Be(new KeystrokeOverlaySettings(ExcludedApps: ["a.exe"]));
        new KeystrokeOverlaySettings(ExcludedApps: ["a.exe"]).Should().NotBe(new KeystrokeOverlaySettings(ExcludedApps: ["b.exe"]));
    }

    [Fact]
    public void Version_3_show_clicks_moves_to_the_overlays()
    {
        var root = (JsonObject)JsonNode.Parse("""{ "schemaVersion": 3, "recording": { "fps": 60, "showClicks": false } }""")!;

        SettingsMigration.Upgrade(root).Should().Be(3);

        root["recording"]!.AsObject().ContainsKey("showClicks").Should().BeFalse();
        root["recording"]!["fps"]!.GetValue<int>().Should().Be(60);
        root["overlays"]!["clicks"]!["enabled"]!.GetValue<bool>().Should().BeFalse();

        var none = (JsonObject)JsonNode.Parse("""{ "schemaVersion": 3, "recording": { "fps": 60 } }""")!;
        SettingsMigration.Upgrade(none);
        none.ContainsKey("overlays").Should().BeFalse("nothing to move: the defaults apply");
    }
}
