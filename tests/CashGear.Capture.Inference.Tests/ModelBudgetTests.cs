using System.Diagnostics;
using System.IO;

namespace CashGear.Capture.Inference.Tests;

public sealed class ModelBudgetTests(ITestOutputHelper output)
{
    internal static string? ModelPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "models", InferenceModels.PersonFinder.File);
        return File.Exists(path) ? path : null;
    }

    /// <summary>The most pictures a second at 720p in a few short runs, through the graphics card.</summary>
    private double Rate()
    {
        using var segmenter = new PersonSegmenter(ModelPath()!);
        const int width = 1280;
        const int height = 720;
        var pixels = new byte[width * height * 4];
        new Random(1).NextBytes(pixels);
        var mask = new byte[segmenter.MaskSize * segmenter.MaskSize];
        segmenter.Segment(pixels, width, height, width * 4, mask);
        var rate = 0.0;
        for (var run = 0; run < 5; run++)
        {
            var watch = Stopwatch.StartNew();
            const int frames = 60;
            for (var n = 0; n < frames; n++)
            {
                segmenter.Segment(pixels, width, height, width * 4, mask);
            }

            rate = Math.Max(rate, frames / watch.Elapsed.TotalSeconds);
        }

        output.WriteLine($"{rate:0} pictures a second at 720p, on {(segmenter.UsesGpu ? "the graphics card" : "the processor")} {segmenter.GpuProblem}");
        segmenter.UsesGpu.Should().BeTrue("the budget is through DirectML: {0}", segmenter.GpuProblem);
        return rate;
    }

    /// <summary>Every run: it is on the graphics card, and not far off. Other test assemblies run side by side, so this can't time it.</summary>
    [Fact]
    public void The_person_finder_runs_on_the_graphics_card()
    {
        Assert.SkipWhen(ModelPath() is null, "tools\\get-inference-models.ps1 hasn't been run.");
        Rate().Should().BeGreaterThan(10);
    }

    /// <summary>
    /// Phase 20's budget: at least 30 pictures a second at 720p through DirectML on the reference PC's GTX 1650. Timed,
    /// so it runs when asked for, on its own: <c>--filter-method "*keeps_up*" --explicit only</c>.
    /// </summary>
    [Fact(Explicit = true)]
    public void The_person_finder_keeps_up_with_720p_pictures()
    {
        Assert.SkipWhen(ModelPath() is null, "tools\\get-inference-models.ps1 hasn't been run.");
        Rate().Should().BeGreaterThanOrEqualTo(30);
    }
}
