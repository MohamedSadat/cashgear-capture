using System.Diagnostics;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Editor.Batch;
using CashGear.Capture.Editor.Effects;
using CashGear.Capture.Editor.Imaging;
using SkiaSharp;

namespace CashGear.Capture.Editor.Tests.Batch;

/// <summary>A folder of test pictures that is deleted afterwards.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder(string name)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cashgear-tests", name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Write(string name, SKImage image, ImageFileFormat format = ImageFileFormat.Png)
    {
        var path = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        ImageFiles.Write(image, stream, format);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

/// <summary>An effect that notes how many images are being worked on at once.</summary>
internal sealed record CountingEffect(int DelayMs) : ImageEffect
{
    private static int _current;
    private static int _most;

    public static int Most => Volatile.Read(ref _most);

    public static void Reset() => (_current, _most) = (0, 0);

    public override string Name => "counting";

    public override EffectOutput Apply(SKImage source)
    {
        var now = Interlocked.Increment(ref _current);
        int seen;
        while (now > (seen = Volatile.Read(ref _most)) && Interlocked.CompareExchange(ref _most, now, seen) != seen)
        {
        }

        Thread.Sleep(DelayMs);
        Interlocked.Decrement(ref _current);
        return new EffectOutput(SkiaImaging.Copy(source, SKRectI.Create(source.Width, source.Height)), default);
    }

    public override ImageEffect Normalize() => this;
}

public sealed class BatchProcessorTests : IDisposable
{
    private readonly TempFolder _input = new("batch-in");
    private readonly TempFolder _output = new("batch-out");

    public void Dispose()
    {
        _input.Dispose();
        _output.Dispose();
    }

    private static Task<BatchResult> Run(IReadOnlyList<string> files, string output, BatchOptions options, IReadOnlyList<ImageEffect>? effects = null) =>
        BatchProcessor.RunAsync(new BatchJob(files, output, options, effects ?? []), null, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Each_file_is_resized_changed_converted_and_renamed()
    {
        using var red = TestImages.Solid(400, 200, SKColors.Red);
        string[] files = [_input.Write("one.png", red), _input.Write("two.png", red)];
        var options = new BatchOptions(Resize: ResizeMode.Percent, Percent: 50, Format: BatchFormat.Jpeg, JpegQuality: 95, NameTemplate: "{counter}_{name}_{width}x{height}");

        var result = await Run(files, _output.Path, options, [new GrayscaleEffect()]);

        result.Failures.Should().BeEmpty();
        result.Written.Select(Path.GetFileName).Should().Equal("1_one_200x100.jpg", "2_two_200x100.jpg");
        using var back = ImageFiles.Read(result.Written[0]);
        back.Width.Should().Be(200);
        TestImages.Pixel(back, 100, 50).ShouldBeCloseTo(new SKColor(54, 54, 54), tolerance: 4);
        Directory.GetFiles(_output.Path, "*.tmp").Should().BeEmpty();
    }

    [Theory]
    [InlineData(ResizeMode.FitWithin, 300, 300, true, 300, 150)]
    [InlineData(ResizeMode.Width, 100, 0, true, 100, 50)]
    [InlineData(ResizeMode.Height, 0, 50, true, 100, 50)]
    [InlineData(ResizeMode.Width, 800, 0, true, 400, 200)]
    [InlineData(ResizeMode.Width, 800, 0, false, 800, 400)]
    [InlineData(ResizeMode.None, 10, 10, true, 400, 200)]
    public void Sizes_keep_their_proportions_and_only_shrink_if_asked(ResizeMode mode, int width, int height, bool onlyShrink, int expectedWidth, int expectedHeight)
    {
        var options = new BatchOptions(Resize: mode, Width: Math.Max(1, width), Height: Math.Max(1, height), OnlyShrink: onlyShrink);

        options.ResizedSize(new SKSizeI(400, 200)).Should().Be(new SKSizeI(expectedWidth, expectedHeight));
    }

    [Fact]
    public async Task Formats_are_kept_or_changed_and_bmp_is_readable()
    {
        using var image = SkiaImaging.Draw(30, 20, c =>
        {
            c.Clear(SKColors.Transparent);
            using var blue = new SKPaint { Color = SKColors.Blue };
            c.DrawRect(0, 0, 15, 20, blue);
        });
        string[] files = [_input.Write("a.png", image), _input.Write("b.jpg", image, ImageFileFormat.Jpeg)];

        var kept = await Run(files, Path.Combine(_output.Path, "kept"), new BatchOptions());
        var bmp = await Run(files, Path.Combine(_output.Path, "bmp"), new BatchOptions(Format: BatchFormat.Bmp));

        kept.Written.Select(Path.GetExtension).Should().Equal(".png", ".jpg");
        using (var png = ImageFiles.Read(kept.Written[0]))
        {
            TestImages.Pixel(png, 25, 10).Alpha.Should().Be(0, "PNG keeps transparency");
        }

        using var fromBmp = ImageFiles.Read(bmp.Written[0]);
        fromBmp.Width.Should().Be(30);
        TestImages.Pixel(fromBmp, 5, 5).Should().Be(SKColors.Blue);
        TestImages.Pixel(fromBmp, 25, 10).Should().Be(SKColors.White, "BMP gets white behind transparent parts");
    }

    [Fact]
    public async Task Taken_names_are_numbered_skipped_or_overwritten()
    {
        using var image = TestImages.Solid(10, 10, SKColors.Green);
        string[] files = [_input.Write("x/shot.png", image), _input.Write("y/shot.png", image)];
        File.WriteAllText(Path.Combine(_output.Path, "shot.png"), "already there");

        var numbered = await Run(files, _output.Path, new BatchOptions());
        numbered.Written.Select(Path.GetFileName).Should().BeEquivalentTo("shot (2).png", "shot (3).png"); // in the order they finish

        var skipped = await Run(files, _output.Path, new BatchOptions(Existing: ExistingFiles.Skip, NameTemplate: "{name} ({counter})"));
        skipped.Skipped.Should().Be(1, "\"shot (2)\" exists; \"shot (1)\" doesn't");
        skipped.Written.Select(Path.GetFileName).Should().Equal("shot (1).png");

        var overwritten = await Run(files, _output.Path, new BatchOptions(Existing: ExistingFiles.Overwrite));
        overwritten.Written.Select(Path.GetFileName).Should().BeEquivalentTo("shot.png", "shot (2).png");
        using var replaced = ImageFiles.Read(Path.Combine(_output.Path, "shot.png"));
        replaced.Width.Should().Be(10);
    }

    [Fact]
    public async Task A_bad_file_is_reported_and_the_others_still_done()
    {
        using var image = TestImages.Solid(10, 10, SKColors.Green);
        var bad = Path.Combine(_input.Path, "broken.png");
        File.WriteAllText(bad, "not a picture");
        string[] files = [_input.Write("good1.png", image), bad, _input.Write("good2.png", image), Path.Combine(_input.Path, "gone.png")];

        var result = await Run(files, _output.Path, new BatchOptions());

        result.Written.Should().HaveCount(2);
        result.Failures.Select(f => Path.GetFileName(f.File)).Should().Equal("broken.png", "gone.png");
        result.Failures[0].Message.Should().Contain("broken.png");
    }

    [Fact]
    public async Task No_more_than_the_chosen_number_of_images_are_worked_on_at_once()
    {
        using var image = TestImages.Solid(20, 20, SKColors.Green);
        var files = Enumerable.Range(0, 24).Select(i => _input.Write($"f{i}.png", image)).ToList();
        CountingEffect.Reset();

        var result = await Run(files, _output.Path, new BatchOptions(Parallelism: 3), [new CountingEffect(25)]);

        result.Written.Should().HaveCount(24);
        CountingEffect.Most.Should().BeInRange(2, 3, "several at once, never more than three");
    }

    [Fact]
    public async Task Cancelling_stops_the_batch_and_leaves_only_whole_files()
    {
        using var image = TestImages.Noise(300, 200);
        var files = Enumerable.Range(0, 60).Select(i => _input.Write($"f{i}.png", image)).ToList();
        using var cancel = new CancellationTokenSource();
        var progress = new SyncProgress(p =>
        {
            if (p.Finished >= 5)
            {
                cancel.Cancel();
            }
        });

        var job = new BatchJob(files, _output.Path, new BatchOptions(Parallelism: 2), [new CountingEffect(15)]);
        var result = await BatchProcessor.RunAsync(job, progress, cancel.Token);

        result.Cancelled.Should().BeTrue();
        result.Written.Count.Should().BeLessThan(60);
        Directory.GetFiles(_output.Path).Should().HaveCount(result.Written.Count, "no temporary or partial files are left");
        foreach (var file in result.Written)
        {
            using var whole = ImageFiles.Read(file);
            whole.Width.Should().Be(300);
        }
    }

    [Fact]
    public void Pictures_are_turned_upright_by_their_exif_orientation()
    {
        using var image = TestImages.Noise(3, 2);

        using var right = ImageFiles.Upright(image, SKEncodedOrigin.RightTop);
        using var left = ImageFiles.Upright(image, SKEncodedOrigin.LeftBottom);
        using var mirrored = ImageFiles.Upright(image, SKEncodedOrigin.TopRight);
        using var transposed = ImageFiles.Upright(image, SKEncodedOrigin.LeftTop);

        right.Width.Should().Be(2);
        TestImages.Pixel(right, 1, 0).Should().Be(TestImages.Pixel(image, 0, 0), "turned a quarter clockwise");
        TestImages.Pixel(left, 0, 2).Should().Be(TestImages.Pixel(image, 0, 0), "turned a quarter counter-clockwise");
        TestImages.Pixel(mirrored, 2, 0).Should().Be(TestImages.Pixel(image, 0, 0));
        TestImages.Pixel(transposed, 1, 2).Should().Be(TestImages.Pixel(image, 2, 1));
    }

    [Fact]
    public void Name_templates_are_checked_and_expanded()
    {
        var context = new BatchNameContext("Shot:1", 7, 120, new DateTime(2026, 10, 21, 9, 5, 3), new SKSizeI(640, 480));

        BatchNames.Expand("{yyyy}-{MM}-{dd}_{HH}{mm}{ss}_{counter}_{name}_{width}", context).Should().Be("2026-10-21_090503_007_Shot_1_640");
        BatchNames.Expand("   ", context).Should().Be("Shot_1");
        BatchNames.TryValidate("{name", out var error).Should().BeFalse();
        error.Should().Contain("never closed");
        BatchNames.TryValidate("{nme}", out error).Should().BeFalse();
        BatchNames.TryValidate("{name}_{counter}", out _).Should().BeTrue();
        BatchNames.Expand("CON", context).Should().Be("CON_");
    }

    /// <summary>Reports on the worker's thread at once (Progress&lt;T&gt; would post to the thread pool).</summary>
    private sealed class SyncProgress(Action<BatchProgress> report) : IProgress<BatchProgress>
    {
        public void Report(BatchProgress value) => report(value);
    }
}

[CollectionDefinition(nameof(BatchAcceptance), DisableParallelization = true)]
public sealed class BatchAcceptance;

/// <summary>The acceptance criterion: 500 screenshots go through with memory that stays flat.</summary>
[Collection(nameof(BatchAcceptance))]
public sealed class BatchAcceptanceTests : IDisposable
{
    private readonly TempFolder _input = new("batch500-in");
    private readonly TempFolder _output = new("batch500-out");

    public void Dispose()
    {
        _input.Dispose();
        _output.Dispose();
    }

    [Fact]
    public async Task Five_hundred_screenshots_with_flat_memory()
    {
        var files = MakeScreenshots(_input.Path, 500);
        var job = new BatchJob(
            files,
            _output.Path,
            new BatchOptions(Resize: ResizeMode.Percent, Percent: 75, Format: BatchFormat.Jpeg, NameTemplate: "{counter}_{name}"),
            [new ShadowEffect(), new WatermarkEffect(new Watermark(Text: "Batch", Position: Anchor.BottomRight))]);
        var samples = new List<(int Finished, long Bytes)>();
        var progress = new Sampler(p =>
        {
            using var process = Process.GetCurrentProcess();
            lock (samples)
            {
                samples.Add((p.Finished, process.PrivateMemorySize64));
            }
        });

        var clock = Stopwatch.StartNew();
        var result = await BatchProcessor.RunAsync(job, progress, TestContext.Current.CancellationToken);
        clock.Stop();

        result.Failures.Should().BeEmpty();
        result.Written.Should().HaveCount(500);
        Path.GetFileName(result.Written[^1]).Should().Be("500_shot499.jpg");
        var early = samples.Where(s => s.Finished is >= 50 and <= 200).Select(s => s.Bytes).DefaultIfEmpty(0).Max();
        var late = samples.Where(s => s.Finished >= 350).Select(s => s.Bytes).DefaultIfEmpty(0).Max();
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"500 screenshots in {clock.Elapsed.TotalSeconds:0.0} s; private memory peak {early / 1048576} MB (50–200 done), {late / 1048576} MB (350–500 done); {samples.Count} samples.");
        samples.Count.Should().BeGreaterThan(10);
        (late - early).Should().BeLessThan(64L * 1024 * 1024, "memory doesn't grow with the number of images");
    }

    /// <summary>1280 × 800 pictures that look like screenshots: flat panels, lines of "text", a different one each time.</summary>
    private static List<string> MakeScreenshots(string folder, int count)
    {
        var files = new string[count];
        Parallel.For(0, count, i =>
        {
            using var shot = SkiaImaging.Draw(1280, 800, c =>
            {
                c.Clear(new SKColor(0xF3, 0xF3, 0xF3));
                using var panel = new SKPaint { Color = SKColors.White };
                using var ink = new SKPaint { Color = new SKColor(0x20, 0x20, 0x20) };
                using var accent = new SKPaint { Color = new SKColor(0x00, 0x78, 0xD4) };
                c.DrawRect(0, 0, 1280, 40, accent);
                c.DrawRect(40, 80, 1200, 680, panel);
                for (var line = 0; line < 30; line++)
                {
                    var width = 200 + (((i * 37) + (line * 53)) % 900);
                    c.DrawRect(80, 110 + (line * 21), width, 9, ink);
                }
            });
            var path = Path.Combine(folder, $"shot{i:000}.png");
            using var stream = File.Create(path);
            ImageFiles.Write(shot, stream, ImageFileFormat.Png);
            files[i] = path;
        });
        return [.. files];
    }

    private sealed class Sampler(Action<BatchProgress> sample) : IProgress<BatchProgress>
    {
        public void Report(BatchProgress value) => sample(value);
    }
}
