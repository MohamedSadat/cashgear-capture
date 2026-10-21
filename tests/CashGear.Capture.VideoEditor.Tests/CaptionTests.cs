using System.IO;
using System.Text;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Transcripts;
using CashGear.Capture.Editor.Model;
using CashGear.Capture.Editor.Rendering;
using CashGear.Capture.Media.Tests;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Export;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Rendering;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace CashGear.Capture.VideoEditor.Tests;

public sealed class CaptionTests : IDisposable
{
    private const string Arabic = "مرحبا بكم في هذا الدرس القصير عن تسجيل الشاشة وتحرير الفيديو";

    private readonly TestMediaFiles _media = new("cashgear-captions-");

    public void Dispose() => _media.Dispose();

    /// <summary>Words of a transcript, each 0.3 s long with 0.1 s between, from <paramref name="start"/>.</summary>
    private static TranscriptSegment Spoken(double start, string text)
    {
        var words = text.Split(' ').Select((w, i) => new TranscriptWord(start + (i * 0.4), start + (i * 0.4) + 0.3, " " + w)).ToList();
        return new TranscriptSegment(start, words[^1].End, text, words);
    }

    [Fact]
    public void Srt_and_vtt_files_keep_times_text_and_arabic()
    {
        SubtitleCue[] cues =
        [
            new(0.5, 2.25, "Hello, world"),
            new(3725.004, 3727.9, $"{Arabic}\nسطر ثان"),
            new(3728, 3729, "Tom & <Jerry>"),
        ];
        var srt = SubtitleFiles.Write(cues, SubtitleFormat.Srt);
        srt.Should().StartWith("1\n00:00:00,500 --> 00:00:02,250\nHello, world\n\n2\n01:02:05,004 --> 01:02:07,900\n");
        var vtt = SubtitleFiles.Write(cues, SubtitleFormat.Vtt);
        vtt.Should().StartWith("WEBVTT\n\n00:00:00.500 --> 00:00:02.250\nHello, world\n\n").And.Contain("Tom &amp; &lt;Jerry&gt;");
        SubtitleFiles.Read(srt).Should().Equal(cues);
        SubtitleFiles.Read(vtt).Should().Equal(cues);

        // Saved: SRT with a byte-order mark, VTT without; both read back.
        var srtPath = Path.Combine(_media.Root, "a.srt");
        var vttPath = Path.Combine(_media.Root, "a.vtt");
        SubtitleFiles.Save(srtPath, cues, SubtitleFormat.Srt);
        SubtitleFiles.Save(vttPath, cues, SubtitleFormat.Vtt);
        File.ReadAllBytes(srtPath).Take(3).Should().Equal(0xEF, 0xBB, 0xBF);
        File.ReadAllBytes(vttPath)[0].Should().Be((byte)'W');
        SubtitleFiles.Load(srtPath).Should().Equal(cues);

        // What others write: CRLF, cue ids and settings, notes, tags, minutes without hours.
        var other = "WEBVTT\r\n\r\nNOTE made elsewhere\r\n\r\nintro\r\n01:02.500 --> 01:04.000 align:start\r\n<v Sam>Hi <b>there</b>\r\n\r\n";
        SubtitleFiles.Read(other).Should().Equal(new SubtitleCue(62.5, 64, "Hi there"));
        FluentActions.Invoking(() => SubtitleFiles.Read("nothing here")).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Speech_becomes_short_captions_cut_between_words_and_at_sentence_ends()
    {
        var long1 = string.Join(' ', Enumerable.Range(1, 30).Select(i => $"word{i}"));
        var captions = CaptionEdits.FromTranscript([Spoken(1, long1), Spoken(20, "First sentence is here. Second one.")], offset: 10);
        captions.Should().OnlyContain(c => c.Text.Length <= 84 && c.Length <= 7.0001);
        captions.Take(3).Select(c => c.Text).Should().Equal(
            string.Join(' ', Enumerable.Range(1, 13).Select(i => $"word{i}")),
            string.Join(' ', Enumerable.Range(14, 12).Select(i => $"word{i}")),
            string.Join(' ', Enumerable.Range(26, 5).Select(i => $"word{i}")));
        captions[0].Start.Should().Be(11, "the offset moves them along the timeline");
        captions[1].Start.Should().BeApproximately(11 + (13 * 0.4), 1e-6, "from its first word");
        captions[0].Words![1].Should().Be(new CaptionWord(0.4, 0.7, " word2"));
        captions.Zip(captions.Skip(1)).Should().OnlyContain(p => p.First.End <= p.Second.Start + 1e-9);
        captions.Skip(3).Select(c => c.Text).Should().Equal("First sentence is here.", "Second one.");

        // A piece of a word (no space before it) stays with it; no word times: cut at spaces, time shared by letters.
        var pieces = CaptionEdits.FromTranscript([new TranscriptSegment(0, 1, "transcription", [new(0, 0.5, " transcr"), new(0.5, 1, "iption")])]);
        pieces.Single().Text.Should().Be("transcription");
        var plain = CaptionEdits.FromTranscript([new TranscriptSegment(0, 10, string.Join(' ', Enumerable.Repeat("abcdefghi", 16)))]);
        plain.Select(c => c.Text.Length).Should().Equal(79, 79);
        plain[1].Start.Should().BeApproximately(5, 1e-6);

        // A short word on its own still lasts long enough to read, up to the next caption.
        var quick = CaptionEdits.FromTranscript([new TranscriptSegment(0, 0.2, "Hi"), new TranscriptSegment(0.5, 1.5, "there")]);
        quick.Select(c => (c.Start, c.End)).Should().Equal((0, 0.5), (0.5, 1.5));
    }

    [Fact]
    public void Captions_split_and_merge_by_their_words_and_survive_cuts()
    {
        var spoken = CaptionEdits.FromTranscript([Spoken(0, "one two three four five six")]).Single();
        var (project, trackId) = CaptionEdits.AddTrack(TestProjects.Project(10), [spoken]);
        project.Track(trackId)!.Should().Match<Track>(t => t.Kind == TrackKind.Caption && t.Name == "Captions");

        var (split, secondId) = CaptionEdits.Split(project, spoken.Id, 1.15);
        var first = (CaptionItem)split.Find(spoken.Id)!.Value.Item;
        var second = (CaptionItem)split.Find(secondId!)!.Value.Item;
        (first.Text, second.Text).Should().Be(("one two three", "four five six"));
        second.Words![0].Should().Be(new CaptionWord(0.05, 0.35, " four"));
        CaptionEdits.MergeWithNext(split, spoken.Id).Find(spoken.Id)!.Value.Item.Should().BeEquivalentTo(spoken with { Length = spoken.Length });

        // Written by hand: split at the space nearest the time's share of the letters, merged with a space.
        var (typed, id) = CaptionEdits.Add(TestProjects.Project(10), 0, 4, "aaaa bbbb cccc dddd");
        var (halves, other) = CaptionEdits.Split(typed, id, 2);
        ((CaptionItem)halves.Find(id)!.Value.Item).Text.Should().Be("aaaa bbbb");
        ((CaptionItem)halves.Find(other!)!.Value.Item).Text.Should().Be("cccc dddd");
        ((CaptionItem)CaptionEdits.MergeWithNext(halves, id).Find(id)!.Value.Item).Should().Match<CaptionItem>(c => c.Text == "aaaa bbbb cccc dddd" && c.Length == 4);

        // A cut through the caption: each side keeps the words said on it.
        var cut = TimelineEdits.DeleteRange(project, 0.75, 1.55);
        cut.Tracks.Single(t => t.Kind == TrackKind.Caption).Items.Cast<CaptionItem>().Select(c => c.Text).Should().Equal("one two", "five six");
        ProjectJson.Deserialize(ProjectJson.Serialize(cut)).Should().BeEquivalentTo(cut);
    }

    private static SKBitmap Render(VideoProject project, double time)
    {
        var canvas = project.Canvas;
        var bitmap = new SKBitmap(new SKImageInfo(canvas.Width, canvas.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var surface = new SKCanvas(bitmap);
        new FrameCompositor(new SolidFrames(_ => (canvas.Width, canvas.Height, 0xFF000000))).Render(surface, project, time);
        return bitmap;
    }

    [Fact]
    public void Burned_in_captions_are_the_image_editors_text_wrapped_centred_and_only_while_they_last()
    {
        var size = new ProjectCanvas(1280, 720, 30);
        var style = new CaptionStyle();
        var (project, _) = CaptionEdits.Add(TestProjects.Project(10), 1, 2, "The quick brown fox jumps over the lazy dog and keeps running far beyond the hills");
        var lines = CaptionRenderer.Lines(((CaptionItem)project.Tracks[^1].Items[0]).Text, style, size);
        lines.Should().HaveCount(2);
        string.Join(' ', lines).Should().Be("The quick brown fox jumps over the lazy dog and keeps running far beyond the hills");

        // The same pixels as the image editor's text with the same look, line by line, centred above the margin.
        var layout = new CaptionRenderer().Layout(string.Join(' ', lines), style, size);
        using var expected = new SKBitmap(new SKImageInfo(1280, 720, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(expected))
        {
            canvas.Clear(SKColors.Black);
            foreach (var line in layout)
            {
                var text = new TextAnnotation(new AnnotationId(0), CaptionRenderer.TextStyle(style, 720), line.Position, line.Text);
                text.Render(canvas, new RenderContext(SKImage.Create(new SKImageInfo(1, 1)), new EffectCache(), new SKSamplingOptions(SKFilterMode.Linear)));
            }
        }

        layout.Should().OnlyContain(l => Math.Abs(l.Position.X + (l.Bounds.Width / 2) - 640) <= 1);
        layout[^1].Bounds.Bottom.Should().BeApproximately(720 - (720 * 0.06f), 1);
        using (var drawn = Render(project, 1.5))
        {
            drawn.Bytes.Should().Equal(expected.Bytes);
        }

        using (var before = Render(project, 0.9))
        {
            before.Pixels.Should().OnlyContain(p => p == new SKColor(0, 0, 0), "not shown before its start");
        }

        var hidden = project.WithTrack(project.Tracks[^1] with { Hidden = true });
        using var none = Render(hidden, 1.5);
        none.Pixels.Should().OnlyContain(p => p == new SKColor(0, 0, 0), "a hidden caption track isn't burned in");
    }

    [Fact]
    public void Arabic_captions_wrap_in_reading_order_and_each_line_reads_right_to_left()
    {
        var size = new ProjectCanvas(1280, 720, 30);
        var style = new CaptionStyle(MaxWidth: 0.4);
        var lines = CaptionRenderer.Lines(Arabic, style, size);
        lines.Should().HaveCountGreaterThan(1);
        string.Join(' ', lines).Should().Be(Arabic, "the words keep their order, the first ones on the first line");
        var look = CaptionRenderer.TextStyle(style, 720);
        lines.Should().OnlyContain(l => TextLayout.Measure(l, look).RightToLeft);
        lines.Should().OnlyContain(l => TextLayout.Measure(l, look).Size.Width - (2 * TextLayout.Measure(l, look).Padding) <= 1280 * 0.4);
    }

    [Fact]
    public async Task A_marked_range_exports_alone_with_its_captions_burned_in_or_in_files()
    {
        TestMedia.Require();
        var file = _media.Stamped("range.mp4", 4);
        var recording = ProjectFactory.FromRecording(ProjectFactory.ReadAsset(file));
        var (project, _) = CaptionEdits.Add(recording, 1.5, 1, "Hello");
        var output = Path.Combine(_media.Root, "range.mp4");
        var exporter = new ProjectExporter(new SoftwareEncoders(), NullLogger<ProjectExporter>.Instance);
        var result = await exporter.ExportAsync(
            new ExportRequest(project, ExportPresets.Mp4H264Project, QualityPreset.High, output, EncoderPresets.Software, new TimeRange(1, 3), BurnCaptions: false, CaptionFiles.Srt | CaptionFiles.Vtt),
            ct: TestContext.Current.CancellationToken);

        result.Frames.Should().Be(60);
        var exported = ExportedFile.Read(output);
        exported.Stamps.Should().Equal(Enumerable.Range(30, 60), "source frames 1 s to 3 s");
        exported.Flashes.Should().Equal([45], "the flash at 2.5 s is 1.5 s in");
        result.CaptionPaths.Should().Equal(Path.ChangeExtension(output, ".srt"), Path.ChangeExtension(output, ".vtt"));
        SubtitleFiles.Load(result.CaptionPaths![0]).Should().Equal(new SubtitleCue(0.5, 1.5, "Hello"));
        File.ReadAllText(result.CaptionPaths[1], Encoding.UTF8).Should().StartWith("WEBVTT");
    }
}
