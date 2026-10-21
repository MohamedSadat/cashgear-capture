using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Rendering;
using SkiaSharp;

namespace CashGear.Capture.VideoEditor.Tests;

public sealed class CompositorTests
{
    private const uint Red = 0xFFFF0000;
    private const uint Blue = 0xFF0000FF;

    /// <summary>Renders at 100 × 100 canvas pixels (the canvas is 1280 × 720 scaled down to 160 × 90).</summary>
    private static SKBitmap Render(VideoProject project, double time, SolidFrames frames)
    {
        var bitmap = new SKBitmap(new SKImageInfo(160, 90, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Scale(160f / project.Canvas.Width, 90f / project.Canvas.Height);
        new FrameCompositor(frames).Render(canvas, project, time);
        return bitmap;
    }

    private static SolidFrames Look(uint screen = Red, uint webcam = Blue, int screenWidth = 1280, int screenHeight = 720) =>
        new(clip => clip.Stream == 3 ? (320, 240, webcam) : (screenWidth, screenHeight, screen));

    [Fact]
    public void The_screen_fills_the_canvas_and_a_different_shape_is_letterboxed()
    {
        var project = TestProjects.Project(10) with { Canvas = new ProjectCanvas(1280, 720, 30, "#00FF00") };
        using (var bitmap = Render(project, 1, Look()))
        {
            bitmap.GetPixel(1, 1).Should().Be(new SKColor(255, 0, 0));
            bitmap.GetPixel(158, 88).Should().Be(new SKColor(255, 0, 0));
        }

        // A 4:3 picture on a 16:9 canvas: the background shows left and right.
        var asset = project.Assets.Single();
        var square = project.WithAsset(asset with { Streams = [asset.Streams[0] with { Width = 640, Height = 480 }, .. asset.Streams.Skip(1)] });
        using (var bitmap = Render(square, 1, Look(screenWidth: 640, screenHeight: 480)))
        {
            bitmap.GetPixel(5, 45).Should().Be(new SKColor(0, 255, 0));
            bitmap.GetPixel(80, 45).Should().Be(new SKColor(255, 0, 0));
            bitmap.GetPixel(155, 45).Should().Be(new SKColor(0, 255, 0));
        }

        using var after = Render(project, 11, Look());
        after.GetPixel(80, 45).Should().Be(new SKColor(0, 255, 0), "after the clip only the background is left");
    }

    [Fact]
    public void The_webcam_is_placed_shaped_and_hidden_on_request()
    {
        var project = TestProjects.Project(10, webcam: true);
        var webcam = project.Tracks[3].Clips.Single() with { Placement = new Placement(0.75, 0.5, 0.5, PlacementShape.Circle, "#FFFFFF", 0) };
        project = project.WithItem(webcam);
        using (var bitmap = Render(project, 1, Look()))
        {
            bitmap.GetPixel(120, 45).Should().Be(new SKColor(0, 0, 255), "the circle's centre at 75% across");
            bitmap.GetPixel(120, 25).Blue.Should().BeGreaterThan(200, "inside the circle (radius 22.5 px)");
            bitmap.GetPixel(100, 25).Should().Be(new SKColor(255, 0, 0), "the square's corner is outside the circle");
            bitmap.GetPixel(60, 45).Should().Be(new SKColor(255, 0, 0));
        }

        using var hidden = Render(project.WithTrack(project.Tracks[3] with { Hidden = true }), 1, Look());
        hidden.GetPixel(120, 45).Should().Be(new SKColor(255, 0, 0));
    }

    [Fact]
    public void A_border_is_drawn_around_the_shape()
    {
        var project = TestProjects.Project(10, webcam: true);
        project = project.WithItem(project.Tracks[3].Clips.Single() with { Placement = new Placement(0.5, 0.5, 0.5, PlacementShape.Rectangle, "#00FF00", 16) });
        using var bitmap = Render(project, 1, Look());

        // 4:3 webcam at half the canvas height: 480 × 360 canvas pixels, centred: x 400..880, y 180..540 → 50..110, 22.5..67.5.
        bitmap.GetPixel(50, 45).Should().Be(new SKColor(0, 255, 0));
        bitmap.GetPixel(80, 45).Should().Be(new SKColor(0, 0, 255));
    }

    [Fact]
    public void Fades_and_crossfades_blend_by_time()
    {
        var project = TestProjects.Project(10);
        var screen = project.Tracks[2].Clips.Single() with { FadeIn = 1, FadeOut = 2 };
        project = project.WithItem(screen);
        FrameCompositor.Fade(screen, 0.25).Should().BeApproximately(0.25, 1e-9);
        FrameCompositor.Fade(screen, 9).Should().BeApproximately(0.5, 1e-9);
        using (var bitmap = Render(project, 0.5, Look()))
        {
            bitmap.GetPixel(80, 45).Red.Should().BeInRange(126, 129, "half faded in over black");
        }

        // Two clips overlapping by 1 s (4 to 5): halfway through, half of each.
        var split = TimelineEdits.Split(TestProjects.Project(10), 5);
        var second = split.Tracks[2].Clips.Last() with { Start = 4 };
        split = split.WithItem(second);
        var frames = new SolidFrames(c => (1280, 720, c.Id == second.Id ? Blue : Red));
        using var blend = Render(split, 4.5, frames);
        var pixel = blend.GetPixel(80, 45);
        pixel.Red.Should().BeInRange(126, 129);
        pixel.Blue.Should().BeInRange(126, 129);
        frames.Asked.Should().Contain((second.Id, 5.5), "the later clip shows its source half a second in");
    }
}
