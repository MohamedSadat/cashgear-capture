using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Media;
using CashGear.Capture.Media.Tests;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Export;
using CashGear.Capture.VideoEditor.Model;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace CashGear.Capture.VideoEditor.Tests;

/// <summary>Chapters from the markers, the export window's choices as a request, and thumbnails.</summary>
public sealed class SharingExportTests : IDisposable
{
    private readonly TestMediaFiles _media = new("cashgear-sharing-");

    public void Dispose() => _media.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ProjectExporter Exporter() => new(new SoftwareEncoders(), NullLogger<ProjectExporter>.Instance);

    private string Out(string name) => Path.Combine(_media.Root, name);

    private VideoProject Marked(params (double Time, string Name)[] markers)
    {
        var project = ProjectFactory.FromRecording(ProjectFactory.ReadAsset(_media.Stamped("marked.mp4", 12)));
        return project with { Markers = [.. markers.Select((m, i) => new TimelineMarker($"m{i}", m.Time, m.Name))] };
    }

    private static VideoProject Long(params (double Time, string Name)[] markers)
    {
        var project = VideoProject.Empty(new ProjectCanvas(320, 180, 30));
        var clip = new Clip { Id = "c", AssetId = "a", SourceOut = 100 };
        return project with
        {
            Tracks = [new Track("t", TrackKind.Video, "Screen", [clip])],
            Markers = [.. markers.Select((m, i) => new TimelineMarker($"m{i}", m.Time, m.Name))],
        };
    }

    [Fact]
    public void A_project_without_markers_has_no_chapters()
    {
        ProjectChapters.From(Long()).Should().BeEmpty();
        ProjectChapters.WithDescription("Text", []).Should().Be("Text");
        ProjectChapters.YouTubeProblem([]).Should().BeNull();
    }

    [Fact]
    public void Chapters_start_at_zero_and_each_ends_where_the_next_starts()
    {
        var chapters = ProjectChapters.From(Long((20, "One"), (50, "Two")));

        chapters.Select(c => (c.Start, c.End, c.Title)).Should().Equal((0, 20, "Introduction"), (20, 50, "One"), (50, 100, "Two"));
    }

    [Fact]
    public void A_marker_at_zero_is_the_first_chapter_and_markers_past_the_end_or_at_the_same_time_are_left_out()
    {
        var chapters = ProjectChapters.From(Long((0, "Start"), (30, "A"), (30, "B"), (150, "Past")));

        chapters.Select(c => c.Title).Should().Equal("Start", "A");
        chapters[^1].End.Should().Be(100);
    }

    [Fact]
    public void A_marker_without_a_name_gets_a_number()
    {
        ProjectChapters.From(Long((0, " "), (30, "A"), (60, ""))).Select(c => c.Title).Should().Equal("Chapter 1", "A", "Chapter 3");
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(9.999, "0:09")]
    [InlineData(83.5, "1:23")]
    [InlineData(600, "10:00")]
    [InlineData(3723, "1:02:03")]
    public void Times_are_written_as_YouTube_reads_them(double seconds, string written) => ProjectChapters.Stamp(seconds).Should().Be(written);

    [Fact]
    public void A_description_gets_the_times_after_what_it_says()
    {
        var chapters = ProjectChapters.From(Long((0, "مقدمة"), (65, "Part 2")));

        ProjectChapters.Description(chapters).Should().Be("0:00 مقدمة\n1:05 Part 2");
        ProjectChapters.WithDescription("About this.\n", chapters).Should().Be("About this.\n\n0:00 مقدمة\n1:05 Part 2");
        ProjectChapters.WithDescription("", chapters).Should().Be("0:00 مقدمة\n1:05 Part 2");
    }

    [Fact]
    public void What_YouTube_wont_show_is_said()
    {
        ProjectChapters.YouTubeProblem(ProjectChapters.From(Long((0, "a"), (30, "b")))).Should().Contain("at least 3");
        ProjectChapters.YouTubeProblem(ProjectChapters.From(Long((0, "a"), (5, "b"), (30, "c")))).Should().Contain("at least 10 s");
        ProjectChapters.YouTubeProblem(ProjectChapters.From(Long((0, "a"), (30, "b"), (60, "c")))).Should().BeNull();
    }

    [Fact]
    public void The_WebVTT_chapters_file_has_a_cue_per_chapter()
    {
        var path = Out("chapters.vtt");

        ProjectChapters.SaveVtt(path, ProjectChapters.From(Long((10, "الفصل"), (70, "Two"))));

        var text = File.ReadAllText(path);
        text.Should().StartWith("WEBVTT");
        text.Should().Contain("00:00:00.000 --> 00:00:10.000").And.Contain("Introduction");
        text.Should().Contain("00:00:10.000 --> 00:01:10.000").And.Contain("الفصل");
        text.Should().Contain("00:01:10.000 --> 00:01:40.000").And.Contain("Two");
        File.ReadAllBytes(path).Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, "no byte-order mark");
    }

    /// <summary>What ffprobe lists: each chapter's start and title.</summary>
    private static List<(double Start, double End, string Title)> FfprobeChapters(string path)
    {
        var tool = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffprobe.exe");
        Assert.SkipUnless(File.Exists(tool), "ffprobe isn't in tools\\ffmpeg.");
        var start = new ProcessStartInfo(tool, ["-v", "error", "-show_chapters", "-of", "json", path])
        {
            RedirectStandardOutput = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
        };
        using var process = Process.Start(start)!;
        var text = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        using var json = JsonDocument.Parse(text);
        return [.. json.RootElement.GetProperty("chapters").EnumerateArray().Select(c => (
            double.Parse(c.GetProperty("start_time").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
            double.Parse(c.GetProperty("end_time").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
            c.GetProperty("tags").GetProperty("title").GetString()!))];
    }

    public static TheoryData<string> ChapterContainers => new() { "mp4-720", "mkv", "m4a" };

    [Theory]
    [MemberData(nameof(ChapterContainers))]
    public async Task ffprobe_lists_the_chapters_and_a_description_has_the_same_times(string presetId)
    {
        TestMedia.Require();
        var project = Marked((2, "البداية"), (5, "Part 2"), (9, "Part 3"));
        var preset = ExportPresets.Find(presetId)!;
        var path = Out("chapters" + preset.Extension);

        var result = await Exporter().ExportAsync(new ExportRequest(project, preset, QualityPreset.Small, path, Chapters: ExportChapterFiles.InFile), null, Ct);

        var listed = FfprobeChapters(path);
        listed.Select(c => c.Title).Should().Equal("Introduction", "البداية", "Part 2", "Part 3");
        listed.Select(c => c.Start).Should().BeEquivalentTo(new[] { 0.0, 2, 5, 9 }, o => o.WithStrictOrdering().Using<double>(c => c.Subject.Should().BeApproximately(c.Expectation, 0.01)).WhenTypeIs<double>());
        listed[^1].End.Should().BeApproximately(project.Duration, 0.1);

        var description = ProjectChapters.Description(result.Chapters!);
        description.Split('\n').Should().Equal(listed.Select(c => $"{ProjectChapters.Stamp(c.Start)} {c.Title}"), "the same times as the file's");
        result.Chapters.Should().HaveCount(4);
    }

    [Fact]
    public async Task A_WebM_file_says_what_it_keeps()
    {
        TestMedia.Require();
        var project = Marked((0, "One"), (5, "Two"));
        var path = Out("chapters.webm");

        await CashGear.Capture.Tests.MachineTurn.RunAsync(async () => // VP9 works every core
        {
            await Exporter().ExportAsync(new ExportRequest(project, ExportPresets.WebMVp9, QualityPreset.Small, path, Chapters: ExportChapterFiles.InFile), null, Ct);
            return 0;
        });

        FfprobeChapters(path).Select(c => c.Title).Should().Equal("One", "Two");
    }

    [Fact]
    public async Task Without_being_asked_for_there_are_no_chapters_and_an_old_export_is_as_it_was()
    {
        TestMedia.Require();
        var project = Marked((2, "One"));
        var path = Out("plain.mp4");

        var result = await Exporter().ExportAsync(new ExportRequest(project, ExportPresets.Mp4H264Small, QualityPreset.Small, path), null, Ct);

        FfprobeChapters(path).Should().BeEmpty();
        result.Chapters.Should().BeEmpty();
        File.Exists(ProjectChapters.VttPath(path)).Should().BeFalse();
    }

    [Fact]
    public async Task The_chapters_file_is_written_beside_the_export()
    {
        TestMedia.Require();
        var project = Marked((0, "One"), (5, "Two"));
        var path = Out("with file.mp4");

        var result = await Exporter().ExportAsync(new ExportRequest(project, ExportPresets.Mp4H264Small, QualityPreset.Small, path, Chapters: ExportChapterFiles.Vtt), null, Ct);

        result.ChaptersPath.Should().Be(Out("with file.chapters.vtt"));
        File.ReadAllText(result.ChaptersPath!).Should().Contain("00:00:05.000 -->").And.Contain("Two");
        FfprobeChapters(path).Should().BeEmpty("only the file was asked for");
    }

    [Fact]
    public async Task A_marked_range_has_the_chapters_inside_it_on_its_own_clock()
    {
        TestMedia.Require();
        var project = Marked((1, "Before"), (4, "In it"), (10, "After"));
        var path = Out("range.mp4");

        await Exporter().ExportAsync(new ExportRequest(project, ExportPresets.Mp4H264Small, QualityPreset.Small, path, Range: new TimeRange(3, 8), Chapters: ExportChapterFiles.InFile), null, Ct);

        var listed = FfprobeChapters(path);
        listed.Select(c => c.Title).Should().Equal("Introduction", "In it");
        listed[1].Start.Should().BeApproximately(1, 0.01);
    }

    [Fact]
    public void Choices_become_a_request()
    {
        var project = Long((0, "a"));
        var choices = new ExportChoices(ExportPresets.Mp4H264Small.Id, QualityPreset.Balanced, -14, nameof(SoundTracksOut.WavFiles), BurnCaptions: false, Chapters: true, ChaptersVtt: true);

        var (request, note) = choices.ToRequest(project, "x.mp4", "software", new TimeRange(0, 1));

        note.Should().BeNull();
        request.Preset.Should().Be(ExportPresets.Mp4H264Small);
        request.Quality.Should().Be(QualityPreset.Balanced);
        request.LoudnessTarget.Should().Be(-14);
        request.BurnCaptions.Should().BeFalse();
        request.Chapters.Should().Be(ExportChapterFiles.InFile | ExportChapterFiles.Vtt);
        request.EncoderSetting.Should().Be("software");
        request.Range.Should().Be(new TimeRange(0, 1));
        request.Gif.Should().BeNull();
    }

    [Fact]
    public void An_HDR_preset_on_a_project_without_HDR_media_says_it_was_exported_as_SDR()
    {
        var (preset, note) = new ExportChoices(ExportPresets.Mp4Av1Hdr.Id).PresetFor(Long());

        preset.Should().Be(ExportPresets.Mp4Hevc);
        note.Should().Contain("no HDR media");
    }

    [Fact]
    public void What_a_licence_leaves_out_falls_back_and_says_so()
    {
        var (preset, note) = new ExportChoices(ExportPresets.Gif.Id).PresetFor(Long(), new ExportAllowances(Gif: false));
        preset.Should().Be(ExportPresets.Mp4H264Hd);
        note.Should().NotBeNullOrEmpty();

        var (request, _) = new ExportChoices(ExportPresets.Mp4H264Hd.Id, LoudnessLufs: -14).ToRequest(Long(), "x.mp4", null, allowances: new ExportAllowances(SoundPost: false));
        request.LoudnessTarget.Should().BeNull();
    }

    [Fact]
    public void A_file_of_sound_alone_keeps_no_streams_and_only_some_keep_chapters()
    {
        var (flac, _) = new ExportChoices(ExportPresets.Flac.Id).ToRequest(Long((0, "a")), "x.flac", null);
        flac.Chapters.Should().Be(ExportChapterFiles.None);
        flac.TracksOut.Should().Be(SoundTracksOut.Mix);

        var (gif, _) = new ExportChoices(ExportPresets.Gif.Id, GifFps: 12, GifMaxMegabytes: 2).ToRequest(Long(), "x.gif", null);
        gif.Gif.Should().Be(new GifOptions(12, 800, MaxBytes: 2 * 1024 * 1024));
    }

    [Fact]
    public void The_built_in_presets_are_what_their_names_say()
    {
        var all = BuiltInExports.All;
        all.Select(e => e.Id).Should().OnlyHaveUniqueItems();
        ExportPresets.Find(all.Single(e => e.Name == "YouTube 1080p").Choices.Preset).Should().Be(ExportPresets.Mp4H264Hd);
        ExportPresets.Find(all.Single(e => e.Name == "Shorts 9:16").Choices.Preset).Should().Be(ExportPresets.Vertical);
        ExportPresets.Find(all.Single(e => e.Name == "Docs GIF").Choices.Preset).Should().Be(ExportPresets.Gif);
    }

    private static TitleTemplate Title() => BuiltInTemplates.All.First(t => t.Kind == TemplateKind.Title);

    private static byte[] Pixels(SKBitmap bitmap) => bitmap.GetPixelSpan().ToArray();

    [Fact]
    public void A_thumbnail_is_the_frame_asked_for_at_the_size_YouTube_asks_for()
    {
        TestMedia.Require();
        var project = ProjectFactory.FromRecording(ProjectFactory.ReadAsset(_media.Stamped("thumb.mp4", 3)));
        var maker = new ThumbnailMaker();

        using var at = maker.Render(new ThumbnailRequest(project, 1));
        using var later = maker.Render(new ThumbnailRequest(project, 2.5));

        (at.Width, at.Height).Should().Be((1280, (int)Math.Round(1280.0 * project.Canvas.Height / project.Canvas.Width)));
        StampedRecording.ReadStamp(Pixels(at), at.Width, at.Width / 320.0).Should().Be(project.Canvas.Fps);
        StampedRecording.ReadStamp(Pixels(later), later.Width, later.Width / 320.0).Should().Be((int)Math.Round(2.5 * project.Canvas.Fps));
    }

    [Fact]
    public void A_tall_project_keeps_its_shape()
    {
        var project = VideoProject.Empty(new ProjectCanvas(1080, 1920, 30));

        using var picture = new ThumbnailMaker().Render(new ThumbnailRequest(project with { Tracks = [] }, 0));

        (picture.Width, picture.Height).Should().Be((720, 1280));
    }

    [Fact]
    public void A_title_from_the_templates_is_drawn_settled_with_what_its_fields_say()
    {
        TestMedia.Require();
        var project = ProjectFactory.FromRecording(ProjectFactory.ReadAsset(_media.Stamped("thumb.mp4", 3)));
        var maker = new ThumbnailMaker();
        var template = Title();
        var field = template.Fields![0];

        using var plain = maker.Render(new ThumbnailRequest(project, 1));
        using var titled = maker.Render(new ThumbnailRequest(project, 1, template, new Dictionary<string, string> { [field.Key] = "عنوان الفيديو" }));
        using var other = maker.Render(new ThumbnailRequest(project, 1, template, new Dictionary<string, string> { [field.Key] = "Another" }));
        using var early = maker.Render(new ThumbnailRequest(project, 0, template, new Dictionary<string, string> { [field.Key] = "عنوان الفيديو" }));

        Pixels(titled).Should().NotEqual(Pixels(plain));
        Pixels(titled).Should().NotEqual(Pixels(other), "the field's text shows");
        Different(early, maker.Render(new ThumbnailRequest(project, 0))).Should().BeGreaterThan(1000, "a title shows on the very first frame too");
        project.Tracks.Should().NotContain(t => t.Items.OfType<TemplateItem>().Any(), "the project stays as it was");
    }

    private static int Different(SKBitmap a, SKBitmap b)
    {
        using (b)
        {
            var (x, y) = (Pixels(a), Pixels(b));
            return Enumerable.Range(0, x.Length).Count(i => x[i] != y[i]);
        }
    }

    [Fact]
    public void Captions_arent_in_a_thumbnail()
    {
        var project = Long();
        var captions = new Track("caps", TrackKind.Caption, "Captions", [new CaptionItem { Id = "c1", Start = 0, Length = 100, Text = "Hello" }]);

        var prepared = ThumbnailMaker.Prepared(new ThumbnailRequest(project with { Tracks = [.. project.Tracks, captions] }, 5), out var time);

        time.Should().Be(5);
        prepared.Tracks.Single(t => t.Kind == TrackKind.Caption).Hidden.Should().BeTrue();
    }

    [Fact]
    public void A_time_past_the_end_is_the_last_frame()
    {
        ThumbnailMaker.Prepared(new ThumbnailRequest(Long(), 500), out var time);

        time.Should().BeApproximately(100 - (1.0 / 30), 1e-6);
    }

    [Fact]
    public void A_thumbnail_saves_as_PNG_and_for_YouTube_stays_under_its_limit()
    {
        using var small = new SKBitmap(1280, 720);
        small.Erase(SKColors.SteelBlue);
        var pngPath = Out("thumb.png");
        ThumbnailMaker.SavePng(small, pngPath);
        using (var read = SKBitmap.Decode(pngPath))
        {
            (read.Width, read.Height).Should().Be((1280, 720));
        }

        ThumbnailMaker.SaveForYouTube(small, Out("small")).Should().EndWith(".png");

        // Noise compresses badly: a PNG of it is about 3.6 MB.
        using var noisy = new SKBitmap(1280, 720);
        var random = new Random(25);
        for (var y = 0; y < 720; y++)
        {
            for (var x = 0; x < 1280; x++)
            {
                noisy.SetPixel(x, y, new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
            }
        }

        var path = ThumbnailMaker.SaveForYouTube(noisy, Out("noisy"));

        path.Should().EndWith(".jpg");
        new FileInfo(path).Length.Should().BeLessThanOrEqualTo(ThumbnailMaker.YouTubeLimit);
    }
}
