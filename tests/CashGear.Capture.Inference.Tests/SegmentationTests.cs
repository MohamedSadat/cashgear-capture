using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace CashGear.Capture.Inference.Tests;

public sealed class SegmentationTests(ITestOutputHelper output)
{
    private const int Width = 640;
    private const int Height = 480;

    private static readonly string People = Path.Combine(AppContext.BaseDirectory, "Fixtures", "people");

    /// <summary>The movements of the five frames made of each portrait: (across, down, larger), as someone shifting in a chair.</summary>
    private static readonly (float X, float Y, float Scale)[] Moves =
        [(0, 0, 1.03f), (-14, 4, 1.05f), (12, -3, 1.04f), (22, 6, 1.08f), (-6, 10, 1.02f)];

    private sealed record Person(string File, string Source, int[][] Outline);

    private static List<Person> Load() =>
        JsonSerializer.Deserialize<List<Person>>(File.ReadAllText(Path.Combine(People, "people.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    private static SKMatrix Move((float X, float Y, float Scale) move) =>
        SKMatrix.CreateScaleTranslation(move.Scale, move.Scale, (Width / 2f * (1 - move.Scale)) + move.X, (Height / 2f * (1 - move.Scale)) + move.Y);

    private static SKBitmap Frame(SKBitmap picture, (float X, float Y, float Scale) move)
    {
        var frame = new SKBitmap(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(frame);
        canvas.Clear(SKColors.Black);
        using var image = SKImage.FromBitmap(picture);
        using var shader = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, new SKSamplingOptions(SKFilterMode.Linear), Move(move));
        using var paint = new SKPaint { Shader = shader };
        canvas.DrawRect(SKRect.Create(Width, Height), paint);
        return frame;
    }

    /// <summary>The traced outline, moved as the frame was, filled at <paramref name="size"/>².</summary>
    private static byte[] Truth(Person person, (float X, float Y, float Scale) move, int size)
    {
        using var builder = new SKPathBuilder();
        builder.MoveTo(person.Outline[0][0], person.Outline[0][1]);
        foreach (var point in person.Outline.Skip(1))
        {
            builder.LineTo(point[0], point[1]);
        }

        // A person reaches the bottom of the picture: carry the outline on below it, so a move up doesn't leave a gap.
        builder.Close();
        using var path = builder.Detach();
        using var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Gray8, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);
        canvas.Scale((float)size / Width, (float)size / Height);
        canvas.Concat(Move(move));
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = false };
        canvas.DrawPath(path, paint);
        canvas.DrawRect(new SKRect(person.Outline.Where(p => p[1] >= Height).Min(p => p[0]), Height - 1, person.Outline.Where(p => p[1] >= Height).Max(p => p[0]), Height + 200), paint);
        return bitmap.Bytes;
    }

    private static double Overlap(ReadOnlySpan<byte> mask, ReadOnlySpan<byte> truth)
    {
        int both = 0, either = 0;
        for (var i = 0; i < mask.Length; i++)
        {
            var (a, b) = (mask[i] >= 128, truth[i] >= 128);
            both += a && b ? 1 : 0;
            either += a || b ? 1 : 0;
        }

        return (double)both / Math.Max(1, either);
    }

    /// <summary>
    /// Phase 20 acceptance: the model's mask against hand-made ones on 20 frames, as intersection over union. The
    /// portraits and outlines are described in Fixtures\people\README.md; a real camera's clip is a by-hand check.
    /// </summary>
    [Fact]
    public void The_person_is_found_as_well_as_by_hand()
    {
        Assert.SkipWhen(ModelBudgetTests.ModelPath() is null, "tools\\get-inference-models.ps1 hasn't been run.");
        using var turn = new CashGear.Capture.Tests.MachineTurn();
        using var segmenter = new PersonSegmenter(ModelBudgetTests.ModelPath()!);
        var size = segmenter.MaskSize;
        var mask = new byte[size * size];
        var scores = new List<double>();
        foreach (var person in Load())
        {
            using var picture = SKBitmap.Decode(Path.Combine(People, person.File)).Copy(SKColorType.Bgra8888);
            var smoother = new MaskSmoother(mask.Length);
            foreach (var move in Moves)
            {
                using var frame = Frame(picture, move);
                segmenter.Segment(frame.GetPixelSpan(), Width, Height, frame.RowBytes, mask);
                smoother.Apply(mask);
                var score = Overlap(mask, Truth(person, move, size));
                scores.Add(score);
                output.WriteLine(FormattableString.Invariant($"{person.File} moved {move}: IoU {score:0.000}"));
            }
        }

        scores.Should().HaveCount(20);
        var mean = scores.Average();
        output.WriteLine(FormattableString.Invariant($"mean IoU {mean:0.000}, lowest {scores.Min():0.000}, on the graphics card: {segmenter.UsesGpu}"));
        scores.Min().Should().BeGreaterThanOrEqualTo(0.9, "on every frame, not only on average");
    }

    /// <summary>Draws the outlines and the model's masks over the portraits, to look at (<c>CASHGEAR_PEOPLE_TO</c>).</summary>
    [Fact(Explicit = true)]
    public void Write_the_outlines_over_the_people()
    {
        var folder = Environment.GetEnvironmentVariable("CASHGEAR_PEOPLE_TO") ?? Path.Combine(Path.GetTempPath(), "cashgear-people");
        Directory.CreateDirectory(folder);
        using var segmenter = ModelBudgetTests.ModelPath() is { } model ? new PersonSegmenter(model) : null;
        foreach (var person in Load())
        {
            using var picture = SKBitmap.Decode(Path.Combine(People, person.File)).Copy(SKColorType.Bgra8888);
            using var frame = Frame(picture, (0, 0, 1));
            using var canvas = new SKCanvas(frame);
            if (segmenter is not null)
            {
                var mask = new byte[segmenter.MaskSize * segmenter.MaskSize];
                using var source = Frame(picture, (0, 0, 1));
                segmenter.Segment(source.GetPixelSpan(), Width, Height, source.RowBytes, mask);
                using var paint = new SKPaint { Color = new SKColor(255, 0, 255, 90) };
                var (stepX, stepY) = ((float)Width / segmenter.MaskSize, (float)Height / segmenter.MaskSize);
                for (var y = 0; y < segmenter.MaskSize; y++)
                {
                    for (var x = 0; x < segmenter.MaskSize; x++)
                    {
                        if (mask[(y * segmenter.MaskSize) + x] < 128)
                        {
                            canvas.DrawRect(x * stepX, y * stepY, stepX + 1, stepY + 1, paint);
                        }
                    }
                }
            }

            using var line = new SKPaint { Color = SKColors.Lime, Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true };
            var points = person.Outline.Select(p => new SKPoint(p[0], p[1])).ToArray();
            for (var i = 0; i < points.Length; i++)
            {
                canvas.DrawLine(points[i], points[(i + 1) % points.Length], line);
            }

            using var data = frame.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(folder, Path.ChangeExtension(person.File, ".png")), data.ToArray());
        }
    }

    [Fact]
    public void Masks_follow_movement_at_once_and_settle_small_changes()
    {
        var smoother = new MaskSmoother(3);
        byte[] mask = [0, 255, 200];
        smoother.Apply(mask);
        mask.Should().Equal((byte)0, (byte)255, (byte)200);

        // A little flicker on the third pixel, a big change on the first: flicker moves slowly, the change at once.
        byte[] next = [255, 255, 208];
        smoother.Apply(next);
        next[0].Should().Be(255);
        next[1].Should().Be(255);
        next[2].Should().BeInRange((byte)201, (byte)204);

        // Noise round a level stays near it.
        var values = new List<int>();
        var noisy = new MaskSmoother(1);
        for (var i = 0; i < 200; i++)
        {
            byte[] one = [(byte)(128 + (i % 2 == 0 ? 10 : -10))];
            noisy.Apply(one);
            values.Add(one[0]);
        }

        (values.Skip(50).Max() - values.Skip(50).Min()).Should().BeLessThan(10, "20 levels of flicker in, under 10 out");

        // The same pictures give the same masks: it starts over when reset.
        noisy.Reset();
        byte[] again = [128];
        noisy.Apply(again);
        again[0].Should().Be(128);
    }

    private static HttpListener Serve(byte[] body, out Uri address)
    {
        var port = FreePort();
        address = new Uri($"http://127.0.0.1:{port}/");
        var listener = new HttpListener();
        listener.Prefixes.Add(address.ToString());
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                try
                {
                    var context = await listener.GetContextAsync();
                    await context.Response.OutputStream.WriteAsync(body);
                    context.Response.Close();
                }
                catch (HttpListenerException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        });
        return listener;
    }

    private static int FreePort()
    {
        var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return port;
    }

    [Fact]
    public async Task A_model_is_downloaded_once_and_checked()
    {
        var root = Directory.CreateTempSubdirectory("cashgear-inference-").FullName;
        try
        {
            var body = new byte[5000];
            new Random(2).NextBytes(body);
            var good = new InferenceModel("m", "Model", "m.onnx", body.Length, Convert.ToHexStringLower(SHA256.HashData(body)), new Uri("http://127.0.0.1/never"));
            using var listener = Serve(body, out var address);
            using var http = new HttpClient();
            var store = new InferenceModelStore(Path.Combine(root, "models"), http, NullLogger<InferenceModelStore>.Instance, address);
            store.Has(good).Should().BeFalse();
            var progress = new List<double>();
            var path = await store.EnsureAsync(good, new SyncProgress(progress), TestContext.Current.CancellationToken);
            File.ReadAllBytes(path).Should().Equal(body);
            store.Has(good).Should().BeTrue();
            progress[^1].Should().Be(1);

            listener.Stop();
            (await store.EnsureAsync(good, ct: TestContext.Current.CancellationToken)).Should().Be(path, "kept: nothing is asked of the server");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_damaged_download_is_refused_and_nothing_kept()
    {
        var root = Directory.CreateTempSubdirectory("cashgear-inference-").FullName;
        try
        {
            var body = new byte[5000];
            var wrong = new InferenceModel("m", "Model", "m.onnx", body.Length, new string('0', 64), new Uri("http://127.0.0.1/never"));
            using var listener = Serve(body, out var address);
            using var http = new HttpClient();
            var store = new InferenceModelStore(Path.Combine(root, "models"), http, NullLogger<InferenceModelStore>.Instance, address);
            await FluentActions.Invoking(() => store.EnsureAsync(wrong, ct: TestContext.Current.CancellationToken)).Should().ThrowAsync<InvalidDataException>().WithMessage("*damaged*");
            Directory.GetFiles(Path.Combine(root, "models")).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void The_person_finder_is_pinned_and_small()
    {
        var model = InferenceModels.PersonFinder;
        model.Source.AbsoluteUri.Should().Contain("/resolve/be49485c8e027524be38591817fc5cd31bd9d00e/", "a fixed commit, not main");
        model.Sha256.Should().HaveLength(64);
        var script = File.ReadAllText(Path.Combine(FindRepository(), "tools", "get-inference-models.ps1"));
        script.Should().Contain(model.Sha256).And.Contain(model.File).And.Contain(model.Source.AbsoluteUri);
    }

    private static string FindRepository()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "CashGear.Capture.sln")))
            {
                return folder.FullName;
            }
        }

        throw new DirectoryNotFoundException("The repository isn't above the tests.");
    }

    private sealed class SyncProgress(List<double> list) : IProgress<double>
    {
        public void Report(double value) => list.Add(value);
    }
}
