using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Rendering;
using CashGear.Capture.VideoEditor.Timeline;
using SkiaSharp;

namespace CashGear.Capture.VideoEditor.Tests;

/// <summary>Phase 22 M2: every picture clip and annotation has position, scale, rotation, opacity and crop, each with keyframes.</summary>
public sealed class MotionKeyframeTests
{
    private static SKBitmap Draw(VideoProject project, double time)
    {
        var bitmap = new SKBitmap(new SKImageInfo(project.Canvas.Width, project.Canvas.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        new FrameCompositor(new SolidFrames(_ => (640, 360, 0xFFFF0000))).Render(canvas, project, time);
        return bitmap;
    }

    private static (VideoProject Project, Clip Clip) Picture(ItemMotion? motion, double size = 0.25)
    {
        var (project, id) = MediaEdits.AddSolid(VideoProject.Empty(new ProjectCanvas(640, 360, 30)), "#00FF00", 0, 10);
        var clip = (Clip)project.Find(id)!.Value.Item with { Placement = new Placement(0.5, 0.5, size), Motion = motion };
        return (project.WithItem(clip), clip);
    }

    private static SKColor Pixel(VideoProject project, double time, int x, int y)
    {
        using var drawn = Draw(project, time);
        return drawn.GetPixel(x, y);
    }

    public static TheoryData<MotionEasing> Easings => new() { MotionEasing.Linear, MotionEasing.EaseIn, MotionEasing.EaseOut, MotionEasing.EaseInOut, MotionEasing.Spring, MotionEasing.Bezier };

    [Theory]
    [MemberData(nameof(Easings))]
    public void An_easing_goes_from_0_to_1_and_is_continuous(MotionEasing easing)
    {
        double[] curve = [0.42, 0, 0.58, 1];
        MotionCurves.Ease(easing, 0, curve).Should().Be(0);
        MotionCurves.Ease(easing, 1, curve).Should().Be(1);
        MotionCurves.Ease(easing, -3, curve).Should().Be(0);
        MotionCurves.Ease(easing, 4, curve).Should().Be(1);
        var previous = 0.0;
        for (var i = 1; i <= 1000; i++)
        {
            var value = MotionCurves.Ease(easing, i / 1000.0, curve);
            Math.Abs(value - previous).Should().BeLessThan(0.02, "no jump at {0}", i);
            previous = value;
        }
    }

    [Fact]
    public void Each_easing_has_its_own_shape()
    {
        MotionCurves.Ease(MotionEasing.Linear, 0.5).Should().Be(0.5);
        MotionCurves.Ease(MotionEasing.EaseIn, 0.5).Should().BeApproximately(0.125, 1e-9);
        MotionCurves.Ease(MotionEasing.EaseOut, 0.5).Should().BeApproximately(0.875, 1e-9);
        MotionCurves.Ease(MotionEasing.EaseInOut, 0.25).Should().BeApproximately(0.15625, 1e-9);
        MotionCurves.Ease(MotionEasing.EaseInOut, 0.5).Should().Be(0.5);
        var most = Enumerable.Range(1, 999).Max(i => MotionCurves.Ease(MotionEasing.Spring, i / 1000.0));
        most.Should().BeInRange(1.05, 1 + (2 * MotionCurves.SpringOvershoot), "a spring goes a little past its value");

        // CSS's ease-in-out and a straight line: known values, and without control points a straight line.
        MotionCurves.Ease(MotionEasing.Bezier, 0.5, [0.42, 0, 0.58, 1]).Should().BeApproximately(0.5, 1e-6);
        MotionCurves.Ease(MotionEasing.Bezier, 0.25, [0.42, 0, 0.58, 1]).Should().BeApproximately(0.1292, 1e-3);
        MotionCurves.Ease(MotionEasing.Bezier, 0.3, [0.25, 0.25, 0.75, 0.75]).Should().BeApproximately(0.3, 1e-6);
        MotionCurves.Ease(MotionEasing.Bezier, 0.3).Should().Be(0.3);
    }

    [Fact]
    public void Each_lane_goes_its_own_way_and_holds_outside_its_keys()
    {
        var motion = new ItemMotion
        {
            X = [new(1, 0), new(3, 1, MotionEasing.EaseIn)],
            Scale = [new(0, 2), new(2, 4, MotionEasing.EaseIn), new(4, 8)],
            Rotation = [new(0, 45)],
        };
        motion.At(0).X.Should().Be(0);
        motion.At(2).X.Should().BeApproximately(0.5, 1e-9);
        motion.At(9).X.Should().Be(1);
        motion.At(1).Scale.Should().BeApproximately(3, 1e-9);
        motion.At(3).Scale.Should().BeApproximately(4 + (4 * 0.125), 1e-9, "the easing is the keyframe's before it");
        motion.At(7).Rotation.Should().Be(45);
        motion.At(7).Opacity.Should().Be(1, "no keys: as it is");
        motion.KeyTimes().Should().Equal(0, 1, 2, 3, 4);
    }

    [Fact]
    public void Position_scale_opacity_and_crop_draw_where_they_say()
    {
        var (still, _) = Picture(null);
        Pixel(still, 1, 320, 180).Green.Should().Be(255);
        Pixel(still, 1, 100, 180).Green.Should().Be(0);

        var moved = Picture(new ItemMotion { X = [new(0, -0.25)] }).Project;
        Pixel(moved, 1, 160, 180).Green.Should().Be(255);
        Pixel(moved, 1, 320, 180).Green.Should().Be(0);

        var grown = Picture(new ItemMotion { Scale = [new(0, 2)] }).Project;
        Pixel(grown, 1, 320 + 100, 180).Green.Should().Be(255, "about its middle");

        var faint = Picture(new ItemMotion { Opacity = [new(0, 0.5)] }).Project;
        ((int)Pixel(faint, 1, 320, 180).Green).Should().BeInRange(126, 129);

        var cropped = Picture(new ItemMotion { CropLeft = [new(0, 0.5)] }).Project;
        Pixel(cropped, 1, 320 - 30, 180).Green.Should().Be(0, "the left half is cut off");
        Pixel(cropped, 1, 320 + 30, 180).Green.Should().Be(255, "the right half stays where it was");
    }

    [Fact]
    public void A_rotation_turns_about_the_middle()
    {
        var (project, _) = Picture(new ItemMotion { Rotation = [new(0, 0), new(2, 90)] }, size: 0.5);
        var wide = project.WithItem(((Clip)project.Tracks[0].Items[0]) with { Placement = new Placement(0.5, 0.5, 0.5) });

        // A 16:9 canvas's colour is 320 × 180: turned a quarter of the way round it reaches further up than across.
        Pixel(wide, 0, 320, 180 - 80).Green.Should().Be(255);
        Pixel(wide, 0, 320, 180 - 120).Green.Should().Be(0);
        Pixel(wide, 2, 320, 180 - 120).Green.Should().Be(255);
        Pixel(wide, 2, 320 + 120, 180).Green.Should().Be(0);
    }

    [Fact]
    public void A_split_keeps_every_picture_of_a_curve()
    {
        var motion = new ItemMotion { X = [new(0, 0, MotionEasing.Spring), new(4, 0.3)], Scale = [new(1, 1, MotionEasing.EaseInOut), new(3, 1.5, MotionEasing.Bezier, [0.2, 0.8, 0.5, 1]), new(6, 1)] };
        var (project, clip) = Picture(motion);
        var split = TimelineEdits.Split(project, 2.03);
        var parts = split.Tracks[0].Clips.ToList();
        parts.Should().HaveCount(2);
        foreach (var time in new[] { 0.5, 1.9, 2.0, 2.1, 2.9, 3.5, 5.5, 8 })
        {
            var part = parts.First(p => p.Covers(time));
            var (was, now) = (motion.At(time), part.Motion!.At(time - part.Start));
            now.X.Should().BeApproximately(was.X, 0.002, "at {0} s", time);
            now.Scale.Should().BeApproximately(was.Scale, 0.002, "at {0} s", time);
        }

        clip.Motion.Should().NotBeNull();
    }

    [Fact]
    public void Making_it_longer_at_its_start_keeps_its_movement_where_it_was()
    {
        var (project, clip) = Picture(new ItemMotion { X = [new(1, 0), new(2, 0.5)] });
        project = project.WithItem(clip with { Start = 3 });
        var longer = TimelineEdits.Trim(project, clip.Id, ItemEdge.Start, 1);
        var moved = (Clip)longer.Find(clip.Id)!.Value.Item;
        moved.Start.Should().Be(1);
        moved.Motion!.At(4 - moved.Start).X.Should().BeApproximately(0, 1e-9);
        moved.Motion!.At(5 - moved.Start).X.Should().BeApproximately(0.5, 1e-9);
    }

    [Fact]
    public void Changing_a_value_makes_a_keyframe_only_while_it_animates()
    {
        var constant = MotionEdits.SetValue(null, MotionLane.Scale, 3, 2, 30);
        constant.Scale.Should().ContainSingle();
        constant = MotionEdits.SetValue(constant, MotionLane.Scale, 5, 3, 30);
        constant.Scale.Should().ContainSingle().Which.Value.Should().Be(3, "off, it holds for the whole item");
        constant.KeyTimes().Should().BeEmpty();

        var keyed = MotionEdits.AddKeyframe(constant, 1, 30);
        keyed.Animated.Should().BeTrue();
        keyed.Scale!.Single().Value.Should().Be(3);
        keyed.KeyTimes().Should().Equal(1.0);
        keyed = MotionEdits.SetValue(keyed, MotionLane.Scale, 3, 5, 30);
        keyed.Scale!.Select(k => (k.Time, k.Value)).Should().Equal((1.0, 3.0), (3.0, 5.0));
        keyed = MotionEdits.SetValue(keyed, MotionLane.Scale, 3.01, 6, 30);
        keyed.Scale!.Should().HaveCount(2, "within half a frame it is the same keyframe");
        keyed.At(3).Scale.Should().Be(6);

        keyed = MotionEdits.SetEasing(keyed, 2, MotionEasing.Spring, null, 30);
        keyed.Scale![0].Easing.Should().Be(MotionEasing.Spring, "the keyframe at or before the time");
        keyed.Scale![1].Easing.Should().Be(MotionEasing.Linear);
        MotionEdits.EasingAt(keyed, 2, 30).Easing.Should().Be(MotionEasing.Spring);

        keyed = MotionEdits.RemoveKeyframe(keyed, 3, 30);
        keyed.Scale!.Should().ContainSingle();
    }

    [Fact]
    public void Keyframes_show_in_a_lane_under_the_row_and_drag_along_it()
    {
        var (project, clip) = Picture(null);
        var rows = TimelineView.Rows(project);
        rows.Single().Lane.Should().Be(0);
        project = project.WithItem(clip with { Motion = new ItemMotion { Scale = [new(1, 1), new(2, 2)] } });
        var row = TimelineView.Rows(project).Single();
        row.Lane.Should().Be(TimelineView.KeyframeLaneHeight);
        row.Height.Should().Be(TimelineView.PictureRowHeight + TimelineView.KeyframeLaneHeight);

        var view = new TimelineView { PixelsPerSecond = 100, Width = 1000, Height = 200 };
        var y = view.RowTop(row) + row.Height - (row.Lane / 2);
        view.KeyframeAt(project, view.X(2), y).Should().Be((clip.Id, 2.0));
        view.KeyframeAt(project, view.X(1.5), y).Should().BeNull();
        view.KeyframeAt(project, view.X(2), view.RowTop(row) + 5).Should().BeNull("only in the lane");

        var history = new ProjectHistory(project);
        var session = new TimelineSession(history, view);
        session.PointerDown(view.X(2), y, shift: false);
        session.SelectedId.Should().Be(clip.Id);
        session.Playhead.Should().Be(2);
        session.PointerMove(view.X(3), y, alt: false);
        session.PointerMove(view.X(4), y, alt: false);
        session.PointerUp(view.X(4), y);
        ((Clip)history.Project.Find(clip.Id)!.Value.Item).Motion!.Scale!.Select(k => k.Time).Should().Equal(1.0, 4.0);
        history.Undo();
        ((Clip)history.Project.Find(clip.Id)!.Value.Item).Motion!.Scale!.Select(k => k.Time).Should().Equal(1.0, 2.0);
    }

    [Fact]
    public void An_annotation_goes_between_the_colours_and_opacities_of_its_keyframes()
    {
        var item = new AnnotationItem
        {
            Id = "a",
            Shape = AnnotationShape.Rectangle,
            Length = 4,
            Look = new AnnotationLook(Color: "#FF0000", StrokeWidth: 6),
            Keyframes = [new(0, 10, 10, 100, 100, Color: "#FF0000", Opacity: 1), new(2, 10, 10, 100, 100, Color: "#0000FF", Opacity: 0.5, Ease: MotionEasing.Linear), new(4, 10, 10, 100, 100)],
            Easing = Easing.Linear,
        };
        item.At(1).Color.Should().Be("#800080");
        item.At(1).Opacity.Should().BeApproximately(0.75, 1e-9);
        item.At(2).Color.Should().Be("#0000FF");

        // The last keyframe has none of its own: back to the look's.
        item.At(4).Opacity.Should().Be(1);
        item.At(3).Color.Should().Be("#800080");
        item.At(3).Opacity.Should().BeApproximately(0.75, 1e-9);

        var annotation = FrameCompositor.AnnotationFor(item, 1)!;
        annotation.Style.Color.Should().Be(new SKColor(0x80, 0, 0x80));
        annotation.Style.Opacity.Should().BeApproximately(0.75f, 1e-5f);

        // The way along follows the keyframe's own easing when it has one.
        var eased = item with { Keyframes = [new(0, 0, 0, 0, 0, Ease: MotionEasing.EaseIn), new(2, 100, 0, 0, 0)] };
        eased.At(1).X.Should().BeApproximately(12.5, 1e-9);
    }

    [Fact]
    public void An_annotation_moves_turns_and_is_found_where_it_is_drawn()
    {
        var project = VideoProject.Empty(new ProjectCanvas(640, 360, 30));
        (project, var id) = AnnotationEdits.Add(project, AnnotationShape.Rectangle, 0, 5);
        var item = (AnnotationItem)project.Find(id)!.Value.Item;
        item = item with { Look = item.Look with { Fill = "#00FF00", Color = "#00FF00" }, Keyframes = [new(0, 100, 100, 100, 100)], FadeIn = 0, FadeOut = 0, Motion = new ItemMotion { X = [new(0, 0.5)], Opacity = [new(0, 1)] } };
        project = project.WithItem(item);
        Pixel(project, 1, 150, 150).Green.Should().Be(0);
        Pixel(project, 1, 150 + 320, 150).Green.Should().Be(255);
        PlacementEdits.HitTest(project, 1, new SKPoint(150, 150)).Should().BeNull();
        PlacementEdits.HitTest(project, 1, new SKPoint(150 + 320, 150))!.Value.ItemId.Should().Be(id);
    }

    [Fact]
    public void A_picture_with_motion_is_found_where_it_is_drawn()
    {
        var (project, clip) = Picture(new ItemMotion { X = [new(0, 0.25)] });
        PlacementEdits.HitTest(project, 1, new SKPoint(320, 180)).Should().BeNull();
        PlacementEdits.HitTest(project, 1, new SKPoint(480, 180))!.Value.ItemId.Should().Be(clip.Id);
        PlacementEdits.Area(project, clip, 1).MidX.Should().BeApproximately(480, 0.01f);
    }

    [Fact]
    public void Tracks_stay_put_under_a_zoom_unless_they_move_with_it()
    {
        var project = TestProjects.Project(10);
        (project, var id) = MediaEdits.AddSolid(project, "#00FF00", 0, 10);
        project = project.WithItem(((Clip)project.Find(id)!.Value.Item) with { Placement = new Placement(0.75, 0.75, 0.1) });
        (project, _) = AnnotationEdits.AddZoom(project, 0, 10, followPointer: false);
        var zoom = project.Tracks.SelectMany(t => t.Items).OfType<ZoomItem>().Single();
        project = project.WithItem(zoom with { Scale = 2, CenterX = 0.25, CenterY = 0.25, EaseIn = 0, EaseOut = 0 });

        // Zoomed into the top-left quarter: where it is put, it stays; moved with the zoom, it is off the picture.
        var at = 5.0;
        Pixel(project, at, 960, 540).Green.Should().Be(255);
        var track = project.Find(id)!.Value.Track;
        var moving = project.WithTrack(track with { MovesWithZoom = true });
        Pixel(moving, at, 960, 540).Green.Should().NotBe(255);
    }

    [Fact]
    public void Motion_is_saved_and_read_back()
    {
        var (project, clip) = Picture(new ItemMotion { Animated = true, X = [new(0, 0.1, MotionEasing.Bezier, [0.1, 0.2, 0.3, 0.4]), new(2, 0.2)], CropTop = [new(1, 0.5)] });
        project = project.WithTrack(project.Tracks[0] with { MovesWithZoom = true });
        var read = ProjectJson.Deserialize(ProjectJson.Serialize(project));
        ProjectJson.Serialize(read).Should().Equal(ProjectJson.Serialize(project));
        var again = (Clip)read.Find(clip.Id)!.Value.Item;
        again.Motion!.X![0].Easing.Should().Be(MotionEasing.Bezier);
        again.Motion.X![0].Curve.Should().Equal(0.1, 0.2, 0.3, 0.4);
        read.Tracks[0].MovesWithZoom.Should().BeTrue();

        var text = System.Text.Encoding.UTF8.GetString(ProjectJson.Serialize(TestProjects.Project(1)));
        text.Should().NotContain("motion", "nothing is written for what isn't used");
    }

    [Theory]
    [InlineData(0.1)]
    [InlineData(0.8)]
    [InlineData(1.6)]
    public void Keyframed_scale_rotation_and_opacity_match_between_preview_and_export(double time)
    {
        using var turn = new CashGear.Capture.Tests.MachineTurn();
        using var gl = OffscreenGl.TryCreate();
        if (gl is null)
        {
            Assert.Skip("No OpenGL here");
        }

        var (project, id) = (TestProjects.Project(4), "");
        (project, id) = MediaEdits.AddSolid(project, "#E03010", 0, 4);
        var clip = (Clip)project.Find(id)!.Value.Item with
        {
            Placement = new Placement(0.5, 0.5, 0.5, CornerRadius: 12, Border: "#FFFFFF", BorderWidth: 3),
            Motion = new ItemMotion
            {
                Scale = [new(0, 0.5, MotionEasing.Spring), new(1, 1.2, MotionEasing.EaseOut), new(2, 0.8)],
                Rotation = [new(0, -30, MotionEasing.EaseInOut), new(2, 45)],
                Opacity = [new(0, 0.2, MotionEasing.EaseIn), new(1, 1)],
                X = [new(0, -0.2, MotionEasing.Bezier, [0.2, 0.8, 0.4, 1]), new(2, 0.2)],
                CropBottom = [new(0, 0), new(2, 0.3)],
            },
        };
        project = project.WithItem(clip);
        (project, var text) = AnnotationEdits.Add(project, AnnotationShape.Text, 0, 4);
        var written = (AnnotationItem)project.Find(text)!.Value.Item;
        project = project.WithItem(written with { Motion = new ItemMotion { Scale = [new(0, 1), new(2, 1.5)], Rotation = [new(0, 0), new(2, 20)], Opacity = [new(0, 1), new(2, 0.5)] } });

        var frames = new SolidFrames(_ => (1280, 720, 0xFF2040C0));
        var (width, height) = (project.Canvas.Width, project.Canvas.Height);
        using var memory = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(memory))
        {
            new FrameCompositor(frames).Render(canvas, project, time);
        }

        using var gpu = SKSurface.Create(gl!.Context, false, new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        new FrameCompositor(frames).Render(gpu.Canvas, project, time);
        gpu.Flush();
        using var read = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        gpu.ReadPixels(read.Info, read.GetPixels(), read.RowBytes, 0, 0);
        PreviewCompare.MeanDifference(memory, read).Should().BeLessThan(1, "at {0} s", time);
    }
}

/// <summary>How far two pictures are apart: the mean of each colour channel's difference, in levels.</summary>
internal static class PreviewCompare
{
    public static double MeanDifference(SKBitmap a, SKBitmap b)
    {
        var first = a.GetPixelSpan();
        var second = b.GetPixelSpan();
        long sum = 0;
        for (var i = 0; i < first.Length; i += 4)
        {
            sum += Math.Abs(first[i] - second[i]) + Math.Abs(first[i + 1] - second[i + 1]) + Math.Abs(first[i + 2] - second[i + 2]);
        }

        return sum / (first.Length / 4 * 3.0);
    }
}
