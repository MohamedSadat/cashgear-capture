using System.Text.Json;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Hotkeys;
using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.Core.Tests.Recording;

public sealed class FramePacerTests
{
    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void Frames_are_due_on_the_frame_rate_grid()
    {
        var pacer = new FramePacer(30);

        pacer.Next(Ms(0)).Should().BeNull("frame 0 is due at once");
        pacer.FrameWritten();
        pacer.Next(Ms(10)).Should().BeCloseTo(Ms(1000.0 / 30 - 10), Ms(0.01), "frame 1 belongs at 33.3 ms");
        pacer.Next(Ms(34)).Should().BeNull();
        pacer.FrameWritten();

        pacer.Written.Should().Be(2);
        pacer.Dropped.Should().Be(0);
    }

    [Fact]
    public void A_short_delay_is_caught_up_by_writing_every_missed_frame()
    {
        var pacer = new FramePacer(30);
        pacer.Next(Ms(0));
        pacer.FrameWritten();

        // 100 ms later frames 1, 2 and 3 are due: within the backlog, so all are written (the same picture repeats).
        var written = 0;
        while (pacer.Next(Ms(100)) is null)
        {
            pacer.FrameWritten();
            written++;
        }

        written.Should().Be(3);
        pacer.Dropped.Should().Be(0);
    }

    [Fact]
    public void Falling_far_behind_skips_frames_and_counts_them_as_dropped()
    {
        var pacer = new FramePacer(30, maxBacklog: 5);
        pacer.Next(Ms(0));
        pacer.FrameWritten();

        pacer.Next(Ms(1000)).Should().BeNull();
        pacer.FrameWritten();

        pacer.Dropped.Should().Be(29, "frames 1–29 were skipped to catch up with frame 30");
        pacer.Next(Ms(1000)).Should().NotBeNull("caught up");
    }

    [Fact]
    public void A_minute_of_steady_writing_produces_exactly_fps_times_seconds_frames()
    {
        var pacer = new FramePacer(30);
        for (var ms = 0.0; ms < 60_000; ms += 7) // the writer wakes every 7 ms or so
        {
            while (pacer.Next(Ms(ms)) is null)
            {
                pacer.FrameWritten();
            }
        }

        pacer.Written.Should().Be(1800);
        pacer.Dropped.Should().Be(0);
    }
}

public sealed class FfmpegArgumentsTests
{
    [Fact]
    public void Raw_bgra_frames_go_in_on_stdin_and_h264_mp4_comes_out()
    {
        var args = FfmpegArguments.Record(1920, 1080, 30, 23, VideoEncoder.X264, @"C:\out\rec.mp4");

        string.Join(' ', args).Should().Be(
            "-hide_banner -nostats -loglevel warning -f rawvideo -pixel_format bgra -video_size 1920x1080 -framerate 30 -i pipe:0 " +
            "-c:v libx264 -preset veryfast -crf 23 -pix_fmt yuv420p -g 60 -movflags +faststart -y C:\\out\\rec.mp4");
    }

    [Theory]
    [InlineData(VideoEncoder.Nvenc, "-c:v h264_nvenc -preset p4 -rc vbr -cq 28 -b:v 0 -pix_fmt yuv420p")]
    [InlineData(VideoEncoder.QuickSync, "-c:v h264_qsv -preset veryfast -global_quality 28 -pix_fmt nv12")]
    [InlineData(VideoEncoder.Amf, "-c:v h264_amf -quality balanced -rc cqp -qp_i 28 -qp_p 28 -pix_fmt yuv420p")]
    public void Each_hardware_encoder_gets_its_own_quality_settings(VideoEncoder encoder, string expected)
    {
        var args = string.Join(' ', FfmpegArguments.Record(640, 480, 60, 28, encoder, "x.mp4"));

        args.Should().Contain(expected);
        args.Should().Contain("-g 120");
    }

    [Fact]
    public void Quality_is_clamped_and_odd_sizes_are_refused()
    {
        string.Join(' ', FfmpegArguments.Record(64, 64, 15, 99, VideoEncoder.X264, "x.mp4")).Should().Contain("-crf 35");

        var odd = () => FfmpegArguments.Record(641, 480, 30, 23, VideoEncoder.X264, "x.mp4");
        odd.Should().Throw<ArgumentException>();
        FfmpegArguments.EvenSize(641, 481).Should().Be((640, 480));
    }

    [Fact]
    public void Sound_comes_in_as_a_second_input_on_a_named_pipe_and_goes_out_as_aac()
    {
        var args = FfmpegArguments.Record(1920, 1080, 30, 23, VideoEncoder.X264, @"C:\out\rec.mp4", @"\\.\pipe\cashgear-rec-1");

        string.Join(' ', args).Should().Be(
            "-hide_banner -nostats -loglevel warning -f rawvideo -pixel_format bgra -video_size 1920x1080 -framerate 30 -i pipe:0 " +
            @"-f f32le -ar 48000 -ch_layout stereo -i \\.\pipe\cashgear-rec-1 -map 0:v:0 -map 1:a:0 " +
            @"-c:v libx264 -preset veryfast -crf 23 -pix_fmt yuv420p -g 60 -c:a aac -b:a 160k -movflags +faststart -y C:\out\rec.mp4");
    }

    [Fact]
    public void Hardware_is_preferred_unless_software_is_chosen()
    {
        FfmpegArguments.Preference(RecordingEncoder.Auto).Should().Equal(VideoEncoder.Nvenc, VideoEncoder.QuickSync, VideoEncoder.Amf, VideoEncoder.X264);
        FfmpegArguments.Preference(RecordingEncoder.Software).Should().Equal(VideoEncoder.X264);
        string.Join(' ', FfmpegArguments.Probe(VideoEncoder.Nvenc)).Should().Contain("-c:v h264_nvenc").And.EndWith("-f null -");
    }
}

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
        new RecordingSettings().Should().Be(new RecordingSettings(30, 23, true, true, RecordingEncoder.Auto, SystemAudio: true, Microphone: null));

        var odd = new RecordingSettings(Fps: 50, Quality: 3, Encoder: (RecordingEncoder)9).Normalize();
        odd.Fps.Should().Be(60, "the nearest supported rate");
        odd.Quality.Should().Be(FfmpegArguments.MinQuality);
        odd.Encoder.Should().Be(RecordingEncoder.Auto);
    }

    [Fact]
    public void A_blank_microphone_means_none_and_an_id_is_trimmed()
    {
        new RecordingSettings(Microphone: "  ").Normalize().Microphone.Should().BeNull();
        new RecordingSettings(Microphone: " {0.0.1.00000000}.{abc} ").Normalize().Microphone.Should().Be("{0.0.1.00000000}.{abc}");
        new RecordingSettings(Microphone: RecordingSettings.DefaultMicrophone).Normalize().Microphone.Should().Be("default");
    }

    [Fact]
    public void A_settings_file_from_before_sound_records_system_audio_and_no_microphone()
    {
        var settings = JsonSerializer.Deserialize("""{ "recording": { "fps": 15, "quality": 25 } }""", SettingsJsonContext.Default.AppSettings)!.Normalize();

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
