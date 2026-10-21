using CashGear.Capture.Editor.Imaging;
using CashGear.Capture.Editor.Resources;
using SkiaSharp;

namespace CashGear.Capture.Editor.Effects;

/// <summary>
/// Brightness and contrast, each −100 to 100 (0 changes nothing). Contrast stretches the colours away from mid grey
/// (−100 makes everything grey, 100 four times as far); brightness then adds up to half the range.
/// </summary>
public sealed record AdjustEffect(int Brightness = 0, int Contrast = 0) : ImageEffect
{
    public override string Name => EditorStrings.EffectBrightnessContrast;

    public override EffectOutput Apply(SKImage source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var factor = MathF.Pow((100 + Math.Clamp(Contrast, -100, 100)) / 100f, 2);
        var shift = (0.5f * (1 - factor)) + (Math.Clamp(Brightness, -100, 100) / 200f);
        float[] matrix =
        [
            factor, 0, 0, 0, shift,
            0, factor, 0, 0, shift,
            0, 0, factor, 0, shift,
            0, 0, 0, 1, 0,
        ];
        return new EffectOutput(ColorFilters.Apply(source, matrix), default);
    }

    public override ImageEffect Normalize() => this with
    {
        Brightness = Math.Clamp(Brightness, -100, 100),
        Contrast = Math.Clamp(Contrast, -100, 100),
    };
}

/// <summary>Shades of grey by the colours' luminance (Rec. 709 weights); transparency is kept.</summary>
public sealed record GrayscaleEffect : ImageEffect
{
    public override string Name => EditorStrings.EffectGrayscale;

    public override EffectOutput Apply(SKImage source)
    {
        ArgumentNullException.ThrowIfNull(source);
        const float R = 0.2126f, G = 0.7152f, B = 0.0722f;
        float[] matrix =
        [
            R, G, B, 0, 0,
            R, G, B, 0, 0,
            R, G, B, 0, 0,
            0, 0, 0, 1, 0,
        ];
        return new EffectOutput(ColorFilters.Apply(source, matrix), default);
    }

    public override ImageEffect Normalize() => this;
}

/// <summary>
/// Crisper edges: each colour is pushed away from the average of its four neighbours by
/// <see cref="Amount"/> percent (0 to 200). Flat areas don't change; transparency is kept.
/// </summary>
public sealed record SharpenEffect(int Amount = 50) : ImageEffect
{
    public const int MaxAmount = 200;

    public override string Name => EditorStrings.EffectSharpen;

    public override EffectOutput Apply(SKImage source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var k = Math.Clamp(Amount, 0, MaxAmount) / 100f;
        var width = source.Width;
        var height = source.Height;
        var stride = width * 4;
        var src = SkiaImaging.ReadUnpremul(source);
        var dst = new byte[src.Length];
        for (var y = 0; y < height; y++)
        {
            var row = y * stride;
            var up = Math.Max(0, y - 1) * stride;
            var down = Math.Min(height - 1, y + 1) * stride;
            for (var x = 0; x < width; x++)
            {
                var i = row + (x * 4);
                var left = row + (Math.Max(0, x - 1) * 4);
                var right = row + (Math.Min(width - 1, x + 1) * 4);
                var upI = up + (x * 4);
                var downI = down + (x * 4);
                for (var c = 0; c < 3; c++)
                {
                    var around = src[left + c] + src[right + c] + src[upI + c] + src[downI + c];
                    var value = ((1 + (4 * k)) * src[i + c]) - (k * around);
                    dst[i + c] = (byte)Math.Clamp((int)MathF.Round(value), 0, 255);
                }

                dst[i + 3] = src[i + 3];
            }
        }

        return new EffectOutput(SkiaImaging.FromUnpremul(dst, width, height), default);
    }

    public override ImageEffect Normalize() => this with { Amount = Math.Clamp(Amount, 0, MaxAmount) };
}

internal static class ColorFilters
{
    /// <summary>The image through a 4 × 5 colour matrix (on straight colours; the last column in 0–1 units).</summary>
    public static SKImage Apply(SKImage source, float[] matrix) => SkiaImaging.Draw(source.Width, source.Height, c =>
    {
        using var filter = SKColorFilter.CreateColorMatrix(matrix);
        using var paint = new SKPaint { ColorFilter = filter };
        c.DrawImage(source, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), paint);
    });
}
