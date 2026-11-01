using System.IO;
using CashGear.Capture.Core.Overlays;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Media.Tests;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Export;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Rendering;
using Microsoft.Extensions.Logging.Abstractions;
using CashGear.Capture.Core.Recording;
using SkiaSharp;

namespace CashGear.Capture.VideoEditor.Tests;

public sealed class LayoutTests(ITestOutputHelper output) : IDisposable
{
    private const uint Red = 0xFFFF0000;
    private const uint Blue = 0xFF0000FF;

    private readonly string _root = Directory.CreateTempSubdirectory("cashgear-layout-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static SolidFrames Frames() => new(clip => clip.Stream == 3 ? (320, 240, Blue) : (1280, 720, Red));

    private static SKBitmap Draw(VideoProject project, double time, IFrameProvider? frames = null)
    {
        var bitmap = new SKBitmap(new SKImageInfo(project.Canvas.Width, project.Canvas.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        new FrameCompositor(frames ?? Frames()).Render(canvas, project, time);
        return bitmap;
    }

    private static VideoProject WithLayout(VideoProject project, LayoutKind kind, double at, double length, double? transition = null)
    {
        var (changed, id) = CanvasEdits.AddLayout(project, kind, at, length);
        return transition is { } time ? changed.WithItem((LayoutItem)changed.Find(id)!.Value.Item with { Transition = time }) : changed;
    }

    [Fact]
    public void Without_a_layout_nothing_changes_and_a_corner_bubble_is_each_pictures_own_place()
    {
        var project = TestProjects.Project(10, webcam: true);
        LayoutEngine.At(project, 1).Should().BeNull();
        LayoutEngine.ScreenCell(project, 1).Should().BeNull();
        using var plain = Draw(project, 1);
        using var bubble = Draw(WithLayout(project, LayoutKind.CornerBubble, 0, 10), 1);
        bubble.Bytes.Should().Equal(plain.Bytes);
    }

    [Fact]
    public void Side_by_side_gives_each_picture_a_part_of_a_wide_canvas()
    {
        var project = WithLayout(TestProjects.Project(10, webcam: true) with { Canvas = new ProjectCanvas(1280, 720, 30, "#00FF00") }, LayoutKind.SideBySide, 0, 10, transition: 0);
        using var bitmap = Draw(project, 1);
        var (screen, camera) = LayoutEngine.Cells(project.Canvas);
        screen.Right.Should().BeLessThan(camera.Left);
        bitmap.GetPixel((int)screen.MidX, (int)screen.MidY).Should().Be(new SKColor(255, 0, 0));
        bitmap.GetPixel((int)camera.MidX, (int)camera.MidY).Should().Be(new SKColor(0, 0, 255));
        bitmap.GetPixel((int)camera.MidX, (int)camera.Top + 12).Should().Be(new SKColor(0, 0, 255), "the camera fills its cell, cut to its shape");
        bitmap.GetPixel((int)((screen.Right + camera.Left) / 2), (int)screen.MidY).Should().Be(new SKColor(0, 255, 0), "the background between them");
    }

    [Fact]
    public void Side_by_side_stacks_on_a_tall_canvas()
    {
        var canvas = new ProjectCanvas(1080, 1920, 30, "#00FF00");
        var (screen, camera) = LayoutEngine.Cells(canvas);
        screen.Bottom.Should().BeLessThan(camera.Top);
        var project = WithLayout(TestProjects.Project(10, webcam: true) with { Canvas = canvas }, LayoutKind.SideBySide, 0, 10, transition: 0);
        using var bitmap = Draw(project, 1);
        bitmap.GetPixel(540, (int)screen.MidY).Should().Be(new SKColor(255, 0, 0));
        bitmap.GetPixel(540, (int)camera.MidY).Should().Be(new SKColor(0, 0, 255));
    }

    [Fact]
    public void Camera_only_and_screen_only_show_one_picture()
    {
        var project = TestProjects.Project(10, webcam: true);
        using (var camera = Draw(WithLayout(project, LayoutKind.CameraOnly, 0, 10, transition: 0), 1))
        {
            camera.GetPixel(4, 4).Should().Be(new SKColor(0, 0, 255), "the camera covers the canvas");
            camera.GetPixel(640, 360).Should().Be(new SKColor(0, 0, 255));
        }

        using var screen = Draw(WithLayout(project, LayoutKind.ScreenOnly, 0, 10, transition: 0), 1);
        var webcam = ProjectFactory.DefaultWebcam;
        screen.GetPixel((int)(webcam.CenterX * 1280), (int)(webcam.CenterY * 720)).Should().Be(new SKColor(255, 0, 0), "no webcam");
    }

    [Fact]
    public void Switching_animates_and_is_the_same_whatever_the_order_of_the_frames()
    {
        var project = WithLayout(TestProjects.Project(10, webcam: true), LayoutKind.CameraOnly, 2, 4);
        var track = project.Tracks.Single(t => t.Kind == TrackKind.Webcam);
        var clip = track.Clips.Single();
        LayoutEngine.At(project, 1.9).Should().BeNull();
        LayoutEngine.At(project, 2).Should().Be(new LayoutMoment(LayoutKind.CornerBubble, LayoutKind.CameraOnly, 0));
        LayoutEngine.At(project, 2.2)!.Value.Amount.Should().BeApproximately(0.5, 1e-6, "halfway through 0.4 s");
        LayoutEngine.At(project, 2.4)!.Value.Amount.Should().BeApproximately(1, 1e-6);

        // The webcam's box grows steadily from its own place to the whole canvas, with no jump.
        var widths = Enumerable.Range(0, 49).Select(n => LayoutEngine.Box(project, track, clip, 2 + (n / 120.0))!.Value.Area.Width).ToList();
        widths[0].Should().BeApproximately(PlacementEdits.Area(project, clip).Width, 1);
        widths[^1].Should().BeApproximately(1280, 1);
        widths.Zip(widths.Skip(1)).Should().OnlyContain(p => p.Second >= p.First - 0.01);
        widths.Zip(widths.Skip(1)).Max(p => p.Second - p.First).Should().BeLessThan(90, "no jump");

        // After its end, back to each picture's own place, as long.
        LayoutEngine.At(project, 6.2)!.Value.Should().Match<LayoutMoment>(m => m.From == LayoutKind.CameraOnly && m.To == LayoutKind.CornerBubble && Math.Abs(m.Amount - 0.5) < 1e-6);
        LayoutEngine.At(project, 6.5).Should().BeNull();

        // From one layout straight into the next.
        var two = WithLayout(project, LayoutKind.SideBySide, 6, 2);
        LayoutEngine.At(two, 6.2)!.Value.Should().Match<LayoutMoment>(m => m.From == LayoutKind.CameraOnly && m.To == LayoutKind.SideBySide && Math.Abs(m.Amount - 0.5) < 1e-6);

        // A hidden track doesn't count.
        var hidden = project.WithTrack(project.Tracks.Single(t => t.Kind == TrackKind.Layout) with { Hidden = true });
        LayoutEngine.At(hidden, 3).Should().BeNull();
    }

    [Fact]
    public void A_layout_is_cut_moved_and_saved_like_any_item()
    {
        var project = WithLayout(TestProjects.Project(10), LayoutKind.SideBySide, 2, 4);
        var item = project.Tracks.Single(t => t.Kind == TrackKind.Layout).Items.OfType<LayoutItem>().Single();
        ((LayoutItem)item.Slice(3, 5)).Should().Match<LayoutItem>(l => l.Start == 3 && l.Length == 2 && l.Transition == 0, "a part from the middle has no change into it");
        ((LayoutItem)item.Slice(0, 3)).Should().Match<LayoutItem>(l => l.Start == 2 && l.Length == 1 && l.Transition == 0.4);
        TrackEdits.Accepts(project, project.Tracks.Single(t => t.Kind == TrackKind.Layout), item).Should().BeTrue();
        TrackEdits.Accepts(project, project.Tracks.First(t => t.Kind == TrackKind.Video), item).Should().BeFalse();

        var cut = TimelineEdits.DeleteRange(project, 3, 4);
        cut.Tracks.Single(t => t.Kind == TrackKind.Layout).Items.Should().HaveCount(2).And.OnlyContain(i => i is LayoutItem);
        ProjectJson.Deserialize(ProjectJson.Serialize(project)).Find(item.Id)!.Value.Item.Should().Be(item);
    }

    [Fact]
    public void A_zoom_in_a_side_by_side_layout_stays_in_the_screens_part()
    {
        var project = WithLayout(TestProjects.Project(10, webcam: true) with { Canvas = new ProjectCanvas(1280, 720, 30, "#00FF00") }, LayoutKind.SideBySide, 0, 10, transition: 0);
        project = AutoZoom.AddZoom(project, new ZoomItem { Id = "z", Start = 0, Length = 10, Scale = 2, Focus = ZoomFocus.Fixed, EaseIn = 0, EaseOut = 0 });
        var (screen, camera) = LayoutEngine.Cells(project.Canvas);
        using var bitmap = Draw(project, 1);
        bitmap.GetPixel((int)((screen.Right + camera.Left) / 2), (int)screen.MidY).Should().Be(new SKColor(0, 255, 0), "the zoomed screen doesn't spill past its part");
        bitmap.GetPixel((int)camera.MidX, (int)camera.MidY).Should().Be(new SKColor(0, 0, 255));
        bitmap.GetPixel((int)screen.MidX, (int)screen.MidY).Should().Be(new SKColor(255, 0, 0));
    }

    [Fact]
    public void The_canvas_shapes_keep_the_shorter_side_and_even_sizes()
    {
        CanvasEdits.SizeOf(CanvasShape.Wide, 1080).Should().Be((1920, 1080));
        CanvasEdits.SizeOf(CanvasShape.Vertical, 1080).Should().Be((1080, 1920));
        CanvasEdits.SizeOf(CanvasShape.Square, 1080).Should().Be((1080, 1080));
        CanvasEdits.SizeOf(CanvasShape.Portrait, 1080).Should().Be((1080, 1350));
        CanvasEdits.SizeOf(CanvasShape.Vertical, 721).Should().Be((720, 1280));
        CanvasEdits.ShapeOf(new ProjectCanvas(1080, 1920, 30)).Should().Be(CanvasShape.Vertical);
        CanvasEdits.ShapeOf(new ProjectCanvas(1280, 720, 30)).Should().Be(CanvasShape.Wide);
        CanvasEdits.ShapeOf(new ProjectCanvas(1000, 1000, 30)).Should().Be(CanvasShape.Square);
        CanvasEdits.ShapeOf(new ProjectCanvas(1280, 1024, 30)).Should().BeNull();
    }

    [Fact]
    public void Changing_the_canvas_keeps_things_where_they_were_and_a_webcam_stacks()
    {
        var project = TestProjects.Project(10, webcam: true) with { Canvas = new ProjectCanvas(1920, 1080, 30) };
        (project, var note) = AnnotationEdits.Add(project, AnnotationShape.Rectangle, 1);
        var before = ((AnnotationItem)project.Find(note)!.Value.Item).Keyframes[0];

        var tall = CanvasEdits.Change(project, 1080, 1920, ScreenFraming.FollowPointer);
        tall.Canvas.Should().Match<ProjectCanvas>(c => c.Width == 1080 && c.Height == 1920 && c.Framing == ScreenFraming.FollowPointer);
        var moved = ((AnnotationItem)tall.Find(note)!.Value.Item).Keyframes[0];
        (moved.X / 1080).Should().BeApproximately(before.X / 1920, 1e-9);
        (moved.Y / 1920).Should().BeApproximately(before.Y / 1080, 1e-9);
        tall.Tracks.Single(t => t.Kind == TrackKind.Layout).Items.Should().ContainSingle()
            .Which.Should().BeOfType<LayoutItem>().Which.Should().Match<LayoutItem>(l => l.Kind == LayoutKind.SideBySide && l.Auto && l.Length == 10);

        var back = CanvasEdits.Change(tall, 1920, 1080, ScreenFraming.Fit);
        back.Tracks.Should().NotContain(t => t.Kind == TrackKind.Layout, "its own layout goes when the canvas goes back");
        ((AnnotationItem)back.Find(note)!.Value.Item).Keyframes[0].X.Should().BeApproximately(before.X, 1e-6);

        // A layout made by hand stays, and none is added beside it.
        var chosen = WithLayout(project, LayoutKind.CameraOnly, 0, 2);
        var kept = CanvasEdits.Change(chosen, 1080, 1920, ScreenFraming.Fill);
        kept.Tracks.Single(t => t.Kind == TrackKind.Layout).Items.Should().ContainSingle().Which.Should().Match<LayoutItem>(l => !l.Auto);

        // Without a webcam, none at all.
        var noWebcam = TestProjects.Project(10) with { Canvas = new ProjectCanvas(1920, 1080, 30) };
        CanvasEdits.Change(noWebcam, 1080, 1920, ScreenFraming.FollowPointer).Tracks.Should().NotContain(t => t.Kind == TrackKind.Layout);
    }

    [Fact]
    public void Fit_leaves_the_background_and_fill_covers_the_canvas()
    {
        var project = TestProjects.Project(10) with { Canvas = new ProjectCanvas(1280, 720, 30, "#00FF00") };
        using (var fit = Draw(CanvasEdits.Change(project, 720, 1280, ScreenFraming.Fit), 1))
        {
            fit.GetPixel(360, 40).Should().Be(new SKColor(0, 255, 0), "bars above and below");
            fit.GetPixel(360, 640).Should().Be(new SKColor(255, 0, 0));
        }

        using var fill = Draw(CanvasEdits.Change(project, 720, 1280, ScreenFraming.Fill), 1);
        fill.GetPixel(360, 4).Should().Be(new SKColor(255, 0, 0), "the picture covers the canvas");
        fill.GetPixel(4, 640).Should().Be(new SKColor(255, 0, 0));
        fill.GetPixel(715, 1275).Should().Be(new SKColor(255, 0, 0));
    }

    /// <summary>A 1920 × 1080 recording's metadata with the pointer at <paramref name="pointer"/>(seconds).</summary>
    private MediaAsset Recording(double seconds, Func<double, (double X, double Y)> pointer, int fps = 30)
    {
        var frames = Enumerable.Range(0, (int)(seconds * fps)).Select(n =>
        {
            var (x, y) = pointer((double)n / fps);
            return new[] { n, (int)Math.Round(x), (int)Math.Round(y), 0, 1 };
        }).ToList();
        var metadata = new RecordingMetadata(
            RecordingMetadata.FormatName, 1, "Rec.mp4", [new MetadataPart("Rec.mp4", 0)], fps, new MetadataArea(0, 0, 1920, 1080), 1,
            CursorInVideo: true, OverlaysInVideo: false, KeystrokeMode.Off, [], frames, [], [], [], []);
        var path = Path.Combine(_root, $"Rec-{Guid.NewGuid():N}.cgmeta.json");
        metadata.Save(path);
        return new MediaAsset("rec", Path.Combine(_root, "Rec.mp4"), seconds, [new AssetStream(0, StreamKind.Video, null, 0, seconds, 1920, 1080, fps)], MetadataPath: path);
    }

    [Fact]
    public void Auto_reframe_keeps_a_pointer_going_about_its_work_in_the_picture_on_every_frame()
    {
        // Big arcs round the whole screen, then quick sweeps of over a thousand pixels a second, stopping between them.
        double Sweep(double t) => 960 + (850 * Math.Sin(t * 1.4));
        var asset = Recording(60, t => t < 30 ? (960 + (880 * Math.Sin(t * 0.5)), 540 + (480 * Math.Sin((t * 0.35) + 1))) : (Sweep(t), 540 + (400 * Math.Sin(t * 0.9))));
        var wide = ProjectFactory.AddRecording(VideoProject.Empty(new ProjectCanvas(1920, 1080, 30)), asset, 0);
        var project = CanvasEdits.Change(wide, 1080, 1920, ScreenFraming.FollowPointer);
        var camera = new ZoomCamera(new MetadataOverlays());

        // It is really zoomed: the view is the picture's height, a third of its width on the canvas.
        var view = camera.View(project, 10);
        (1080 / view.Width).Should().BeApproximately(1920.0 / 607.5, 0.05);
        var misses = camera.PointerOutOfView(project);
        output.WriteLine($"{misses.Count} spans out of the picture: {string.Join(", ", misses.Select(m => $"{m.From:0.00}-{m.To:0.00}"))}");

        // In the picture on every frame of the 1800, except where the path itself jumps from arcs to sweeps at 30 s.
        var jump = misses.Should().ContainSingle("the pointer keeps up everywhere it doesn't teleport").Which;
        jump.From.Should().BeInRange(29.5, 30);
        jump.To.Should().BeInRange(30, 30.5);
    }

    [Fact]
    public void A_pointer_that_moves_too_fast_to_follow_is_reported()
    {
        // Still, then a jump across the whole screen in a frame, then still.
        var asset = Recording(10, t => t < 5 ? (100, 500) : (1800, 500));
        var project = CanvasEdits.Change(ProjectFactory.AddRecording(VideoProject.Empty(new ProjectCanvas(1920, 1080, 30)), asset, 0), 1080, 1920, ScreenFraming.FollowPointer);
        var misses = new ZoomCamera(new MetadataOverlays()).PointerOutOfView(project);
        misses.Should().ContainSingle();
        misses[0].From.Should().BeInRange(4.5, 5.2);
        misses[0].To.Should().BeInRange(5, 6.5, "the view catches up");

        // Without metadata there is nothing to report, and the view stays in the middle.
        var plain = asset with { MetadataPath = null };
        var still = CanvasEdits.Change(ProjectFactory.AddRecording(VideoProject.Empty(new ProjectCanvas(1920, 1080, 30)), plain, 0), 1080, 1920, ScreenFraming.FollowPointer);
        var camera = new ZoomCamera(new MetadataOverlays());
        camera.PointerOutOfView(still).Should().BeEmpty();
        camera.View(still, 5).CenterX.Should().BeApproximately(540, 1);
    }

    [Fact]
    public void Zooms_work_on_top_of_auto_reframe_and_their_scale_is_from_what_it_shows()
    {
        var asset = Recording(10, t => (960, 540));
        var project = CanvasEdits.Change(ProjectFactory.AddRecording(VideoProject.Empty(new ProjectCanvas(1920, 1080, 30)), asset, 0), 1080, 1920, ScreenFraming.Fill);
        var camera = new ZoomCamera(new MetadataOverlays());
        var cover = camera.View(project, 1);
        var zoomed = camera.View(AutoZoom.AddZoom(project, new ZoomItem { Id = "z", Start = 0, Length = 10, Scale = 2, Focus = ZoomFocus.Fixed, EaseIn = 0, EaseOut = 0 }), 1);
        zoomed.Width.Should().BeApproximately(cover.Width / 2, 1e-6);
        zoomed.Height.Should().BeApproximately(cover.Height / 2, 1e-6);
    }

    [Fact]
    public void A_preview_decodes_enough_for_a_screen_that_covers_a_narrow_canvas()
    {
        var stream = new AssetStream(0, StreamKind.Video, null, 0, 10, 1920, 1080, 30);
        Playback.PreviewFrames.DecodeSize(stream, new ProjectCanvas(1080, 1920, 30)).Should().Be((1080, 608), "fitted, as much as it will show");
        Playback.PreviewFrames.DecodeSize(stream, new ProjectCanvas(1080, 1920, 30, null, ScreenFraming.FollowPointer)).Should().Be((1920, 1080), "covering, the whole picture");
    }

    [Theory]
    [InlineData("mp4-9x16", 1080, 1920)]
    [InlineData("mp4-1x1", 1080, 1080)]
    [InlineData("mp4-4x5", 1080, 1350)]
    public void The_format_presets_have_a_canvas_of_their_own(string id, int width, int height)
    {
        var preset = ExportPresets.Find(id)!;
        preset.HasCanvas.Should().BeTrue();
        preset.Size(1920, 1080).Should().Be((width, height));
        preset.Name.Should().NotBeNullOrEmpty();
        var prepared = new ExportRequest(ProjectFactory.AddRecording(VideoProject.Empty(new ProjectCanvas(1920, 1080, 30)), TestProjects.Recording(5), 0), preset, QualityPreset.High, "x.mp4").Prepared();
        prepared.Canvas.Should().Match<ProjectCanvas>(c => c.Width == width && c.Height == height && c.Framing == ScreenFraming.FollowPointer);
    }

    [Fact]
    public async Task A_9_16_export_of_a_16_9_recording_keeps_the_pointer_in_the_picture_and_says_where_it_could_not()
    {
        TestMedia.Require();
        var video = Path.Combine(_root, "Rec.mp4");
        new StampedRecording { Path = video, Seconds = 4, Format = new VideoFormat(640, 360, 30), Tracks = ["Mixed"] }.Write();
        var metadata = new RecordingMetadata(
            RecordingMetadata.FormatName, 1, "Rec.mp4", [new MetadataPart("Rec.mp4", 0)], 30, new MetadataArea(0, 0, 640, 360), 1,
            CursorInVideo: true, OverlaysInVideo: false, KeystrokeMode.Off, [],
            [.. Enumerable.Range(0, 120).Select(n => new[] { n, n < 60 ? 80 : 600, 180, 0, 1 })], [], [], [], []);
        metadata.Save(RecordingMetadata.PathFor(video));
        var asset = ProjectFactory.ReadAsset(video);
        var project = ProjectFactory.FromRecording(asset);
        var path = Path.Combine(_root, "Vertical.mp4");
        var preset = ExportPresets.Vertical;
        var result = await new ProjectExporter(new SoftwareEncoders(), NullLogger<ProjectExporter>.Instance).ExportAsync(new ExportRequest(project, preset, QualityPreset.Small, path), ct: TestContext.Current.CancellationToken);
        File.Exists(path).Should().BeTrue();
        result.PointerOutOfView.Should().ContainSingle("the pointer jumps at 2 s").Which.From.Should().BeInRange(1.5, 2.2);
        using var reader = new CashGear.Capture.Media.MediaReader(path);
        var stream = reader.Streams.First(s => s.IsVideo);
        (stream.Width, stream.Height).Should().Be((1080, 1920));
    }
}
