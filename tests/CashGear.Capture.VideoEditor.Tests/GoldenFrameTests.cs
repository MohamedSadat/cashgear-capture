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
}
