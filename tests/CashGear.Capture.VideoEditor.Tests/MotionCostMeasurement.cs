using System.Diagnostics;
using System.IO;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Rendering;
using SkiaSharp;

namespace CashGear.Capture.VideoEditor.Tests;

/// <summary>What this phase's drawing adds to an export's frame at 1080p, on the processor. Prints its numbers when asked for; TESTING.md has them.</summary>
public sealed class MotionCostMeasurement(ITestOutputHelper output)
{
    private static VideoProject Screen()
    {
        var asset = new MediaAsset("rec", @"C:\nowhere\Rec.mp4", 20, [new AssetStream(0, StreamKind.Video, null, 0, 20, 1920, 1080, 30)]);
        return ProjectFactory.AddRecording(VideoProject.Empty(new ProjectCanvas(1920, 1080, 30)), asset, 0);
    }

    private static double Cost(VideoProject project, double from)
    {
        using var surface = SKSurface.Create(new SKImageInfo(1920, 1080, SKColorType.Bgra8888, SKAlphaType.Premul));
        var compositor = new FrameCompositor(new PatternFrames());
        const int frames = 20;
        compositor.Render(surface.Canvas, project, from);
        var watch = Stopwatch.StartNew();
        for (var n = 1; n <= frames; n++)
        {
            compositor.Render(surface.Canvas, project, from + (n * 0.02));
            surface.Flush();
        }

        return watch.Elapsed.TotalMilliseconds / frames;
    }

    [Fact(Explicit = true)]
    public void What_this_phases_drawing_costs_an_export()
    {
        var plain = Screen();
        var clip = plain.Tracks.Single(t => t.Kind == TrackKind.Video).Clips.Single();
        var baseline = Cost(plain, 1);
        output.WriteLine($"A 1080p picture alone (the pattern's own making included): {baseline:0.0} ms");
        void Line(string what, VideoProject project, double from = 1) => output.WriteLine($"{what}: {Cost(project, from) - baseline:+0.0;-0.0} ms");

        Line("Colour effects", plain.WithItem(clip with { Effects = new ClipEffects(0.1, 0.1, 0.1, 0.1) }));
        Line("Chroma key", plain.WithItem(clip with { Effects = new ClipEffects(Key: new ChromaKey()) }));
        var cube = Path.Combine(Path.GetTempPath(), $"cashgear-cost-{Environment.ProcessId}.cube");
        var lines = new List<string> { "LUT_3D_SIZE 2" };
        for (var i = 0; i < 8; i++)
        {
            lines.Add($"{i & 1} {(i >> 1) & 1} {(i >> 2) & 1}");
        }

        File.WriteAllLines(cube, lines);
        try
        {
            var asset = Looks.PictureAsset(cube);
            Line("A look-up table", plain.WithAsset(asset).WithItem(clip with { Effects = new ClipEffects(LutAssetId: asset.Id) }));
        }
        finally
        {
            File.Delete(cube);
        }

        Line("A rotating, scaling picture", plain.WithItem(clip with { Motion = new ItemMotion { Rotation = [new(0, 0), new(20, 360)], Scale = [new(0, 0.5), new(20, 1)] } }));
        var split = TimelineEdits.Split(plain, 5);
        var second = split.Tracks.Single(t => t.Kind == TrackKind.Video).Clips.Last();
        foreach (var kind in new[] { TransitionKind.Dissolve, TransitionKind.Wipe, TransitionKind.Push, TransitionKind.Zoom, TransitionKind.Blur, TransitionKind.Circle })
        {
            Line($"A {kind} transition", TransitionEdits.SetTransition(split, second.Id, ItemEdge.Start, new ClipTransition(kind, 1, MotionEasing.Linear)), 4.2);
        }

        var (titled, _) = TemplateEdits.Insert(plain, BuiltInTemplates.All.Single(t => t.Id == "third-bar"), 0);
        Line("A lower third", titled, 2);
        var (magnified, _) = AnnotationEdits.Add(plain, AnnotationShape.Magnify, 0, 10);
        Line("A magnifier", magnified, 2);
    }
}
