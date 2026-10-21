using System.IO;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Media;
using CashGear.Capture.Media.Tests;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Export;
using CashGear.Capture.VideoEditor.Model;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace CashGear.Capture.VideoEditor.Tests;

/// <summary>Screen-like pictures for a recording: a window with text over a gradient, typing, a moving pointer and a scroll.</summary>
internal static class SyntheticScreen
{
    private const string Words = "The quick brown fox jumps over the lazy dog while the recorder keeps every frame in step with its sound";

    /// <summary>Draws picture <paramref name="n"/> (30 fps, 1280 × 720) into <paramref name="bgra"/>.</summary>
    public static unsafe void Draw(int n, byte[] bgra)
    {
        const int width = 1280;
        const int height = 720;
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        fixed (byte* pixels = bgra)
        {
            using var surface = SKSurface.Create(info, (IntPtr)pixels, width * 4)!;
            var canvas = surface.Canvas;
            using (var desktop = new SKPaint())
            {
                desktop.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(width, height), [new SKColor(0x10, 0x5E, 0x7A), new SKColor(0x2B, 0x2D, 0x6E)], SKShaderTileMode.Clamp);
                canvas.DrawRect(info.Rect, desktop);
            }

            var window = SKRect.Create(80, 50, 1120, 620);
            using var fill = new SKPaint { IsAntialias = true };
            using var font = new SKFont(SKTypeface.Default, 15);
            fill.Color = SKColors.White;
            canvas.DrawRect(window, fill);
            fill.Color = new SKColor(0x2B, 0x57, 0x9A);
            canvas.DrawRect(SKRect.Create(window.Left, window.Top, window.Width, 34), fill);
            fill.Color = SKColors.White;
            canvas.DrawText("Notes — CashGear Capture test", window.Left + 12, window.Top + 22, SKTextAlign.Left, font, fill);
            fill.Color = new SKColor(0xF0, 0xF0, 0xF0);
            canvas.DrawRect(SKRect.Create(window.Left, window.Top + 34, window.Width, 40), fill);
            for (var b = 0; b < 8; b++)
            {
                fill.Color = SKColor.FromHsl(b * 40, 60, 50);
                canvas.DrawRoundRect(SKRect.Create(window.Left + 12 + (b * 44), window.Top + 42, 30, 24), 4, 4, fill);
            }

            fill.Color = new SKColor(0xF4, 0xF6, 0xF8);
            canvas.DrawRect(SKRect.Create(window.Left, window.Top + 74, 220, window.Height - 74), fill);
            for (var i = 0; i < 14; i++)
            {
                fill.Color = i == 3 ? new SKColor(0xCC, 0xE4, 0xF7) : fill.Color;
                if (i == 3)
                {
                    canvas.DrawRect(SKRect.Create(window.Left, window.Top + 84 + (i * 34) - 20, 220, 30), fill);
                }

                fill.Color = new SKColor(0x30, 0x30, 0x30);
                canvas.DrawText($"Document {i + 1}", window.Left + 16, window.Top + 84 + (i * 34), SKTextAlign.Left, font, fill);
                fill.Color = new SKColor(0xF4, 0xF6, 0xF8);
            }

            // The text scrolls up 200 px over the second from 6 s.
            var scroll = n < 180 ? 0 : Math.Min(200, (n - 180) * 200 / 30);
            canvas.Save();
            canvas.ClipRect(SKRect.Create(window.Left + 220, window.Top + 74, window.Width - 220, window.Height - 74));
            fill.Color = new SKColor(0x20, 0x20, 0x20);
            for (var line = 0; line < 30; line++)
            {
                var text = string.Join(' ', Words.Split(' ').Skip(line % 7).Take(12));
                canvas.DrawText($"{line + 1}. {text}", window.Left + 240, window.Top + 110 + (line * 24) - scroll, SKTextAlign.Left, font, fill);
            }

            // Typing from 2 s to 5 s, a letter every other frame.
            var typed = Words[..Math.Clamp((n - 60) / 2, 0, Words.Length)];
            fill.Color = new SKColor(0x10, 0x40, 0x90);
            canvas.DrawText("> " + typed + (n / 15 % 2 == 0 ? "|" : ""), window.Left + 240, window.Top + 110 + (31 * 24) - scroll - 400, SKTextAlign.Left, font, fill);
            canvas.Restore();

            // The pointer glides across, then circles.
            var t = n / 30.0;
            var (x, y) = t < 3 ? (300 + (200 * t), 300 + (30 * t)) : (900 + (80 * Math.Cos(t * 2)), 390 + (80 * Math.Sin(t * 2)));
            using var arrow = new SKPathBuilder();
            arrow.MoveTo((float)x, (float)y);
            arrow.LineTo((float)x, (float)y + 22);
            arrow.LineTo((float)x + 6, (float)y + 17);
            arrow.LineTo((float)x + 11, (float)y + 27);
            arrow.LineTo((float)x + 14, (float)y + 25);
            arrow.LineTo((float)x + 9, (float)y + 16);
            arrow.LineTo((float)x + 16, (float)y + 16);
            arrow.Close();
            using var path = arrow.Detach();
            fill.Color = SKColors.Black;
            canvas.DrawPath(path, fill);
            using var outline = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = SKColors.White };
            canvas.DrawPath(path, outline);
            canvas.Flush();
        }
    }
}

public sealed class GifExportTests : IDisposable
{
    private readonly TestMediaFiles _media = new("cashgear-gif-export-");

    public void Dispose() => _media.Dispose();

    private static ProjectExporter Exporter() => new(new SoftwareEncoders(), NullLogger<ProjectExporter>.Instance);

    /// <summary>A 10 s, 1280 × 720, 30 fps recording of <see cref="SyntheticScreen"/>.</summary>
    private string Screen()
    {
        var path = Path.Combine(_media.Root, "screen.mp4");
        if (!File.Exists(path))
        {
            new StampedRecording { Path = path, Seconds = 10, Format = new VideoFormat(1280, 720, 30), Picture = SyntheticScreen.Draw }.Write();
        }

        return path;
    }

    [Fact]
    public async Task A_gif_has_its_rate_size_loop_and_the_marked_range_only()
    {
        TestMedia.Require();
        var project = ProjectFactory.FromRecording(ProjectFactory.ReadAsset(_media.Stamped("short.mp4", 4)));
        var output = Path.Combine(_media.Root, "short.gif");
        var reports = new List<ExportProgress>();
        var result = await Exporter().ExportAsync(
            new ExportRequest(project, ExportPresets.Gif, QualityPreset.High, output, Gif: new GifOptions(Fps: 10, Width: 160, Loop: false)),
            new SyncProgress<ExportProgress>(reports.Add),
            TestContext.Current.CancellationToken);
        result.Frames.Should().Be(40);
        result.Gif!.Width.Should().Be(160);
        reports[^1].Fraction.Should().Be(1);
        File.Exists(output + ".partial").Should().BeFalse();
        GifProbe.Of(output).Should().Match<GifProbe>(p => p.Width == 160 && p.Height == 90 && p.Frames == 40 && !p.Loops);
        GifProbe.Of(output).Duration.Should().BeApproximately(4, 0.05);

        var ranged = Path.Combine(_media.Root, "range.gif");
        await Exporter().ExportAsync(
            new ExportRequest(project, ExportPresets.Gif, QualityPreset.High, ranged, Range: new TimeRange(1, 3), Gif: new GifOptions(Fps: 10, Width: 160)),
            ct: TestContext.Current.CancellationToken);
        GifProbe.Of(ranged).Should().Match<GifProbe>(p => p.Frames == 20 && p.Loops);
        new GifOptions(Width: 2000).Size(320, 180).Should().Be((320, 180), "never larger than the project");
    }

    [Fact]
    public async Task Acceptance_a_10_second_800_px_gif_of_a_screen_stays_under_5_mb_at_the_defaults()
    {
        TestMedia.Require();
        var project = ProjectFactory.FromRecording(ProjectFactory.ReadAsset(Screen()));
        var output = Path.Combine(_media.Root, "screen.gif");
        var result = await Exporter().ExportAsync(new ExportRequest(project, ExportPresets.Gif, QualityPreset.High, output), ct: TestContext.Current.CancellationToken);
        var probe = GifProbe.Of(output);
        TestContext.Current.TestOutputHelper?.WriteLine($"{result.Bytes / 1024.0 / 1024.0:0.00} MB, {probe.Width} x {probe.Height}, {probe.Frames} frames, {probe.Duration:0.00} s, made in {result.Elapsed.TotalSeconds:0.0} s");
        result.Gif.Should().Be(new GifOptions(), "the defaults, not made smaller");
        result.Bytes.Should().BeLessThan(5 * 1024 * 1024);
        probe.Should().Match<GifProbe>(p => p.Width == 800 && p.Height == 450 && p.Frames == 150 && p.Loops);
        probe.Duration.Should().BeApproximately(10, 0.1);

        // A smaller size asked for: made again with fewer colours, frames or pixels until it fits.
        var limit = result.Bytes * 6 / 10;
        var smaller = await Exporter().ExportAsync(
            new ExportRequest(project, ExportPresets.Gif, QualityPreset.High, Path.Combine(_media.Root, "smaller.gif"), Gif: new GifOptions(MaxBytes: limit)),
            ct: TestContext.Current.CancellationToken);
        TestContext.Current.TestOutputHelper?.WriteLine($"made smaller: {smaller.Bytes / 1024.0 / 1024.0:0.00} MB with {smaller.Gif}");
        smaller.Bytes.Should().BeLessThanOrEqualTo(limit);
        smaller.Gif.Should().NotBe(new GifOptions(MaxBytes: limit));
    }
}
