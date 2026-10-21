using System.IO;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Editor.Model;
using CashGear.Capture.Editor.Rendering;
using CashGear.Capture.Media;
using CashGear.Capture.Media.Tests;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Export;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Rendering;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace CashGear.Capture.VideoEditor.Tests;

public sealed class AnnotationTests : IDisposable
{
    private readonly TestMediaFiles _media = new("cashgear-annotations-");

    public void Dispose() => _media.Dispose();

    /// <summary>An annotation from 2 s to 6 s moving right by 100 px between its keyframes at 1 s and 3 s.</summary>
    private static AnnotationItem Moving(Easing easing = Easing.Linear) => new()
    {
        Id = "a",
        Start = 2,
        Length = 4,
        Shape = AnnotationShape.Rectangle,
        Easing = easing,
        Keyframes = [new(1, 100, 50, 40, 20), new(3, 200, 50, 40, 20)],
        FadeIn = 0.5,
        FadeOut = 1,
    };

    private static SKBitmap Render(VideoProject project, double time, uint background = 0xFF000000)
    {
        var bitmap = new SKBitmap(new SKImageInfo(project.Canvas.Width, project.Canvas.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        new FrameCompositor(new SolidFrames(_ => (project.Canvas.Width, project.Canvas.Height, background))).Render(canvas, project, time);
        return bitmap;
    }

    /// <summary>A 320 × 180 project: a 10 s recording, and the annotation on a track above it.</summary>
    private static VideoProject With(AnnotationItem item)
    {
        var asset = TestProjects.Recording(10) with { Streams = [new AssetStream(0, StreamKind.Video, null, 0, 10, 320, 180, 30)] };
        var project = ProjectFactory.AddRecording(VideoProject.Empty(new ProjectCanvas(320, 180, 30)), asset, 0);
        return project with { Tracks = [.. project.Tracks, new Track("notes", TrackKind.Annotation, "Annotations", [item])] };
    }

    [Fact]
    public void Keyframes_move_it_evenly_or_smoothly_and_hold_outside_them()
    {
        var item = Moving();
        item.At(2.5).X.Should().Be(100, "before the first keyframe it holds");
        item.At(4).X.Should().Be(150, "halfway, evenly");
        item.At(5.5).X.Should().Be(200);
        Moving(Easing.Smooth).At(3.5).X.Should().BeApproximately(115.625, 1e-9, "a quarter of the way, smoothly: 0.15625 of it");
        Moving(Easing.Smooth).At(4).X.Should().BeApproximately(150, 1e-9);
        item.Fade(2.25).Should().Be(0.5);
        item.Fade(5.5).Should().Be(0.5);
    }

    [Fact]
    public void Placing_it_moves_the_keyframe_there_or_adds_one()
    {
        var still = Moving() with { Keyframes = [new(0, 10, 10, 40, 20)] };
        still.PlacedAt(4, new(0, 50, 60, 40, 20), 30).Keyframes.Should().Equal([new AnnotationKeyframe(0, 50, 60, 40, 20)], "one keyframe: it doesn't move, it is moved");

        var moving = Moving();
        moving.PlacedAt(3, new(0, 120, 70, 40, 20), 30).Keyframes.Should().Equal(new AnnotationKeyframe(1, 120, 70, 40, 20), new AnnotationKeyframe(3, 200, 50, 40, 20));
        moving.PlacedAt(3.01, new(0, 120, 70, 40, 20), 30).Keyframes[0].X.Should().Be(120, "within half a frame of a keyframe");
        moving.PlacedAt(4, new(0, 0, 0, 40, 20), 30).Keyframes.Select(k => k.Time).Should().Equal(1, 2, 3);

        var added = moving.WithKeyframeAt(4, 30);
        added.Keyframes.Should().HaveCount(3);
        added.At(4).X.Should().Be(150, "a keyframe where it is changes nothing");
        added.WithoutKeyframeAt(4, 30).Keyframes.Should().Equal(moving.Keyframes);
        (moving with { Keyframes = [moving.Keyframes[0]] }).WithoutKeyframeAt(3, 30).Keyframes.Should().HaveCount(1, "the last keyframe stays");
    }

    [Fact]
    public void Splitting_a_moving_annotation_keeps_its_movement_on_both_sides()
    {
        var item = Moving();
        var left = (AnnotationItem)item.Slice(2, 4.5);
        var right = (AnnotationItem)item.Slice(4.5, 6);
        foreach (var time in new[] { 2.2, 3.5, 4.4 })
        {
            left.At(time).Should().Be(item.At(time) with { Time = time - 2 });
        }

        foreach (var time in new[] { 4.5, 5, 5.9 })
        {
            right.At(time).X.Should().BeApproximately(item.At(time).X, 1e-9);
        }

        (left.FadeIn, left.FadeOut, right.FadeIn, right.FadeOut).Should().Be((0.5, 0, 0, 1));
        var longer = (AnnotationItem)item.Lengthen(ItemEdge.Start, 1);
        (longer.Start, longer.Length).Should().Be((1, 5));
        longer.At(4).Should().Be(item.At(4) with { Time = 3 }, "lengthening at the start doesn't move it");

        var project = TimelineEdits.DeleteRange(With(item), 0, 1);
        ((AnnotationItem)project.Tracks[^1].Items.Single()).Start.Should().Be(1, "annotations move with a cut before them");
    }

    [Fact]
    public void New_annotations_go_on_an_annotation_track_with_room_on_top()
    {
        var project = TestProjects.Project(30);
        var (first, a) = AnnotationEdits.Add(project, AnnotationShape.Text, 2);
        first.Tracks[^1].Kind.Should().Be(TrackKind.Annotation);
        first.Tracks[^1].Name.Should().Be("Annotations");
        var text = (AnnotationItem)first.Find(a)!.Value.Item;
        (text.Start, text.Length, text.Text, text.Look.Color, text.Look.FontSize).Should().Be((2, 3, "Text", "#FFFFFF", 48));

        var (second, b) = AnnotationEdits.Add(first, AnnotationShape.Arrow, 3, 1);
        second.Tracks.Count(t => t.Kind == TrackKind.Annotation).Should().Be(2, "the first track is taken from 2 to 5 s");
        second.Find(b)!.Value.Track.Name.Should().Be("Annotations 2");
        var (third, c) = AnnotationEdits.Add(second, AnnotationShape.Blur, 10);
        third.Find(c)!.Value.Track.Name.Should().Be("Annotations", "free again after 5 s");
        ProjectJson.Deserialize(ProjectJson.Serialize(third)).Tracks[^1].Items.OfType<AnnotationItem>().Single().Shape.Should().Be(AnnotationShape.Arrow);
    }

    [Fact]
    public void Shapes_are_drawn_where_their_keyframes_put_them()
    {
        var box = new AnnotationItem { Id = "box", Start = 0, Length = 10, Shape = AnnotationShape.Rectangle, Look = new AnnotationLook(Color: "#00FF00", StrokeWidth: 4, Fill: "#0000FF"), Keyframes = [new(0, 20, 20, 100, 60), new(10, 120, 20, 100, 60)] };
        using (var start = Render(With(box), 0))
        {
            start.GetPixel(70, 50).Should().Be(new SKColor(0, 0, 255), "filled");
            start.GetPixel(20, 50).Green.Should().BeGreaterThan(200, "outlined");
            start.GetPixel(170, 50).Should().Be(new SKColor(0, 0, 0));
        }

        using (var later = Render(With(box), 5))
        {
            later.GetPixel(120, 50).Should().Be(new SKColor(0, 0, 255), "halfway it has moved 50 px");
            later.GetPixel(60, 50).Should().Be(new SKColor(0, 0, 0));
        }

        using var hidden = Render(With(box) is var p ? p.WithTrack(p.Tracks[^1] with { Hidden = true }) : p, 0);
        hidden.GetPixel(70, 50).Should().Be(new SKColor(0, 0, 0));
    }

    [Fact]
    public void Text_is_drawn_like_the_image_editor_draws_it_arabic_included()
    {
        foreach (var words in new[] { "Hello", "مرحبا بالعالم 123" })
        {
            var text = new AnnotationItem { Id = "t", Start = 0, Length = 5, Shape = AnnotationShape.Text, Text = words, Look = new AnnotationLook(Color: "#FFFFFF", Fill: "#FF202020", FontSize: 28), Keyframes = [new(0, 30, 40, 0, 0)] };
            using var video = Render(With(text), 1);

            // The same text drawn by the image editor on the same background.
            using var expected = new SKBitmap(new SKImageInfo(320, 180, SKColorType.Bgra8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(expected))
            {
                canvas.Clear(SKColors.Black);
                var style = new AnnotationStyle { Color = SKColors.White, Fill = new SKColor(0x20, 0x20, 0x20), FontSize = 28 };
                using var effects = new EffectCache();
                using var blank = SKImage.Create(new SKImageInfo(1, 1));
                new TextAnnotation(new AnnotationId(1), style, new SKPoint(30, 40), words).Render(canvas, new RenderContext(blank, effects, new SKSamplingOptions(SKFilterMode.Linear)));
            }

            video.Bytes.Should().Equal(expected.Bytes, $"\"{words}\" looks the same in a video as in a capture");
            video.Pixels.Count(p => p.Red > 200).Should().BeGreaterThan(50, "there is ink");
        }
    }

    [Fact]
    public void Blur_and_pixelate_hide_what_is_under_them_and_nothing_else()
    {
        // Red left, blue right: a blur across the edge mixes them; a pixelate cell is one colour.
        var project = With(new AnnotationItem { Id = "b", Start = 0, Length = 5, Shape = AnnotationShape.Blur, Look = new AnnotationLook(Strength: 8), Keyframes = [new(0, 140, 60, 40, 40)] });
        var frames = new SplitFrames();
        using var bitmap = new SKBitmap(new SKImageInfo(320, 180, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            new FrameCompositor(frames).Render(canvas, project, 1);
        }

        var middle = bitmap.GetPixel(160, 80);
        middle.Red.Should().BeInRange(80, 175);
        middle.Blue.Should().BeInRange(80, 175);
        bitmap.GetPixel(130, 80).Should().Be(new SKColor(255, 0, 0), "outside the area nothing changes");
        bitmap.GetPixel(141, 80).Red.Should().BeGreaterThan(200, "the edge of the area isn't darkened");

        var pixelated = project.WithItem(new AnnotationItem { Id = "b", Start = 0, Length = 5, Shape = AnnotationShape.Pixelate, Look = new AnnotationLook(Strength: 40), Keyframes = [new(0, 140, 60, 40, 40)] });
        using var cells = new SKBitmap(new SKImageInfo(320, 180, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(cells))
        {
            new FrameCompositor(frames).Render(canvas, pixelated, 1);
        }

        cells.GetPixel(141, 61).Should().Be(cells.GetPixel(178, 98), "one cell of 40 px, one colour");
        cells.GetPixel(141, 61).Red.Should().BeInRange(120, 135);
    }

    [Fact]
    public void Annotations_are_picked_on_the_preview_by_their_body_grips_and_line_ends()
    {
        var arrow = new AnnotationItem { Id = "arrow", Start = 0, Length = 5, Shape = AnnotationShape.Arrow, Keyframes = [new(0, 50, 100, 100, -50)] };
        var box = new AnnotationItem { Id = "box", Start = 0, Length = 5, Shape = AnnotationShape.Ellipse, Keyframes = [new(0, 200, 20, 80, 80)] };
        var project = With(arrow);
        project = project.WithTrack(project.Tracks[^1].WithItems([arrow, box]));
        PlacementEdits.HitTest(project, 1, new SKPoint(100, 75)).Should().Be(new PreviewHit("arrow"), "on the shaft");
        PlacementEdits.HitTest(project, 1, new SKPoint(150, 50)).Should().Be(new PreviewHit("arrow", PreviewGrip.End));
        PlacementEdits.HitTest(project, 1, new SKPoint(52, 98)).Should().Be(new PreviewHit("arrow", PreviewGrip.Start));
        PlacementEdits.HitTest(project, 1, new SKPoint(240, 60)).Should().Be(new PreviewHit("box"));
        PlacementEdits.HitTest(project, 1, new SKPoint(279, 99)).Should().Be(new PreviewHit("box", PreviewGrip.Corner));
        PlacementEdits.HitTest(project, 1, new SKPoint(100, 150))!.Value.ItemId.Should().NotBe("arrow", "away from the line: the video under it");

        AnnotationEdits.Drag(arrow, 1, PreviewGrip.End, new SKPoint(150, 50), new SKPoint(170, 40), 30).Keyframes.Single()
            .Should().Be(new AnnotationKeyframe(0, 50, 100, 120, -60));
        AnnotationEdits.Drag(arrow, 1, PreviewGrip.Start, new SKPoint(50, 100), new SKPoint(60, 110), 30).Keyframes.Single()
            .Should().Be(new AnnotationKeyframe(0, 60, 110, 90, -60), "the end stays where it was");
        AnnotationEdits.Drag(box, 1, PreviewGrip.Corner, new SKPoint(280, 100), new SKPoint(300, 90), 30).Keyframes.Single()
            .Should().Be(new AnnotationKeyframe(0, 200, 20, 100, 70));
        AnnotationEdits.Drag(box, 1, PreviewGrip.None, new SKPoint(240, 60), new SKPoint(200, 70), 30).Keyframes.Single()
            .Should().Be(new AnnotationKeyframe(0, 160, 30, 80, 80));
    }

    [Fact]
    public async Task Annotations_are_in_the_export_as_in_the_preview()
    {
        TestMedia.Require();
        var file = _media.Stamped("notes.mp4", 2);
        var project = ProjectFactory.FromRecording(ProjectFactory.ReadAsset(file));
        project = project with
        {
            Tracks =
            [
                .. project.Tracks,
                new Track("notes", TrackKind.Annotation, "Annotations",
                [
                    new AnnotationItem { Id = "box", Start = 0.5, Length = 1, Shape = AnnotationShape.Rectangle, Look = new AnnotationLook(Color: "#00FF00", Fill: "#00FF00"), Keyframes = [new(0, 100, 80, 80, 60)] },
                ]),
            ],
        };
        var output = Path.Combine(_media.Root, "notes-out.mp4");
        await new ProjectExporter(new SoftwareEncoders(), NullLogger<ProjectExporter>.Instance)
            .ExportAsync(new ExportRequest(project, ExportPresets.Mp4H264Project, QualityPreset.High, output), ct: TestContext.Current.CancellationToken);

        using var reader = new VideoStreamReader(output, decoding: VideoDecoding.Software);
        var pixels = new byte[320 * 180 * 4];
        reader.Seek(1);
        reader.Read(pixels, 320, 180);
        var green = new SKColor(pixels[((110 * 320) + 140) * 4 + 2], pixels[(((110 * 320) + 140) * 4) + 1], pixels[((110 * 320) + 140) * 4]);
        green.Green.Should().BeGreaterThan(220);
        green.Red.Should().BeLessThan(40);
        reader.Seek(0.2);
        reader.Read(pixels, 320, 180);
        pixels[(((110 * 320) + 140) * 4) + 1].Should().BeLessThan(100, "not yet shown at 0.2 s");
        StampedRecording.ReadStamp(pixels, 320).Should().Be(6, "the recording is under it");
    }

    /// <summary>Red on the left half, blue on the right.</summary>
    private sealed class SplitFrames : IFrameProvider
    {
        public Playback.VideoFrame? Frame(Clip clip, MediaAsset asset, double sourceTime)
        {
            var frame = Playback.FramePool.Shared.Rent(320, 180);
            var pixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(frame.Pixels);
            for (var y = 0; y < 180; y++)
            {
                pixels.Slice(y * 320, 160).Fill(0xFFFF0000);
                pixels.Slice((y * 320) + 160, 160).Fill(0xFF0000FF);
            }

            return frame;
        }
    }
}
