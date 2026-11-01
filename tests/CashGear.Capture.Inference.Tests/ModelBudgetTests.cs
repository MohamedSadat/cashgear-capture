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

    [Fact]
    public void The_person_finder_keeps_up_with_720p_pictures()
    {
        Assert.SkipWhen(ModelPath() is null, "tools\\get-inference-models.ps1 hasn't been run.");
        using var segmenter = new PersonSegmenter(ModelPath()!);
        const int width = 1280;
        const int height = 720;
        var pixels = new byte[width * height * 4];
        new Random(1).NextBytes(pixels);
        var mask = new byte[segmenter.MaskSize * segmenter.MaskSize];
        segmenter.Segment(pixels, width, height, width * 4, mask);
        var watch = Stopwatch.StartNew();
        const int frames = 120;
        for (var n = 0; n < frames; n++)
        {
            segmenter.Segment(pixels, width, height, width * 4, mask);
        }

        var rate = frames / watch.Elapsed.TotalSeconds;
        output.WriteLine($"{rate:0} pictures a second at 720p, on {(segmenter.UsesGpu ? "the graphics card" : "the processor")} {segmenter.GpuProblem}");
        segmenter.UsesGpu.Should().BeTrue("the budget is through DirectML: {0}", segmenter.GpuProblem);
        rate.Should().BeGreaterThan(30);
    }
}
