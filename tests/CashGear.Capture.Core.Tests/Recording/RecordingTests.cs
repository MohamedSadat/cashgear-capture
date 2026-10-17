using System.Text.Json;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Hotkeys;
using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.Core.Tests.Recording;

public sealed class ClickHighlighterTests
{
    private const int Size = 100;

    private static byte[] White() => Enumerable.Repeat((byte)255, Size * Size * 4).ToArray();

    private static (byte B, byte G, byte R) Pixel(byte[] bgra, int x, int y)
    {
        var i = ((y * Size) + x) * 4;
        return (bgra[i], bgra[i + 1], bgra[i + 2]);
    }

    [Fact]
    public void A_held_left_button_draws_a_yellow_circle_under_the_pointer()
    {
        var highlighter = new ClickHighlighter();
        var frame = White();

        highlighter.Update(TimeSpan.Zero, new PixelPoint(1050, 2050), leftDown: true, leftClicked: true, rightDown: false, rightClicked: false);
        highlighter.Draw(frame, Size, Size, origin: new PixelPoint(1000, 2000), TimeSpan.Zero);

        var centre = Pixel(frame, 50, 50);
        centre.B.Should().BeLessThan(255, "yellow takes blue away");
        centre.R.Should().Be(255);
        Pixel(frame, 50, 50 + 21).B.Should().BeLessThan(centre.B, "the ring is stronger than the fill");
        Pixel(frame, 5, 5).Should().Be(((byte)255, (byte)255, (byte)255), "outside the circle nothing changes");
    }

    [Fact]
    public void After_release_the_circle_fades_out()
    {
        var highlighter = new ClickHighlighter();
        var at = new PixelPoint(50, 50);
        highlighter.Update(TimeSpan.Zero, at, true, true, false, false);
        highlighter.Update(TimeSpan.FromMilliseconds(100), at, false, false, false, false);

        highlighter.IsActive(TimeSpan.FromMilliseconds(200)).Should().BeTrue();
        highlighter.IsActive(TimeSpan.FromMilliseconds(100) + ClickHighlighter.FadeOut).Should().BeFalse();
    }

    [Fact]
    public void A_click_shorter_than_a_frame_still_shows()
    {
        var highlighter = new ClickHighlighter();

        highlighter.Update(TimeSpan.Zero, new PixelPoint(50, 50), leftDown: false, leftClicked: true, rightDown: false, rightClicked: false);

        highlighter.IsActive(TimeSpan.FromMilliseconds(30)).Should().BeTrue();
    }

    [Fact]
    public void The_right_button_is_red_and_circles_are_clipped_at_the_edges()
    {
        var highlighter = new ClickHighlighter();
        var frame = White();

        highlighter.Update(TimeSpan.Zero, new PixelPoint(2, 2), false, false, rightDown: true, rightClicked: true);
        highlighter.Draw(frame, Size, Size, default, TimeSpan.Zero);

        var corner = Pixel(frame, 2, 2);
        corner.G.Should().BeLessThan(255);
        corner.R.Should().Be(255);
    }
}

public sealed class RecordingSettingsTests
{
    [Fact]
    public void Defaults_and_normalizing()
    {
        new RecordingSettings().Should().Be(new RecordingSettings(
            30, QualityPreset.Balanced, true, true, "auto", SystemAudio: true, Microphone: null, Tracks: AudioTrackLayout.Mixed, SystemAudioGain: 1, MicrophoneGain: 1));

        var odd = new RecordingSettings(
            Fps: 50, Quality: (QualityPreset)9, Encoder: "  H264_MF_HW ", Tracks: (AudioTrackLayout)7, SystemAudioGain: 9, MicrophoneGain: float.NaN).Normalize();
        odd.Fps.Should().Be(60, "the nearest supported rate");
        odd.Quality.Should().Be(QualityPreset.Balanced);
        odd.Encoder.Should().Be("h264_mf_hw");
        odd.Tracks.Should().Be(AudioTrackLayout.Mixed);
        odd.SystemAudioGain.Should().Be(RecordingSettings.MaxGain);
        odd.MicrophoneGain.Should().Be(1);
        new RecordingSettings(Encoder: " ").Normalize().Encoder.Should().Be("auto");
    }

    [Fact]
    public void A_blank_microphone_means_none_and_an_id_is_trimmed()
    {
        new RecordingSettings(Microphone: "  ").Normalize().Microphone.Should().BeNull();
        new RecordingSettings(Microphone: " {0.0.1.00000000}.{abc} ").Normalize().Microphone.Should().Be("{0.0.1.00000000}.{abc}");
        new RecordingSettings(Microphone: RecordingSettings.DefaultMicrophone).Normalize().Microphone.Should().Be("default");
    }

    [Fact]
    public void The_output_device_and_one_app_are_kept_and_an_app_scope_without_an_app_is_all_sound()
    {
        var settings = new RecordingSettings(
            SystemAudioDevice: " {0.0.0.00000000}.{out} ", SystemAudioScope: SystemAudioScope.OnlyApp, SystemAudioApp: " Spotify.EXE ", SystemAudioAppName: " Spotify ").Normalize();

        settings.SystemAudioDevice.Should().Be("{0.0.0.00000000}.{out}");
        settings.SystemAudioScope.Should().Be(SystemAudioScope.OnlyApp);
        settings.SystemAudioApp.Should().Be("spotify.exe");
        settings.SystemAudioAppName.Should().Be("Spotify");
        new RecordingSettings(SystemAudioScope: SystemAudioScope.AllExceptApp, SystemAudioApp: " ").Normalize().SystemAudioScope.Should().Be(SystemAudioScope.All);
        new RecordingSettings(SystemAudioScope: (SystemAudioScope)9, SystemAudioApp: "a.exe").Normalize().SystemAudioScope.Should().Be(SystemAudioScope.All);
        new RecordingSettings(SystemAudioDevice: "").Normalize().SystemAudioDevice.Should().BeNull();

        var text = JsonSerializer.Serialize(new AppSettings { Recording = settings }, SettingsJsonContext.Default.AppSettings);
        text.Should().Contain("\"systemAudioScope\": \"OnlyApp\"").And.Contain("\"systemAudioApp\": \"spotify.exe\"");
        JsonSerializer.Deserialize(text, SettingsJsonContext.Default.AppSettings)!.Normalize().Recording.Should().Be(settings);
    }

    [Theory]
    [InlineData(15, QualityPreset.Lossless)]
    [InlineData(20, QualityPreset.High)]
    [InlineData(23, QualityPreset.Balanced)]
    [InlineData(30, QualityPreset.Small)]
    public void Version_2_quality_numbers_and_encoders_carry_over(int quality, QualityPreset preset)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(
            $$"""{ "schemaVersion": 2, "recording": { "fps": 60, "Quality": {{quality}}, "encoder": "Software", "systemAudio": false } }""")!.AsObject();

        SettingsMigration.Upgrade(json).Should().Be(2);
        var settings = json.Deserialize(SettingsJsonContext.Default.AppSettings)!.Normalize();

        settings.Recording.Quality.Should().Be(preset);
        settings.Recording.Encoder.Should().Be("software");
        settings.Recording.Fps.Should().Be(60);
        settings.Recording.SystemAudio.Should().BeFalse();
        settings.Recording.Tracks.Should().Be(AudioTrackLayout.Mixed);
        settings.SchemaVersion.Should().Be(3);
    }

    [Fact]
    public void Version_3_settings_round_trip_with_readable_values()
    {
        var settings = new AppSettings { Recording = new RecordingSettings(Quality: QualityPreset.High, Encoder: "h264_mf_hw", Tracks: AudioTrackLayout.Separate, MicrophoneGain: 0.5f) };

        var text = JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings);
        text.Should().Contain("\"quality\": \"High\"").And.Contain("\"encoder\": \"h264_mf_hw\"").And.Contain("\"tracks\": \"Separate\"");
        var back = JsonSerializer.Deserialize(text, SettingsJsonContext.Default.AppSettings)!.Normalize();

        back.Recording.Should().Be(settings.Recording);
    }

    [Fact]
    public void A_settings_file_from_before_sound_records_system_audio_and_no_microphone()
    {
        var settings = JsonSerializer.Deserialize("""{ "recording": { "fps": 15, "quality": "Small" } }""", SettingsJsonContext.Default.AppSettings)!.Normalize();

        settings.Recording.Fps.Should().Be(15);
        settings.Recording.SystemAudio.Should().BeTrue();
        settings.Recording.Microphone.Should().BeNull();

        var saved = JsonSerializer.Serialize(settings with { Recording = settings.Recording with { SystemAudio = false, Microphone = "default" } }, SettingsJsonContext.Default.AppSettings);
        var back = JsonSerializer.Deserialize(saved, SettingsJsonContext.Default.AppSettings)!.Normalize();
        back.Recording.SystemAudio.Should().BeFalse();
        back.Recording.Microphone.Should().Be("default");
    }

    [Fact]
    public void Recording_has_a_hotkey_and_a_file_name_kind()
    {
        new HotkeySettings().For(HotkeyAction.Record).Should().Be("Shift+PrintScreen");
        FileNameTemplate.Expand(
                FileNameTemplate.Default,
                new FileNameContext(new DateTimeOffset(2026, 3, 7, 9, 5, 4, TimeSpan.Zero), CaptureKind.Recording, null, null),
                null)
            .Should().Be("Capture_2026-03-07_09-05-04_recording");
    }
}
