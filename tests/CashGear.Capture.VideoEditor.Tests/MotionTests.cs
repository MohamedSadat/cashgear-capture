using System.Diagnostics;
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

public sealed class MotionTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cashgear-motion-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static MediaAsset Asset(double seconds, int width, int height, int fps, string? metadata = null) =>
        new("rec", @"C:\nowhere\Rec.mp4", seconds, [new AssetStream(0, StreamKind.Video, null, 0, seconds, width, height, fps)], MetadataPath: metadata);

    private static VideoProject Project(double seconds = 10, int fps = 60) =>
        ProjectFactory.AddRecording(VideoProject.Empty(new ProjectCanvas(640, 360, fps)), Asset(seconds, 640, 360, fps), 0);

    /// <summary>Black pictures with a white square 40 px wide centred at (300, 170).</summary>
    private sealed class Square : IFrameProvider
    {
        public VideoFrame? Frame(Clip clip, MediaAsset asset, double sourceTime)
        {
            var frame = FramePool.Shared.Rent(640, 360);
            var pixels = MemoryMarshal.Cast<byte, uint>(frame.Pixels);
            pixels.Fill(0xFF000000);
            for (var y = 150; y < 190; y++)
            {
                pixels.Slice((y * 640) + 280, 40).Fill(0xFFFFFFFF);
            }

            frame.Time = sourceTime;
            return frame;
        }
    }

    private static SKBitmap Draw(VideoProject project, double time, IFrameProvider? frames = null, int samples = 16)
    {
        var bitmap = new SKBitmap(new SKImageInfo(project.Canvas.Width, project.Canvas.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        new FrameCompositor(frames ?? new Square()) { MotionSamples = samples }.Render(canvas, project, time);
        return bitmap;
    }

    private static ZoomItem Zoom(double start, double length, double blur = 0.5, ZoomEasing easing = ZoomEasing.Smooth, double x = 0.5, double y = 0.5) =>
        new() { Id = Ids.New(), Start = start, Length = length, Scale = 2, Focus = ZoomFocus.Fixed, CenterX = x, CenterY = y, MotionBlur = blur, Easing = easing, EaseIn = 0.6, EaseOut = 0.6 };

    /// <summary>The brightness-weighted middle of a picture.</summary>
    private static (double X, double Y, double Total) Middle(SKBitmap bitmap)
    {
        double x = 0, y = 0, total = 0;
        var pixels = bitmap.GetPixelSpan();
        for (var row = 0; row < bitmap.Height; row++)
        {
            for (var column = 0; column < bitmap.Width; column++)
            {
                var value = pixels[((row * bitmap.Width) + column) * 4];
                x += value * (column + 0.5);
                y += value * (row + 0.5);
                total += value;
            }
        }

        return (x / total, y / total, total);
    }

    /// <summary>The share of a row's pixels that are neither black nor white, in the row through the square.</summary>
    private static int Greys(SKBitmap bitmap, int row)
    {
        var count = 0;
        for (var x = 0; x < bitmap.Width; x++)
        {
            if (bitmap.GetPixel(x, row).Red is > 16 and < 240)
            {
                count++;
            }
        }

        return count;
    }

    [Fact]
    public void A_camera_that_stands_still_is_not_blurred()
    {
        var project = AutoZoom.AddZoom(Project(), Zoom(1, 4));
        var plain = AutoZoom.AddZoom(Project(), Zoom(1, 4, blur: 0));
        var camera = new ZoomCamera(new MetadataOverlays());
        camera.Motion(project, 0.5, 16).Should().BeNull("before the zoom");
        camera.Motion(project, 3, 16).Should().BeNull("holding");
        camera.Motion(plain, 1.3, 16).Should().BeNull("a zoom without motion blur");
        camera.Motion(project, 1.3, 16).Should().NotBeNull("zooming in");
        camera.Motion(project, 4.7, 16).Should().NotBeNull("zooming out");

        using var a = Draw(project, 3);
        using var b = Draw(plain, 3);
        a.Bytes.Should().Equal(b.Bytes, "at a standstill it draws exactly what it draws without");
    }

    [Fact]
    public void A_moving_camera_blurs_along_its_movement_and_keeps_its_place()
    {
        // At 30 fps with the shutter open all the time, zooming about a point away from the square, so its edges travel.
        var blurred = AutoZoom.AddZoom(Project(fps: 30), Zoom(1, 4, blur: 1, x: 0.9, y: 0.9));
        var plain = AutoZoom.AddZoom(Project(fps: 30), Zoom(1, 4, blur: 0, x: 0.9, y: 0.9));
        const double time = 1.3;
        using var soft = Draw(blurred, time);
        using var sharp = Draw(plain, time);

        Greys(soft, (int)Middle(sharp).Y).Should().BeGreaterThan(Greys(sharp, (int)Middle(sharp).Y) + 4, "its edges are smeared");
        var (x, y, total) = Middle(soft);
        var (sharpX, sharpY, sharpTotal) = Middle(sharp);
        Math.Abs(x - sharpX).Should().BeLessThan(1, "around the same place");
        Math.Abs(y - sharpY).Should().BeLessThan(1);
        Math.Abs((total / sharpTotal) - 1).Should().BeLessThan(0.02, "and as bright, only spread out");
    }

    [Fact]
    public void Faster_movement_takes_more_samples_up_to_what_is_allowed()
    {
        var project = AutoZoom.AddZoom(Project(fps: 30), Zoom(1, 4, blur: 1));
        var camera = new ZoomCamera(new MetadataOverlays());
        var fast = camera.Motion(project, 1.3, 32)!.Value;
        var slow = camera.Motion(project, 1.05, 32)!.Value;
        slow.Samples.Should().BeLessThan(fast.Samples);
        camera.Motion(project, 1.3, 4)!.Value.Samples.Should().BeLessThanOrEqualTo(4);

        // More samples change little: the export's and the preview's blur are the same blur.
        using var preview = Draw(project, 1.3, samples: 8);
        using var export = Draw(project, 1.3, samples: 32);
        StudioLookTests.Mean(preview, export).Should().BeLessThan(1);
    }

    [Fact]
    public void A_spring_goes_a_little_past_and_settles_and_a_zoom_out_never_passes()
    {
        ZoomCamera.Spring(0).Should().Be(0);
        ZoomCamera.Spring(1).Should().BeApproximately(1, 1e-9);
        ZoomCamera.Spring(1, overshoots: false).Should().BeApproximately(1, 1e-9);
        var into = Enumerable.Range(0, 1001).Select(n => ZoomCamera.Spring(n / 1000.0)).ToList();
        into.Max().Should().BeInRange(1.02, 1.07, "it goes a few percent past");
        into.Zip(into.Skip(1)).Max(p => Math.Abs(p.Second - p.First)).Should().BeLessThan(0.008, "no jump anywhere");
        var settling = Enumerable.Range(0, 1001).Select(n => ZoomCamera.Spring(n / 1000.0, overshoots: false)).ToList();
        settling.Should().OnlyContain(v => v <= 1 + 1e-9);
        settling.Zip(settling.Skip(1)).Should().OnlyContain(p => p.Second >= p.First - 1e-9, "it only closes in");
        (into[1] / 0.001).Should().BeLessThan(0.1, "it starts with no speed");
    }

    [Fact]
    public void A_spring_zoom_stays_inside_the_picture_and_ends_where_a_smooth_one_does()
    {
        var corner = AutoZoom.AddZoom(Project(), Zoom(1, 4, easing: ZoomEasing.Spring, x: 0, y: 0));
        var smooth = AutoZoom.AddZoom(Project(), Zoom(1, 4, x: 0, y: 0));
        var camera = new ZoomCamera(new MetadataOverlays());
        for (var time = 1.0; time < 5.2; time += 1.0 / 240)
        {
            var view = camera.View(corner, time);
            view.X.Should().BeGreaterThanOrEqualTo(-1e-6);
            view.Y.Should().BeGreaterThanOrEqualTo(-1e-6);
            (view.X + view.Width).Should().BeLessThanOrEqualTo(640 + 1e-6, "at {0}", time);
            (view.Y + view.Height).Should().BeLessThanOrEqualTo(360 + 1e-6);
        }

        // It passes its place on the way in, and holds at the same place afterwards.
        var narrowest = Enumerable.Range(0, 240).Min(n => camera.View(corner, 1 + (n * 0.6 / 240)).Width);
        narrowest.Should().BeLessThan(320 - 1);
        camera.View(corner, 3).Should().Be(camera.View(smooth, 3));

        // Whatever the easing, the view changes smoothly: no jump at the seams.
        var views = Enumerable.Range(0, 1000).Select(n => camera.View(corner, 0.9 + (n * 4.3 / 1000)).Width).ToList();
        views.Zip(views.Skip(1)).Max(p => Math.Abs(p.Second - p.First)).Should().BeLessThan(16);
    }

    [Fact]
    public void Zooms_with_motion_blur_at_60_fps_show_no_stepping()
    {
        // A look, a zoom in, a pan to the next, a zoom out, a frame at a time: the square's middle stays where the
        // camera puts it, and its movement from frame to frame changes as smoothly as the camera's does.
        var look = Looks.Apply(Project(), Looks.Find(Looks.DarkId)!);
        var project = AutoZoom.AddZoom(AutoZoom.AddZoom(look, Zoom(1, 2.5, x: 0.4, y: 0.45)), Zoom(3.6, 2, easing: ZoomEasing.Spring, x: 0.55, y: 0.5));
        var fixture = new FrameCompositor(new Square()) { MotionSamples = 16 };
        var moving = 0;
        var expected = new List<(double X, double Y)>();
        var measured = new List<(double X, double Y)>();
        for (var n = 0; n < 400; n++)
        {
            var time = 0.8 + (n / 60.0);
            var view = fixture.Camera.View(project, time);
            var matrix = view.Matrix(project.Canvas);
            var area = Looks.ScreenPlacement(project)!.Area(640, 360, 640, 360);

            // Where the square's middle (300, 170 in the picture) is on the canvas, then through the camera.
            var scale = area.Width / 640;
            var at = matrix.MapPoint((float)(area.X + (300 * scale)), (float)(area.Y + (170 * scale)));
            expected.Add((at.X, at.Y));
            if (fixture.Camera.Motion(project, time, 16) is not null)
            {
                moving++;
            }

            using var bitmap = new SKBitmap(new SKImageInfo(640, 360, SKColorType.Bgra8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(bitmap))
            {
                fixture.Render(canvas, project, time);
            }

            measured.Add(Brightest(bitmap));
        }

        moving.Should().BeGreaterThan(60, "the test has to cover movement");
        var worst = expected.Zip(measured).Max(p => Math.Max(Math.Abs(p.First.X - p.Second.X), Math.Abs(p.First.Y - p.Second.Y)));
        double Second(IReadOnlyList<(double X, double Y)> path, int i, bool x) =>
            x ? path[i + 1].X - (2 * path[i].X) + path[i - 1].X : path[i + 1].Y - (2 * path[i].Y) + path[i - 1].Y;
        var jitter = Enumerable.Range(1, 398).Max(i => Math.Max(Math.Abs(Second(measured, i, true) - Second(expected, i, true)), Math.Abs(Second(measured, i, false) - Second(expected, i, false))));
        output.WriteLine($"{moving} of 400 frames blurred; worst distance from the camera's place {worst:0.000} px; the square's movement against the camera's {jitter:0.000} px/frame²");
        worst.Should().BeLessThan(0.6);
        jitter.Should().BeLessThan(0.5);
    }

    /// <summary>The middle of the bright parts of a picture (the square, not the background round it).</summary>
    private static (double X, double Y) Brightest(SKBitmap bitmap)
    {
        double x = 0, y = 0, total = 0;
        var pixels = bitmap.GetPixelSpan();
        for (var row = 0; row < bitmap.Height; row++)
        {
            for (var column = 0; column < bitmap.Width; column++)
            {
                var value = pixels[(((row * bitmap.Width) + column) * 4) + 1];
                if (value > 64)
                {
                    x += value * (column + 0.5);
                    y += value * (row + 0.5);
                    total += value;
                }
            }
        }

        return (x / total, y / total);
    }

    [Fact]
    public void Motion_blur_looks_the_same_on_the_graphics_card_and_the_processor()
    {
        using var gl = OffscreenGl.TryCreate();
        Assert.SkipWhen(gl is null, "No OpenGL here.");
        var look = Looks.Apply(Project(fps: 30), Looks.Find(Looks.SoftId)!);
        var project = AutoZoom.AddZoom(look, Zoom(1, 4, blur: 1, x: 0.3, y: 0.4));
        foreach (var time in new[] { 1.1, 1.3, 1.5, 4.5 })
        {
            using var cpu = new SKBitmap(new SKImageInfo(640, 360, SKColorType.Bgra8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(cpu))
            {
                new FrameCompositor(new PatternFrames()).Render(canvas, project, time);
            }

            using var surface = gl!.Surface(640, 360);
            new FrameCompositor(new PatternFrames()).Render(surface.Canvas, project, time);
            using var gpu = gl.Read(surface, 640, 360);
            var mean = StudioLookTests.Mean(cpu, gpu);
            output.WriteLine($"at {time} s: {mean:0.000} levels on average");
            mean.Should().BeLessThan(1);
        }
    }

    /// <summary>
    /// What chose the method (docs\TESTING.md): 1080p, a zoom in motion, the two ways of blurring it on the processor and
    /// the graphics card. A directional blur shader reads one layer several times per pixel; sub-frames draw the picture
    /// through several cameras, each over the last at a falling alpha. Not a test: it prints its numbers.
    /// </summary>
    [Fact(Explicit = true)]
    public void Motion_blur_methods_measured()
    {
        const int size = 1080;
        var project = ProjectFactory.AddRecording(VideoProject.Empty(new ProjectCanvas(1920, size, 30)), Asset(10, 1920, size, 30), 0);
        project = Looks.Apply(project, Looks.Find(Looks.SoftId)!);
        var zoomed = AutoZoom.AddZoom(project, Zoom(1, 4, blur: 1));
        var frames = new MadeBeforehand(new PatternFrames(), Asset(10, 1920, size, 30), 8);
        using var gl = OffscreenGl.TryCreate();

        double Timed(Action draw, int repeat = 8)
        {
            draw();
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < repeat; i++)
            {
                draw();
            }

            return watch.Elapsed.TotalMilliseconds / repeat;
        }

        void SubFrames(SKCanvas canvas, int samples)
        {
            // What sub-frames do: the same pictures drawn through each camera, averaged as they go.
            var compositor = new FrameCompositor(frames);
            canvas.Clear(SKColors.Black);
            for (var i = 0; i < samples; i++)
            {
                canvas.SaveLayer(new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(255.0 / (i + 1))) });
                compositor.Render(canvas, project, 1.3 + (i * 0.001));
                canvas.Restore();
            }
        }

        foreach (var samples in new[] { 8, 16, 32 })
        {
            var shader = new FrameCompositor(frames) { MotionSamples = samples };
            using var cpu = SKSurface.Create(new SKImageInfo(1920, size, SKColorType.Bgra8888, SKAlphaType.Premul));
            var cpuShader = Timed(() =>
            {
                shader.Render(cpu.Canvas, zoomed, 1.3);
                cpu.Flush();
            });
            var cpuSub = Timed(() =>
            {
                SubFrames(cpu.Canvas, samples);
                cpu.Flush();
            });
            output.WriteLine($"{samples} samples, processor: shader {cpuShader:0} ms, sub-frames {cpuSub:0} ms");
            if (gl is not null)
            {
                using var surface = gl.Surface(1920, size);
                var gpuShader = Timed(() =>
                {
                    shader.Render(surface.Canvas, zoomed, 1.3);
                    surface.Flush();
                    gl.Context.Submit(synchronous: true);
                });
                var gpuSub = Timed(() =>
                {
                    SubFrames(surface.Canvas, samples);
                    surface.Flush();
                    gl.Context.Submit(synchronous: true);
                });
                output.WriteLine($"{samples} samples, graphics card: shader {gpuShader:0.0} ms, sub-frames {gpuSub:0.0} ms");
            }
        }

        // No motion, for what the look alone costs.
        var plain = new FrameCompositor(frames);
        using var memory = SKSurface.Create(new SKImageInfo(1920, size, SKColorType.Bgra8888, SKAlphaType.Premul));
        output.WriteLine($"look only, processor: {Timed(() => { plain.Render(memory.Canvas, project, 1.3); memory.Flush(); }):0.0} ms");
        var none = ProjectFactory.AddRecording(VideoProject.Empty(new ProjectCanvas(1920, size, 30)), Asset(10, 1920, size, 30), 0);
        output.WriteLine($"no look, processor: {Timed(() => { plain.Render(memory.Canvas, none, 1.3); memory.Flush(); }):0.0} ms");

        // Each part of a look on its own, on the processor: the picture scaled to its place, then its corners, then the rest.
        var scaled = Looks.WithScreenPlacement(none, s => s with { Size = 0.88 });
        output.WriteLine($"picture scaled only, processor: {Timed(() => { plain.Render(memory.Canvas, scaled, 1.3); memory.Flush(); }):0.0} ms");
        var rounded = Looks.WithScreenPlacement(none, s => s with { Size = 0.88, CornerRadius = 20 });
        output.WriteLine($"scaled with corners, processor: {Timed(() => { plain.Render(memory.Canvas, rounded, 1.3); memory.Flush(); }):0.0} ms");
        var shadow = Looks.WithScreenPlacement(none, s => s with { Size = 0.88, CornerRadius = 20, Shadow = new PlacementShadow(0, 18, 36, "#101828", 0.35) });
        output.WriteLine($"with a shadow, kept between frames, processor: {Timed(() => { plain.Render(memory.Canvas, shadow, 1.3); memory.Flush(); }):0.0} ms");
        output.WriteLine($"with a shadow, worked out each frame, processor: {Timed(() => { new FrameCompositor(frames).Render(memory.Canvas, shadow, 1.3); memory.Flush(); }):0.0} ms");
    }
}

/// <summary>A few pictures made once and handed out in turn, so timing a draw doesn't time making its picture.</summary>
internal sealed class MadeBeforehand : IFrameProvider
{
    private readonly List<VideoFrame> _frames = [];
    private int _next;

    public MadeBeforehand(IFrameProvider source, MediaAsset asset, int count)
    {
        var clip = new Clip { Id = "made", AssetId = asset.Id, Stream = asset.Streams[0].Index, SourceOut = asset.Duration };
        for (var i = 0; i < count; i++)
        {
            _frames.Add(source.Frame(clip, asset, i / 30.0)!);
        }
    }

    public VideoFrame? Frame(Clip clip, MediaAsset asset, double sourceTime) => _frames[_next++ % _frames.Count].AddRef();
}

/// <summary>Timed, so it runs alone, after the other tests of the assembly.</summary>
[Collection(nameof(PreviewAcceptance))]
public sealed class MotionBudgetTests(ITestOutputHelper output)
{
    /// <summary>
    /// What a look and motion blur add to an export (docs\TESTING.md): 10 s at 1080p30 with the software encoder, as
    /// recorded, then with a look, then with a look and two zooms. Not a test: it prints its numbers.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task Export_times_of_a_look_measured()
    {
        Media.Tests.TestMedia.Require();
        using var media = new TestMediaFiles("cashgear-export-cost-");
        var video = media.Stamped("Rec.mp4", 10, new Core.Recording.VideoFormat(1920, 1080, 30));
        var plain = ProjectFactory.FromRecording(ProjectFactory.ReadAsset(video));
        var look = Looks.Apply(plain, Looks.Find(Looks.SoftId)!);
        ZoomItem Zoom(string id, double start) => new() { Id = id, Start = start, Length = 3, Scale = 2, Focus = ZoomFocus.Fixed, MotionBlur = ZoomItem.DefaultMotionBlur };
        var zooms = AutoZoom.AddZoom(AutoZoom.AddZoom(look, Zoom("a", 1)), Zoom("b", 5));
        var exporter = new Export.ProjectExporter(new SoftwareEncoders(), Microsoft.Extensions.Logging.Abstractions.NullLogger<Export.ProjectExporter>.Instance);
        foreach (var (name, project) in new[] { ("as recorded", plain), ("with a look", look), ("with a look and two zooms", zooms) })
        {
            var path = Path.Combine(media.Root, name.Replace(' ', '-') + ".mp4");
            var result = await exporter.ExportAsync(new Export.ExportRequest(project, Export.ExportPresets.Mp4H264Project, Core.Recording.QualityPreset.High, path), ct: TestContext.Current.CancellationToken);
            output.WriteLine($"{name}: {result.Elapsed.TotalSeconds:0.0} s for {result.Frames} frames, {result.Elapsed.TotalMilliseconds / result.Frames:0.0} ms a frame");
        }
    }

    /// <summary>
    /// A 1080p30 project with a look and motion blur previews at full rate: frames drawn on the graphics card (through
    /// the hidden context) at the preview's size, a picture uploaded each time, within a frame's time while it zooms.
    /// </summary>
    [Fact]
    public void A_1080p30_project_with_a_look_and_motion_blur_previews_at_full_rate()
    {
        using var gl = OffscreenGl.TryCreate();
        Assert.SkipWhen(gl is null, "No OpenGL here.");
        var asset = new MediaAsset("rec", @"C:\nowhere\Rec.mp4", 10, [new AssetStream(0, StreamKind.Video, null, 0, 10, 1920, 1080, 30)]);
        var look = Looks.Apply(ProjectFactory.AddRecording(VideoProject.Empty(new ProjectCanvas(1920, 1080, 30)), asset, 0), Looks.Find(Looks.SoftId)!);
        var project = AutoZoom.AddZoom(look, new ZoomItem { Id = "z", Start = 1, Length = 4, Scale = 2, Focus = ZoomFocus.Fixed, MotionBlur = 0.5, EaseIn = 1, EaseOut = 1 });

        // Pictures made beforehand, as the decoder thread has them ready: only the drawing is timed.
        var compositor = new FrameCompositor(new MadeBeforehand(new PatternFrames(), asset, 8));
        using var surface = gl!.Surface(1280, 720);
        var draws = new List<double>();
        for (var n = 0; n < 150; n++)
        {
            var time = 1 + (n / 30.0);
            var watch = Stopwatch.StartNew();
            var canvas = surface.Canvas;
            canvas.Save();
            canvas.Scale(1280f / 1920, 720f / 1080);
            compositor.Render(canvas, project, time);
            canvas.Restore();
            surface.Flush();
            gl.Context.Submit(synchronous: true);
            draws.Add(watch.Elapsed.TotalMilliseconds);
        }

        var sorted = draws.Skip(5).Order().ToList();
        output.WriteLine($"1080p look with motion blur on the graphics card: {sorted.Average():0.0} ms a frame on average, {sorted[(int)(sorted.Count * 0.95)]:0.0} ms at the 95th percentile, {sorted[^1]:0.0} ms at most");
        sorted.Average().Should().BeLessThan(33.3);
        sorted[(int)(sorted.Count * 0.95)].Should().BeLessThan(33.3);
    }
}
