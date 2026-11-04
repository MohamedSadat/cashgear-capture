using CashGear.Capture.VideoEditor.Editing;
using System.Diagnostics;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Media;
using CashGear.Capture.Media.Tests;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Playback;
using CashGear.Capture.VideoEditor.Rendering;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace CashGear.Capture.VideoEditor.Tests;

public sealed class PreviewTests : IDisposable
{
    private readonly TestMediaFiles _media = new("cashgear-preview-");

    public void Dispose() => _media.Dispose();

    [Fact]
    public void Pictures_larger_than_the_canvas_are_decoded_smaller()
    {
        var stream = new AssetStream(0, StreamKind.Video, null, 0, 10, 3840, 2160, 30);
        PreviewFrames.DecodeSize(stream, new ProjectCanvas(1920, 1080, 30)).Should().Be((1920, 1080));
        PreviewFrames.DecodeSize(stream with { Width = 640, Height = 480 }, new ProjectCanvas(1920, 1080, 30)).Should().Be((640, 480));
        PreviewFrames.DecodeSize(stream with { Width = 4000, Height = 1000 }, new ProjectCanvas(1920, 1080, 30)).Should().Be((1920, 480));
    }

    [Fact]
    public void The_preview_shows_the_frame_at_the_playhead_after_seeks_and_redraws_when_it_arrives()
    {
        TestMedia.Require();
        var file = _media.Stamped("seek.mp4", 4);
        var project = ProjectFactory.FromRecording(ProjectFactory.ReadAsset(file));
        using var frames = new PreviewFrames(NullLoggerFactory.Instance);
        var arrived = 0;
        frames.FrameReady += (_, _) => Interlocked.Increment(ref arrived);
        var stamps = new StampingFrames(frames);
        var compositor = new FrameCompositor(stamps);
        using var surface = SKSurface.Create(new SKImageInfo(320, 180));
        foreach (var time in new[] { 3.5, 0.1, 2.0 })
        {
            frames.Update(project, time, playing: false);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (compositor.Render(surface.Canvas, project, time).Missing > 0 && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(5);
            }

            stamps.Shown[^1].Should().Be((int)Math.Floor(time * 30), $"the frame at {time} s");
        }

        arrived.Should().BeGreaterThan(0);
        frames.Failures.Should().BeEmpty();
    }
}

[CollectionDefinition(nameof(PreviewAcceptance), DisableParallelization = true)]
public sealed class PreviewAcceptance;

/// <summary>Timed, so it runs alone, after the other tests of the assembly.</summary>
[Collection(nameof(PreviewAcceptance))]
public sealed class PreviewAcceptanceTests : IDisposable
{
    private readonly TestMediaFiles _media = new("cashgear-preview-acceptance-");

    public void Dispose() => _media.Dispose();

    /// <summary>
    /// Phase 20 acceptance: a 1080p30 project with a look and motion blur previews at full rate. As the test below, with
    /// real decoding, on the graphics card through the hidden OpenGL context, the zooms moving for much of the time.
    /// </summary>
    [Fact]
    public void Acceptance_a_look_and_motion_blur_preview_at_full_rate_on_the_graphics_card()
    {
        TestMedia.Require();
        Assert.SkipWhen(VideoStreamReader.HardwareProblem is not null, $"No hardware video decoding here: {VideoStreamReader.HardwareProblem}");
        using var turn = new CashGear.Capture.Tests.MachineTurn();
        using var gl = OffscreenGl.TryCreate();
        Assert.SkipWhen(gl is null, "No OpenGL here.");
        var file = _media.Stamped("hd-look.mp4", 10, new VideoFormat(1920, 1080, 30));
        var project = Looks.Apply(ProjectFactory.FromRecording(ProjectFactory.ReadAsset(file)), Looks.Find(Looks.SoftId)!);
        foreach (var (id, start) in new[] { ("a", 0.5), ("b", 3.5), ("c", 6.5) })
        {
            project = AutoZoom.AddZoom(project, new ZoomItem { Id = id, Start = start, Length = 3, Scale = 2, Focus = ZoomFocus.Fixed, MotionBlur = ZoomItem.DefaultMotionBlur, EaseIn = 1, EaseOut = 1 });
        }

        using var frames = new PreviewFrames(NullLoggerFactory.Instance, VideoDecoding.Hardware);
        var stamps = new StampingFrames(frames);
        var compositor = new FrameCompositor(stamps);
        using var surface = gl!.Surface(1280, 720);
        void Draw(double time, ref int missing)
        {
            var canvas = surface.Canvas;
            canvas.Save();
            canvas.Scale(1280f / 1920, 720f / 1080);
            missing += compositor.Render(canvas, project, time).Missing;
            canvas.Restore();
            surface.Flush();
            gl.Context.Submit(synchronous: true);
        }

        var ignored = 0;
        frames.Update(project, 0, playing: false);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        do
        {
            ignored = 0;
            Draw(0, ref ignored);
            Thread.Sleep(5);
        }
        while (ignored > 0 && DateTime.UtcNow < deadline);

        stamps.Shown.Clear();
        var moving = 0;
        var draws = new List<double>();
        var waited = 0;
        var tick = Stopwatch.StartNew();
        var next = TimeSpan.Zero;
        while (tick.Elapsed.TotalSeconds < 10)
        {
            var time = tick.Elapsed.TotalSeconds;
            frames.Update(project, time, playing: true);
            var started = tick.Elapsed;
            Draw(time, ref waited);
            draws.Add((tick.Elapsed - started).TotalMilliseconds);
            moving += compositor.Camera.Motion(project, time, 16) is null ? 0 : 1;
            next += TimeSpan.FromSeconds(1 / 60.0);
            var wait = next - tick.Elapsed;
            if (wait > TimeSpan.Zero)
            {
                Thread.Sleep(wait);
            }
        }

        var shown = stamps.Shown.Distinct().Count();
        var sorted = draws.Order().ToList();
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"1080p with a look and motion blur on the graphics card: {shown} of 300 pictures shown, {waited} draws waited for a picture, {moving} of {draws.Count} draws blurred, {sorted.Average():0.0} ms a draw on average, {sorted[(int)(sorted.Count * 0.95)]:0.0} ms at the 95th percentile, {sorted[^1]:0.0} ms at most");
        moving.Should().BeGreaterThan(100, "the camera has to be moving for this to mean anything");
        shown.Should().BeGreaterThanOrEqualTo(297, "at least 99% of the pictures are shown at 30 fps");
    }

    /// <summary>
    /// Phase 16 acceptance: the preview plays 1080p at full frame rate with hardware decoding. A 10 s 1080p30
    /// recording is played on the preview's own engine (a stopwatch clock here, as without a sound device), drawn
    /// 60 times a second into a 1280 × 720 view by the compositor; every source picture must be shown.
    /// </summary>
    [Fact]
    public void Acceptance_the_preview_plays_1080p_at_full_frame_rate_with_hardware_decoding()
    {
        TestMedia.Require();
        Assert.SkipWhen(VideoStreamReader.HardwareProblem is not null, $"No hardware video decoding here: {VideoStreamReader.HardwareProblem}");
        var file = _media.Stamped("hd.mp4", 10, new VideoFormat(1920, 1080, 30));
        var project = ProjectFactory.FromRecording(ProjectFactory.ReadAsset(file));
        using var frames = new PreviewFrames(NullLoggerFactory.Instance, VideoDecoding.Hardware);
        var stamps = new StampingFrames(frames);
        var compositor = new FrameCompositor(stamps);
        using var surface = SKSurface.Create(new SKImageInfo(1280, 720, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var engine = new PlaybackEngine(() => project, () => null, NullLogger.Instance);

        // The first picture is ready before playing starts, as the editor shows it when the project opens.
        frames.Update(project, 0, playing: false);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (compositor.Render(surface.Canvas, project, 0).Missing > 0 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(5);
        }

        engine.Play(0);
        var draw = new List<double>();
        var missing = 0;
        var tick = Stopwatch.StartNew();
        var next = TimeSpan.Zero;
        while (!engine.CheckEnd())
        {
            var time = engine.Position;
            frames.Update(project, time, playing: true);
            var started = tick.Elapsed;
            var canvas = surface.Canvas;
            canvas.Save();
            canvas.Scale(1280f / 1920, 720f / 1080);
            missing += compositor.Render(canvas, project, time).Missing;
            canvas.Restore();
            surface.Flush();
            draw.Add((tick.Elapsed - started).TotalMilliseconds);
            next += TimeSpan.FromSeconds(1 / 60.0);
            var wait = next - tick.Elapsed;
            if (wait > TimeSpan.Zero)
            {
                Thread.Sleep(wait);
            }
        }

        var shown = stamps.Shown.Distinct().Count();
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"1080p preview: {shown} of 300 pictures shown, {missing} draws waited for a picture, drawing took {draw.Average():0.0} ms on average ({draw.Max():0.0} ms at most), hardware decoding {frames.IsHardware}");
        frames.IsHardware.Should().BeTrue();
        shown.Should().BeGreaterThanOrEqualTo(297, "at least 99% of the pictures are shown at 30 fps");
    }
}

/// <summary>Notes the number stamped in each picture the compositor gets.</summary>
internal sealed class StampingFrames(IFrameProvider inner, double scale = 1) : IFrameProvider
{
    public List<int> Shown { get; } = [];

    public VideoFrame? Frame(Clip clip, MediaAsset asset, double sourceTime)
    {
        var frame = inner.Frame(clip, asset, sourceTime);
        if (frame is not null)
        {
            Shown.Add(StampedRecording.ReadStamp(frame.Pixels, frame.Width, scale));
        }

        return frame;
    }
}
