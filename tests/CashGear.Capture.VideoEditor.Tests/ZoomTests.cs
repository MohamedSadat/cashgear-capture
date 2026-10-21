using System.IO;
using System.Runtime.InteropServices;
using CashGear.Capture.Core.Overlays;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Playback;
using CashGear.Capture.VideoEditor.Rendering;
using SkiaSharp;

namespace CashGear.Capture.VideoEditor.Tests;

public sealed class ZoomTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cashgear-zoom-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>A recording's metadata: the pointer at <paramref name="pointer"/>(frame) on every frame, and the clicks and keys given.</summary>
    private string Metadata(int width, int height, int fps, int frames, Func<int, (int X, int Y)> pointer, IReadOnlyList<MetadataClick>? clicks = null, IReadOnlyList<MetadataKey>? keys = null)
    {
        var metadata = new RecordingMetadata(
            RecordingMetadata.FormatName, 1, "Rec.mp4", [new MetadataPart("Rec.mp4", 0)], fps, new MetadataArea(0, 0, width, height), 1,
            CursorInVideo: true, OverlaysInVideo: false, KeystrokeMode.Shortcuts, [],
            [.. Enumerable.Range(0, frames).Select(n => pointer(n) is var (x, y) ? new[] { n, x, y, 0, 1 } : null!)],
            clicks ?? [], keys ?? [], [], []);
        var path = Path.Combine(_root, $"Rec-{Guid.NewGuid():N}.cgmeta.json");
        metadata.Save(path);
        return path;
    }

    private static MediaAsset Asset(double seconds, int width, int height, int fps, string? metadata = null) =>
        new("rec", @"C:\nowhere\Rec.mp4", seconds, [new AssetStream(0, StreamKind.Video, null, 0, seconds, width, height, fps)], MetadataPath: metadata);

    private static VideoProject Project(MediaAsset asset, ProjectCanvas canvas) => ProjectFactory.AddRecording(VideoProject.Empty(canvas), asset, 0);

    private static VideoProject WithZoom(VideoProject project, ZoomItem zoom) => AutoZoom.AddZoom(project, zoom);

    private static ZoomItem Zoom(double start, double length, double scale = 2, ZoomFocus focus = ZoomFocus.Fixed, double x = 0.5, double y = 0.5) =>
        new() { Id = Ids.New(), Start = start, Length = length, Scale = scale, Focus = focus, CenterX = x, CenterY = y };

    private static void Near(ZoomView view, double x, double y, double width, double precision = 1e-6)
    {
        view.X.Should().BeApproximately(x, precision);
        view.Y.Should().BeApproximately(y, precision);
        view.Width.Should().BeApproximately(width, precision);
    }

    [Fact]
    public void A_zoom_is_saved_and_its_slices_keep_their_eases_only_on_their_own_ends()
    {
        var zoom = Zoom(1, 4, 2.5, ZoomFocus.FollowPointer, 0.3, 0.7) with { EaseIn = 0.5, EaseOut = 0.8, Auto = true };
        var project = WithZoom(TestProjects.Project(10), zoom);
        var back = ProjectJson.Deserialize(ProjectJson.Serialize(project));
        back.Find(zoom.Id)!.Value.Item.Should().Be(zoom);
        back.Tracks[^1].Should().Match<Track>(t => t.Kind == TrackKind.Effect && t.Name == "Zoom");

        var middle = (ZoomItem)zoom.Slice(2, 3);
        (middle.Start, middle.Length, middle.EaseIn, middle.EaseOut).Should().Be((2, 1, 0, 0));
        var head = (ZoomItem)zoom.Slice(0, 3);
        (head.Start, head.Length, head.EaseIn, head.EaseOut).Should().Be((1, 2, 0.5, 0));
        ((ZoomItem)zoom.Lengthen(ItemEdge.Start, 0.5)).Should().Match<ZoomItem>(z => z.Start == 0.5 && z.Length == 4.5);
        (zoom with { Length = 0.65 }).Eases.Should().Be((0.25, 0.4), "eases longer than the zoom shrink in proportion");
    }

    [Fact]
    public void The_camera_eases_in_holds_and_eases_out_about_one_point()
    {
        var project = WithZoom(TestProjects.Project(10), Zoom(1, 3, x: 0.25, y: 0.25));
        var camera = new ZoomCamera(new MetadataOverlays());
        Near(camera.View(project, 0.99), 0, 0, 1280);
        Near(camera.View(project, 1.0), 0, 0, 1280);
        Near(camera.View(project, 1.6), 0, 0, 640, 1e-3);
        Near(camera.View(project, 2.5), 0, 0, 640);
        Near(camera.View(project, 1.3), 0, 0, 1280 * Math.Pow(0.5, ZoomCamera.Ease(0.5)), 1e-6);
        Near(camera.View(project, 3.7), 0, 0, 1280 * Math.Pow(0.5, 1 - ZoomCamera.Ease(0.5)), 1e-6);
        Near(camera.View(project, 4.0), 0, 0, 1280);

        // In the middle: a zoom about the centre keeps the centre where it is all along.
        var centred = WithZoom(TestProjects.Project(10), Zoom(1, 3));
        foreach (var time in new[] { 1.1, 1.3, 1.5, 2.0, 3.5 })
        {
            var view = camera.View(centred, time);
            view.CenterX.Should().BeApproximately(640, 1e-9);
            view.CenterY.Should().BeApproximately(360, 1e-9);
        }

        ZoomCamera.Ease(0).Should().Be(0);
        ZoomCamera.Ease(1).Should().Be(1);
        ZoomCamera.Ease(0.5).Should().Be(0.5);
        camera.View(TestProjects.Project(10), 1).Matrix(new ProjectCanvas(1280, 720, 30)).IsIdentity.Should().BeTrue("no zoom, no change");
    }

    [Fact]
    public void The_view_never_leaves_the_screens_picture()
    {
        var camera = new ZoomCamera(new MetadataOverlays());
        var corner = WithZoom(TestProjects.Project(10), Zoom(0, 3, x: 0, y: 1));
        Near(camera.View(corner, 1), 0, 360, 640);

        // A 4:3 recording on a 16:9 canvas is 960 px wide in the middle: the view stays on it, not the bars beside it.
        var narrow = WithZoom(Project(Asset(10, 960, 720, 30), new ProjectCanvas(1280, 720, 30)), Zoom(0, 3, x: 0, y: 0.5));
        Near(camera.View(narrow, 1), 160, 180, 640);
        var right = WithZoom(Project(Asset(10, 960, 720, 30), new ProjectCanvas(1280, 720, 30)), Zoom(0, 3, scale: 1.25, x: 1, y: 0.5));
        camera.View(right, 1).Should().Match<ZoomView>(v => Math.Abs(v.X - 128) < 1e-9 && Math.Abs(v.Width - 1024) < 1e-9, "wider than the picture: centred on it");
    }

    [Fact]
    public void Zooms_close_together_pan_from_one_to_the_next_without_zooming_out()
    {
        var project = WithZoom(WithZoom(TestProjects.Project(10), Zoom(1, 2, x: 0.25)), Zoom(3.5, 2, x: 0.75));
        ZoomCamera.Chains(ZoomCamera.Zooms(project)).Should().ContainSingle("0.5 s apart, less than their eases");
        var camera = new ZoomCamera(new MetadataOverlays());
        var views = Enumerable.Range(0, 200).Select(n => camera.View(project, 1.6 + (n * 0.0165))).ToList();
        views.Should().OnlyContain(v => Math.Abs(v.Width - 640) < 1e-6, "zoomed in all the way between them");
        views.Zip(views.Skip(1)).Should().OnlyContain(p => p.Second.X >= p.First.X - 1e-9, "a pan one way");
        Near(views[0], 0, 180, 640);
        Near(camera.View(project, 4.2), 640, 180, 640);

        var apart = WithZoom(WithZoom(TestProjects.Project(10), Zoom(1, 2)), Zoom(5, 2));
        ZoomCamera.Chains(ZoomCamera.Zooms(apart)).Should().HaveCount(2);
        Near(camera.View(apart, 4), 0, 0, 1280);
    }

    [Fact]
    public void Following_the_pointer_settles_on_it_smoothly_and_is_the_same_whatever_the_order_of_the_frames()
    {
        // The pointer stands at (200, 200) for 2 s, jumps to (1000, 500) and stays there.
        var metadata = Metadata(1280, 720, 30, 300, n => n < 60 ? (200, 200) : (1000, 500));
        var project = WithZoom(Project(Asset(10, 1280, 720, 30, metadata), new ProjectCanvas(1280, 720, 60)), Zoom(0.5, 8, focus: ZoomFocus.FollowPointer));
        var times = Enumerable.Range(0, 600).Select(n => n / 60.0).ToList();
        var inOrder = new ZoomCamera(new MetadataOverlays());
        var views = times.Select(t => inOrder.View(project, t)).ToList();
        var shuffled = new ZoomCamera(new MetadataOverlays());
        foreach (var n in Enumerable.Range(0, 600).OrderBy(n => (n * 7919) % 600))
        {
            shuffled.View(project, times[n]).Should().Be(views[n], "seeking gives the same camera as playing");
        }

        var settled = inOrder.View(project, 6);
        settled.Width.Should().BeApproximately(640, 1e-6);
        Math.Abs(settled.CenterX - 1000).Should().BeLessThanOrEqualTo((640 * ZoomCamera.DeadZone) + 1, "the pointer in the middle of the view");
        settled.Should().Match<ZoomView>(v => v.X >= 0 && v.Y >= 0 && v.X + v.Width <= 1280 + 1e-6 && v.Y + v.Height <= 720 + 1e-6);

        // The centre's speed changes a little from frame to frame: no jump where the pointer jumped.
        var centres = views.Select(v => (v.CenterX, v.CenterY)).ToList();
        var accelerations = Enumerable.Range(1, centres.Count - 2).Select(i => Math.Abs(centres[i + 1].CenterX - (2 * centres[i].CenterX) + centres[i - 1].CenterX)).ToList();
        accelerations.Max().Should().BeLessThan(3, "px per frame²");
    }

    /// <summary>Black pictures with a white square at <paramref name="x"/>, <paramref name="y"/> (6 × 6 px).</summary>
    private sealed class MarkerFrames(int width, int height, int x, int y) : IFrameProvider
    {
        public VideoFrame? Frame(Clip clip, MediaAsset asset, double sourceTime)
        {
            var frame = FramePool.Shared.Rent(width, height);
            var pixels = MemoryMarshal.Cast<byte, uint>(frame.Pixels);
            pixels.Fill(0xFF000000);
            for (var row = y; row < y + 6; row++)
            {
                pixels.Slice((row * width) + x, 6).Fill(0xFFFFFFFF);
            }

            frame.Time = sourceTime;
            return frame;
        }
    }

    /// <summary>The brightness-weighted centre of the pixels around (<paramref name="x"/>, <paramref name="y"/>).</summary>
    private static (double X, double Y) Centroid(SKBitmap bitmap, double x, double y, int reach)
    {
        double sx = 0, sy = 0, total = 0;
        for (var row = Math.Max(0, (int)y - reach); row < Math.Min(bitmap.Height, (int)y + reach); row++)
        {
            for (var column = Math.Max(0, (int)x - reach); column < Math.Min(bitmap.Width, (int)x + reach); column++)
            {
                double level = bitmap.GetPixel(column, row).Green;
                sx += (column + 0.5) * level;
                sy += (row + 0.5) * level;
                total += level;
            }
        }

        return (sx / total, sy / total);
    }

    [Fact]
    public void Acceptance_zooming_after_the_pointer_moves_smoothly_at_60_fps()
    {
        // A 640 × 360 screen at 60 fps with a white square at (300, 170); the pointer waits, glides 150 px right and
        // 70 down over 2 s, waits, then jumps back up and left at 5 s. One zoom follows it for 9.4 s.
        const int fps = 60;
        var metadata = Metadata(640, 360, fps, 600, n => n < 60 ? (250, 150) : n < 180 ? (250 + ((n - 60) * 150 / 120), 150 + ((n - 60) * 70 / 120)) : n < 300 ? (400, 220) : (330, 120));
        var project = WithZoom(Project(Asset(10, 640, 360, fps, metadata), new ProjectCanvas(640, 360, fps)), Zoom(0.3, 9.4, focus: ZoomFocus.FollowPointer));
        var compositor = new FrameCompositor(new MarkerFrames(640, 360, 300, 170));
        using var bitmap = new SKBitmap(new SKImageInfo(640, 360, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);

        var expected = new List<(double X, double Y)>();
        var measured = new List<(double X, double Y)>();
        var worst = 0.0;
        for (var n = 0; n < 600; n++)
        {
            var time = (double)n / fps;
            compositor.Render(canvas, project, time);
            canvas.Flush();
            var view = compositor.Camera.View(project, time);
            var scale = 640 / view.Width;
            var at = ((303 - view.X) * scale, (173 - view.Y) * scale);
            expected.Add(at);
            if (at.Item1 is < 20 or > 620 || at.Item2 is < 20 or > 340)
            {
                measured.Add(at);
                continue;
            }

            var found = Centroid(bitmap, at.Item1, at.Item2, (int)Math.Ceiling(6 * scale) + 4);
            measured.Add(found);
            worst = Math.Max(worst, Math.Max(Math.Abs(found.X - at.Item1), Math.Abs(found.Y - at.Item2)));
        }

        // The square is drawn where the camera puts it, to a fraction of a pixel (no rounding to whole pixels: that is what jitters).
        output.WriteLine($"worst distance from the camera's place: {worst:0.000} px");
        worst.Should().BeLessThan(0.15);

        // Its path on the screen is smooth: its speed changes by little from frame to frame, and the measured path
        // follows the camera's to well under a pixel in each frame's change of speed.
        double Second(IReadOnlyList<(double X, double Y)> path, int i, bool x) =>
            x ? path[i + 1].X - (2 * path[i].X) + path[i - 1].X : path[i + 1].Y - (2 * path[i].Y) + path[i - 1].Y;
        var smooth = Enumerable.Range(1, 598).Max(i => Math.Max(Math.Abs(Second(expected, i, true)), Math.Abs(Second(expected, i, false))));
        var jitter = Enumerable.Range(1, 598).Max(i => Math.Max(Math.Abs(Second(measured, i, true) - Second(expected, i, true)), Math.Abs(Second(measured, i, false) - Second(expected, i, false))));
        var reversals = Enumerable.Range(2, 597).Count(i => Second(expected, i, true) * Second(expected, i - 1, true) < 0 && Math.Abs(Second(expected, i, true)) > 0.05);
        output.WriteLine($"largest change of speed {smooth:0.000} px/frame², measured against the camera {jitter:0.000}, reversals {reversals}");
        smooth.Should().BeLessThan(2.5);
        jitter.Should().BeLessThan(0.3);
        reversals.Should().BeLessThan(12, "a smooth path doesn't shake back and forth");
    }

    /// <summary>A 4 s, 320 × 180 recording at (100, 50) with a left click at 1 s at (60, 40) and a key caption at 1.5 s.</summary>
    private string EffectMetadata()
    {
        var metadata = new RecordingMetadata(
            RecordingMetadata.FormatName, 1, "Rec.mp4", [new MetadataPart("Rec.mp4", 0)], 30, new MetadataArea(100, 50, 320, 180), 1,
            CursorInVideo: true, OverlaysInVideo: false, KeystrokeMode.Shortcuts, [],
            [.. Enumerable.Range(0, 120).Select(n => new[] { n, 40 + n, 90, 0, 1 })],
            [new MetadataClick(1, 1.1, PointerButton.Left, 60, 40)],
            [new MetadataKey(1.5, 1, "Ctrl + S")], [], []);
        var path = Path.Combine(_root, $"Rec-{Guid.NewGuid():N}.cgmeta.json");
        metadata.Save(path);
        return path;
    }

    private static SKBitmap Render(VideoProject project, double time)
    {
        var bitmap = new SKBitmap(new SKImageInfo(320, 180, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        new FrameCompositor(new SolidFrames(_ => (320, 180, 0xFF000000))).Render(canvas, project, time);
        return bitmap;
    }

    [Fact]
    public void Click_rings_move_with_the_zoomed_pictures_and_keystrokes_stay_put()
    {
        var path = EffectMetadata();
        var asset = new MediaAsset("rec", @"C:\nowhere\Rec.mp4", 4, [new AssetStream(0, StreamKind.Video, null, 0, 4, 320, 180, 30)], MetadataPath: path);
        var plain = VideoProject.Empty(new ProjectCanvas(320, 180, 30));
        plain = ProjectFactory.AddRecording(plain, asset, 0);
        var (clicks, _) = AnnotationEdits.AddEffect(plain, EffectKind.Clicks, 0, 4);
        var zoomed = WithZoom(clicks, Zoom(0, 4, x: 60 / 320.0, y: 40 / 180.0));

        // The view is (0, 0) to (160, 90): the ring at (60, 40) is drawn twice as large at (120, 80).
        var ring = new OverlayPlanner(new EffectItem { Id = "x", Kind = EffectKind.Clicks }.Settings(), new Core.Geometry.PixelPoint(100, 50), 1, 320, 180, drawPointer: false, webcam: false)
            .Plan(RecordingMetadata.Load(path).ToTimeline(), TimeSpan.FromSeconds(1.15)).Items.OfType<RingItem>().Single();
        using (var bitmap = Render(zoomed, 1.15))
        {
            bitmap.GetPixel((int)Math.Round(120 + (2 * ring.Radius)), 80).Red.Should().BeGreaterThan(150, "on the ring, zoomed");
            bitmap.GetPixel((int)Math.Round(60 + ring.Radius), 40).Red.Should().BeLessThan(40, "not where it was");
        }

        var (keys, _) = AnnotationEdits.AddEffect(plain, EffectKind.Keystrokes, 0, 4);
        using var still = Render(keys, 1.6);
        using var moved = Render(WithZoom(keys, Zoom(0, 4, x: 0.2, y: 0.2)), 1.6);
        moved.Bytes.Should().Equal(still.Bytes, "keystrokes are drawn where they were, over a black screen either way");
    }

    /// <summary>A 20 s, 1280 × 720 recording's metadata with clicks and keys in groups.</summary>
    private string AutoMetadata()
    {
        MetadataClick Click(double t, int x, int y) => new(t, t + 0.1, PointerButton.Left, x, y);
        return Metadata(1280, 720, 30, 600, n => (640, 360),
            [Click(2.0, 100, 100), Click(2.6, 180, 140), Click(7.0, 50, 50), Click(7.5, 1200, 700), Click(12.0, 1000, 600), Click(12.5, 1020, 610), Click(16.0, 100, 600)],
            [.. Enumerable.Range(0, 5).Select(i => new MetadataKey(3.0 + (i * 0.25), i + 1, $"key {i}"))]);
    }

    [Fact]
    public void Auto_zoom_makes_a_zoom_around_each_group_of_clicks_and_typing()
    {
        var overlays = new MetadataOverlays();
        var project = Project(Asset(20, 1280, 720, 30, AutoMetadata()), new ProjectCanvas(1280, 720, 30));
        AutoZoom.Events(project, overlays).Should().HaveCount(12);
        var zooms = AutoZoom.Plan(project, overlays);
        zooms.Select(z => (z.Start, Math.Round(z.End, 3))).Should().Equal(
            (1.2, 5.5),     // clicks at 2 and 2.6 s, then typing until 4 s, at the second click
            (6.2, 7.25),    // two clicks too far apart to share a view: they meet halfway
            (7.25, 9.0),
            (11.2, 14.0),
            (15.2, 17.5));
        zooms.Should().OnlyContain(z => z.Auto && z.Focus == ZoomFocus.FollowPointer && z.Scale == 2);
        zooms[0].CenterX.Should().BeApproximately(140 / 1280.0, 1e-3);

        // A cut of 5 to 8 s brings the later ones 3 s earlier; the two far-apart clicks are gone.
        var cut = TimelineEdits.DeleteRange(project, 5, 8);
        AutoZoom.Plan(cut, overlays).Select(z => (z.Start, Math.Round(z.End, 3))).Should().Equal((1.2, 5.5), (8.2, 11.0), (12.2, 14.5));

        // Played twice as fast, everything comes at half the time.
        var screen = project.Tracks.Single(t => t.Kind == TrackKind.Video).Clips.Single();
        var fast = project.WithItem(screen with { Speed = 2 });
        var last = AutoZoom.Plan(fast, overlays)[^1];
        last.End.Should().BeApproximately(9.5, 1e-6, "the click at 16 s is at 8 s");
        last.Start.Should().BeApproximately(7.125, 1e-6, "halfway from the clicks at 6.25 s, too far away to share its view");

        // Applied twice: the same zooms on one Zoom track; a zoom made by hand is kept and not overlapped.
        var (applied, count) = AutoZoom.Apply(project, overlays);
        count.Should().Be(5);
        var (again, _) = AutoZoom.Apply(applied, overlays);
        again.Tracks.Where(t => t.Items.Any(i => i is ZoomItem)).Should().ContainSingle().Which.Items.Should().HaveCount(5);
        var mine = Zoom(11, 2);
        var (withMine, kept) = AutoZoom.Apply(WithZoom(project, mine), overlays);
        kept.Should().Be(4);
        withMine.Find(mine.Id).Should().NotBeNull();
    }

    [Fact]
    public void A_zoom_inserted_at_the_playhead_follows_the_pointer_when_there_is_metadata()
    {
        var (project, id) = AnnotationEdits.AddZoom(TestProjects.Project(10), 2, ZoomItem.DefaultLength, followPointer: false);
        var zoom = (ZoomItem)project.Find(id)!.Value.Item;
        (zoom.Start, zoom.Length, zoom.Focus, zoom.Scale).Should().Be((2, 3, ZoomFocus.Fixed, 2));
        var (second, other) = AnnotationEdits.AddZoom(project, 3, 3, followPointer: true);
        second.Tracks.Count(t => t.Items.Any(i => i is ZoomItem)).Should().Be(2, "it overlaps the first");
        ((ZoomItem)second.Find(other)!.Value.Item).Focus.Should().Be(ZoomFocus.FollowPointer);
    }
}
