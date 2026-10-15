using CashGear.Capture.Editor.Document;
using CashGear.Capture.Editor.Imaging;
using CashGear.Capture.Editor.Model;
using SkiaSharp;

namespace CashGear.Capture.Editor.Tests;

/// <summary>Small images and pixel reads for the editor tests.</summary>
internal static class TestImages
{
    public static SKImage Solid(int width, int height, SKColor color) => SkiaImaging.Draw(width, height, c => c.Clear(color));

    /// <summary>One-pixel black and white columns: anything that blurs or averages turns them grey.</summary>
    public static SKImage Stripes(int width, int height) => SkiaImaging.Draw(width, height, c =>
    {
        c.Clear(SKColors.White);
        using var black = new SKPaint { Color = SKColors.Black };
        for (var x = 0; x < width; x += 2)
        {
            c.DrawRect(x, 0, 1, height, black);
        }
    });

    /// <summary>Deterministic opaque noise: every pixel differs from its neighbours, so any offset or resampling shows.</summary>
    public static SKImage Noise(int width, int height, int seed = 7)
    {
        var random = new Random(seed);
        var pixels = new byte[width * height * 4];
        random.NextBytes(pixels);
        for (var i = 3; i < pixels.Length; i += 4)
        {
            pixels[i] = 255;
        }

        return SKImage.FromPixelCopy(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul), pixels, width * 4);
    }

    /// <summary>Straight-alpha BGRA bytes of the image.</summary>
    public static unsafe byte[] Bytes(SKImage image)
    {
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var bytes = new byte[image.Width * image.Height * 4];
        fixed (byte* p = bytes)
        {
            image.ReadPixels(info, (nint)p, image.Width * 4, 0, 0).Should().BeTrue();
        }

        return bytes;
    }

    public static SKColor Pixel(SKImage image, int x, int y)
    {
        var bytes = Bytes(image);
        var i = ((y * image.Width) + x) * 4;
        return new SKColor(bytes[i + 2], bytes[i + 1], bytes[i], bytes[i + 3]);
    }

    public static EditorDocument Document(int width = 200, int height = 150) => new(Solid(width, height, SKColors.White));

    public static AnnotationStyle Red(float width = 4) => new() { Color = SKColors.Red, StrokeWidth = width };
}

internal static class ColorAssertions
{
    /// <summary>Each channel within <paramref name="tolerance"/> of the expected colour.</summary>
    public static void ShouldBeCloseTo(this SKColor actual, SKColor expected, int tolerance = 2)
    {
        var close = Math.Abs(actual.Red - expected.Red) <= tolerance
            && Math.Abs(actual.Green - expected.Green) <= tolerance
            && Math.Abs(actual.Blue - expected.Blue) <= tolerance
            && Math.Abs(actual.Alpha - expected.Alpha) <= tolerance;
        close.Should().BeTrue($"{actual} should be within {tolerance} of {expected}");
    }
}
