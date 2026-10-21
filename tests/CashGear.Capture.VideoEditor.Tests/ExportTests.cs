using System.Diagnostics;
using System.IO;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Media;
using CashGear.Capture.Media.Tests;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Export;
using CashGear.Capture.VideoEditor.Model;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.VideoEditor.Tests;

/// <summary>Encoder detection for the tests: what the build has, without probing the graphics card's encoders.</summary>
internal sealed class SoftwareEncoders : IRecordingEncoders
{
    public string? Problem => null;

    public string? FfmpegVersion => "test";

    public IReadOnlyList<EncoderProbe>? Detected { get; } = [new(EncoderPresets.WindowsSoftwareH264.Id, true), new(EncoderPresets.OpenH264.Id, true)];

    public Task<IReadOnlyList<EncoderProbe>> DetectAsync(bool force = false, CancellationToken ct = default) => Task.FromResult(Detected!);
}

/// <summary>What an exported file holds, read back frame by frame.</summary>
internal sealed record ExportedFile(IReadOnlyList<int> Stamps, IReadOnlyList<int> Flashes, IReadOnlyList<double> Beeps, double Duration, int Width, int Height)
{
    public static ExportedFile Read(string path, bool video = true)
    {
        var stamps = new List<int>();
        var flashes = new List<int>();
        var width = 0;
        var height = 0;
        if (video)
        {
            using var reader = new VideoStreamReader(path, decoding: VideoDecoding.Software);
            (width, height) = (reader.Width, reader.Height);
            var pixels = new byte[width * height * 4];
            var wasLight = false;
            while (reader.Read(pixels, width, height) is not null)
            {
                stamps.Add(StampedRecording.ReadStamp(pixels, width, width / 320.0));
                var light = StampedRecording.Brightness(pixels, width, height) > 128;
                if (light && !wasLight)
                {
                    flashes.Add(stamps.Count - 1);
                }

                wasLight = light;
            }
        }

        var beeps = new List<double>();
        using (var sound = new AudioStreamReader(path))
        {
            sound.Seek(0);
            var block = new float[2 * 48_000];
            long at = 0;
            var quietSince = -100_000L;
            int read;
            while ((read = sound.Read(block)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    if (Math.Abs(block[2 * i]) >= 0.1)
                    {
                        if (at + i - quietSince > 4800)
                        {
                            beeps.Add((at + i) / 48_000.0);
                        }

                        quietSince = at + i;
                    }
                }

                at += read;
            }
        }

        using var media = new MediaReader(path);
        return new ExportedFile(stamps, flashes, beeps, media.Duration, width, height);
    }
}

public sealed class ExportTests : IDisposable
{
    private readonly TestMediaFiles _media = new("cashgear-export-");

    public void Dispose() => _media.Dispose();

    private static ProjectExporter Exporter() => new(new SoftwareEncoders(), NullLogger<ProjectExporter>.Instance);

    /// <summary>The source picture each output frame should show: the screen clip under its time.</summary>
    private static List<int> Expected(VideoProject project)
    {
        var fps = project.Canvas.Fps;
        var screen = project.Tracks.Single(t => t.Kind == TrackKind.Video);
        var count = (int)Math.Ceiling((project.Duration * fps) - 1e-6);
        return [.. Enumerable.Range(0, count).Select(n =>
        {
            var time = (double)n / fps;
            var clip = screen.Clips.Single(c => c.Covers(time));
            return (int)Math.Floor((clip.SourceAt(time) * fps) + 1e-6);
        })];
    }

    [Fact]
    public async Task An_export_shows_exactly_the_frames_left_after_cuts_with_the_sound_in_step()
    {
        TestMedia.Require();
        var file = _media.Stamped("cuts.mp4", 8);
        var project = ProjectFactory.FromRecording(ProjectFactory.ReadAsset(file));
        project = TimelineEdits.DeleteRange(TimelineEdits.DeleteRange(project, 1, 2), 2, 3.5);
        project.Duration.Should().BeApproximately(5.5, 1e-6);

        var output = Path.Combine(_media.Root, "cut.mp4");
        var reports = new List<ExportProgress>();
        var result = await Exporter().ExportAsync(
            new ExportRequest(project, ExportPresets.Mp4H264Project, QualityPreset.High, output, EncoderPresets.Software),
            new SyncProgress<ExportProgress>(reports.Add),
            TestContext.Current.CancellationToken);

        result.Frames.Should().Be(165);
        result.Encoder.Should().Be(EncoderPresets.WindowsSoftwareH264.DisplayName);
        File.Exists(output + ".partial").Should().BeFalse();
        reports.Should().NotBeEmpty();
        reports[^1].Fraction.Should().Be(1);

        var exported = ExportedFile.Read(output);
        exported.Duration.Should().BeApproximately(5.5, 0.04);
        exported.Stamps.Should().Equal(Expected(project), "every frame is the source frame the timeline shows then");

        // Flashes with beeps at source 0.5, 2.5, 4.5 and 6.5 s are at 0.5, 1.5, 2 and 4 s now.
        exported.Flashes.Should().Equal(15, 45, 60, 120);
        exported.Beeps.Should().HaveCount(4);
        exported.Beeps.Zip(exported.Flashes, (beep, flash) => beep - (flash / 30.0)).Should().OnlyContain(d => Math.Abs(d) < 1 / 30.0);
    }

    public static TheoryData<string, string?, string, int, int> Presets => new()
    {
        { "mp4-720", "h264", "aac", 1280, 720 },
        { "mp4", "h264", "aac", 320, 180 },
        { "mp4-hevc", "hevc", "aac", 320, 176 },
        { "webm-vp9", "vp9", "opus", 320, 180 },
        { "webm-av1", "av1", "opus", 320, 180 },
        { "m4a", null, "aac", 0, 0 },
        { "mp3", null, "mp3", 0, 0 },
    };

    [Theory]
    [MemberData(nameof(Presets))]
    public async Task Every_preset_makes_its_kind_of_file(string id, string? video, string sound, int width, int height)
    {
        TestMedia.Require();
        var file = _media.Stamped("presets.mp4", 2);
        var project = ProjectFactory.FromRecording(ProjectFactory.ReadAsset(file));
        var preset = ExportPresets.Find(id)!;
        var output = Path.Combine(_media.Root, "preset-" + id + preset.Extension);
        await Exporter().ExportAsync(new ExportRequest(project, preset, QualityPreset.Balanced, output), ct: TestContext.Current.CancellationToken);

        using var reader = new MediaReader(output);
        reader.Duration.Should().BeApproximately(2, 0.1);
        reader.Streams.Where(s => s.IsVideo).Select(s => s.Codec).Should().Equal(video is null ? [] : [video]);
        reader.Streams.Where(s => !s.IsVideo).Select(s => s.Codec).Should().Equal(sound);
        if (video is not null)
        {
            (reader.Video.Width, reader.Video.Height).Should().Be((width, height));
        }
    }

    [Fact]
    public async Task Cancelling_leaves_no_file()
    {
        TestMedia.Require();
        var file = _media.Stamped("cancel.mp4", 8);
        var project = ProjectFactory.FromRecording(ProjectFactory.ReadAsset(file));
        var output = Path.Combine(_media.Root, "cancelled.mp4");
        using var cancel = new CancellationTokenSource();
        var act = () => Exporter().ExportAsync(
            new ExportRequest(project, ExportPresets.Mp4H264Project, QualityPreset.Balanced, output),
            new SyncProgress<ExportProgress>(p =>
            {
                if (p.Fraction > 0.2)
                {
                    cancel.Cancel();
                }
            }),
            cancel.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        File.Exists(output).Should().BeFalse();
        File.Exists(output + ".partial").Should().BeFalse();
    }

    [Fact]
    public async Task An_empty_timeline_says_there_is_nothing_to_export()
    {
        var project = VideoProject.Empty(new ProjectCanvas(320, 180, 30));
        var act = () => Exporter().ExportAsync(new ExportRequest(project, ExportPresets.Mp4H264Project, QualityPreset.Balanced, Path.Combine(_media.Root, "none.mp4")));
        await act.Should().ThrowAsync<ExportException>().WithMessage("*empty*");
    }

    /// <summary>
    /// Phase 16 acceptance (M1): cutting 3 segments out of a 20-minute recording and exporting it gives the right
    /// duration and no A/V desync. The recording has a flash with a beep every 2 s and every picture numbered.
    /// </summary>
    [Fact]
    public async Task Acceptance_three_cuts_out_of_a_20_minute_recording_keep_the_duration_and_the_sync()
    {
        TestMedia.Require();
        var watch = Stopwatch.StartNew();
        var file = _media.Stamped("twenty.mp4", 20 * 60);
        var made = watch.Elapsed;
        var project = ProjectFactory.FromRecording(ProjectFactory.ReadAsset(file));
        project = TimelineEdits.DeleteRange(project, 900, 1000);
        project = TimelineEdits.DeleteRange(project, 500, 510.5);
        project = TimelineEdits.DeleteRange(project, 100, 160);
        var expectedDuration = 1200 - 100 - 10.5 - 60;
        project.Duration.Should().BeApproximately(expectedDuration, 1e-6);

        var output = Path.Combine(_media.Root, "twenty-cut.mp4");
        watch.Restart();
        var result = await Exporter().ExportAsync(new ExportRequest(project, ExportPresets.Mp4H264Project, QualityPreset.High, output, EncoderPresets.Software), ct: TestContext.Current.CancellationToken);
        var exportTime = watch.Elapsed;
        watch.Restart();
        var exported = ExportedFile.Read(output);
        TestContext.Current.TestOutputHelper?.WriteLine($"20-minute acceptance: made in {made.TotalSeconds:0} s, exported {result.Frames} frames in {exportTime.TotalSeconds:0} s, checked in {watch.Elapsed.TotalSeconds:0} s");

        exported.Duration.Should().BeApproximately(expectedDuration, 1 / 30.0, "the cuts take exactly their length out");
        result.Frames.Should().Be((long)Math.Round(expectedDuration * 30));
        exported.Stamps.Should().Equal(Expected(project), "every frame is the source frame the timeline shows then");

        // Every flash has its beep within a frame, all the way through (no drift, and none at the cuts).
        exported.Flashes.Count.Should().BeGreaterThan(500);
        exported.Beeps.Count.Should().Be(exported.Flashes.Count);
        var offsets = exported.Beeps.Zip(exported.Flashes, (beep, flash) => beep - (flash / 30.0)).ToList();
        offsets.Should().OnlyContain(d => Math.Abs(d) < 1 / 30.0);
        TestContext.Current.TestOutputHelper?.WriteLine($"Flash to beep: {offsets.Min() * 1000:0.0} to {offsets.Max() * 1000:0.0} ms over {offsets.Count} pairs");
    }
}

/// <summary>An IProgress that reports on the reporting thread (Progress&lt;T&gt; posts to the thread pool).</summary>
internal sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
