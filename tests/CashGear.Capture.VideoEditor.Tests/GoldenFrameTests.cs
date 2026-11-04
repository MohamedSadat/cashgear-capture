using System.IO;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Rendering;
using SkiaSharp;

namespace CashGear.Capture.VideoEditor.Tests;

/// <summary>
/// <c>Fixtures\v1.1.1</c> was written once by the 1.1.1 code and never again: a project with every kind of item 1.1.1
/// knew (<c>Project.cgcap</c>, version 1, with its metadata file; the media isn't needed, <see cref="PatternFrames"/>
/// draws it) and the frames 1.1.1 drew from it after opening it. Every later version must open it and draw the same
/// pixels. (Its second caption track has a box: 1.1.1 reads a style without one as having the default box.)
/// </summary>
public sealed class GoldenFrameTests : IDisposable
{
    private static readonly string Fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "v1.1.1");

    private readonly string _cache = Directory.CreateTempSubdirectory("cashgear-golden-").FullName;

    public void Dispose() => Directory.Delete(_cache, recursive: true);

    /// <summary>The moments with a golden frame: (time, the part of the canvas's size it was drawn at).</summary>
    public static TheoryData<double, double> Frames => new()
    {
        { 0.25, 1 }, { 1.5, 1 }, { 2.3, 1 }, { 3.0, 1 }, { 4.6, 1 }, { 5.2, 1 }, { 6.6, 1 }, { 7.5, 1 }, { 9.5, 1 }, { 10.2, 1 }, { 12.0, 1 }, { 15.0, 1 }, { 17.0, 1 }, { 19.9, 1 },
        { 3.0, 0.5 }, { 10.2, 0.5 },
    };

    /// <summary>
    /// <c>Fixtures\phase-20</c> was written once by the Phase 20 code (tag <c>phase-20-done</c>) and never again: <c>Project.cgcap</c>
    /// has a gradient, a framed picture with corners and a shadow, zooms with motion blur and a spring, layouts, a webcam
    /// background, the sharp pointer and captions, and <c>Vertical.cgcap</c> a 9:16 canvas following the pointer over the
    /// blurred screen (version 2). Its frames are what that code drew after opening them.
    /// </summary>
    public static TheoryData<string, string, double> Phase20Frames => new()
    {
        { "Project", "frame", 0.6 }, { "Project", "frame", 1.2 }, { "Project", "frame", 2.5 }, { "Project", "frame", 3.5 }, { "Project", "frame", 4.1 },
        { "Project", "frame", 5.2 }, { "Project", "frame", 6.5 }, { "Project", "frame", 7.9 }, { "Project", "frame", 9.2 }, { "Project", "frame", 10.5 },
        { "Project", "frame", 12.2 }, { "Project", "frame", 13.5 }, { "Project", "frame", 14.2 }, { "Project", "frame", 16.5 }, { "Project", "frame", 18.5 },
        { "Vertical", "vertical", 0.5 }, { "Vertical", "vertical", 6.0 }, { "Vertical", "vertical", 12.0 },
    };

    /// <summary>
    /// <c>Fixtures\phase-21</c> was written once by the Phase 21 code (tag <c>phase-21-done</c>) and never again: a project
    /// (version 3) whose recording has a transcript, cut through it (a crossfading join on the sound), with a word corrected
    /// nowhere, a speed change, a freeze, annotations, a zoom and captions. Its frames are what that code drew after opening it.
    /// </summary>
    public static TheoryData<double> Phase21Frames => new() { 0.4, 1.1, 2.2, 3.3, 4.4, 5.5, 6.6, 7.7, 8.8, 9.9, 11.0, 12.1, 14.5, 16.0, 17.5 };

    private VideoProject Open() => ProjectFile.Open(Path.Combine(Fixture, "Project.cgcap"), _cache).Project;

    internal static SKBitmap Render(VideoProject project, double time, double scale = 1)
    {
        var width = (int)Math.Round(project.Canvas.Width * scale);
        var height = (int)Math.Round(project.Canvas.Height * scale);
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Scale((float)width / project.Canvas.Width, (float)height / project.Canvas.Height);
        new FrameCompositor(new PatternFrames()).Render(canvas, project, time);
        return bitmap;
    }

    [Theory]
    [MemberData(nameof(Frames))]
    public void A_1_1_1_project_draws_the_same_pixels(double time, double scale)
    {
        var name = FormattableString.Invariant($"frame-{time:00.00}{(scale < 1 ? "-half" : "")}.png");
        using var golden = SKBitmap.Decode(Path.Combine(Fixture, name)).Copy(SKColorType.Bgra8888);
        using var drawn = Render(Open(), time, scale);
        (drawn.Width, drawn.Height).Should().Be((golden.Width, golden.Height));
        var differing = 0;
        for (var y = 0; y < golden.Height; y++)
        {
            for (var x = 0; x < golden.Width; x++)
            {
                if (golden.GetPixel(x, y) != drawn.GetPixel(x, y))
                {
                    differing++;
                }
            }
        }

        differing.Should().Be(0, "every pixel of {0} is as 1.1.1 drew it", name);
    }

    [Theory]
    [MemberData(nameof(Phase20Frames))]
    public void A_phase_20_project_draws_the_same_pixels(string file, string prefix, double time)
    {
        using var turn = new CashGear.Capture.Tests.MachineTurn(); // motion blur works every core
        var folder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "phase-20");
        var name = FormattableString.Invariant($"{prefix}-{time:00.00}.png");
        using var golden = SKBitmap.Decode(Path.Combine(folder, name)).Copy(SKColorType.Bgra8888);
        using var drawn = Render(ProjectFile.Open(Path.Combine(folder, file + ".cgcap"), _cache).Project, time);
        (drawn.Width, drawn.Height).Should().Be((golden.Width, golden.Height));
        var differing = 0;
        for (var y = 0; y < golden.Height; y++)
        {
            for (var x = 0; x < golden.Width; x++)
            {
                if (golden.GetPixel(x, y) != drawn.GetPixel(x, y))
                {
                    differing++;
                }
            }
        }

        differing.Should().Be(0, "every pixel of {0} is as Phase 20 drew it", name);
    }

    [Theory]
    [MemberData(nameof(Phase21Frames))]
    public void A_phase_21_project_draws_the_same_pixels(double time)
    {
        using var turn = new CashGear.Capture.Tests.MachineTurn(); // a look's shadow and a blurred area
        var folder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "phase-21");
        var name = FormattableString.Invariant($"frame-{time:00.00}.png");
        using var golden = SKBitmap.Decode(Path.Combine(folder, name)).Copy(SKColorType.Bgra8888);
        using var drawn = Render(ProjectFile.Open(Path.Combine(folder, "Project.cgcap"), _cache).Project, time);
        (drawn.Width, drawn.Height).Should().Be((golden.Width, golden.Height));
        var differing = 0;
        for (var y = 0; y < golden.Height; y++)
        {
            for (var x = 0; x < golden.Width; x++)
            {
                if (golden.GetPixel(x, y) != drawn.GetPixel(x, y))
                {
                    differing++;
                }
            }
        }

        differing.Should().Be(0, "every pixel of {0} is as Phase 21 drew it", name);
    }

    [Fact]
    public void A_phase_21_project_keeps_its_transcript_and_its_cut()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Fixtures", "phase-21");
        var project = ProjectFile.Open(Path.Combine(folder, "Project.cgcap"), _cache).Project;
        project.Asset("rec")!.Transcript!.Words.Should().HaveCount(20);
        var view = CashGear.Capture.VideoEditor.Editing.TranscriptView.Build(project);
        view.Words.Where(w => w.IsCut).Select(w => w.Word.Text).Should().Contain("um");
        var sound = project.Tracks.First(t => t.HasSound).Clips.ToList();
        var pictures = project.Tracks.First(t => t.Kind == TrackKind.Video).Clips.ToList();
        (sound[0].SourceOut - pictures[0].SourceOut).Should().BeApproximately(CashGear.Capture.VideoEditor.Editing.TranscriptEdits.JoinLength / 2, 1e-3, "the sound goes on by half a join into what was cut");
    }
}
