using System.ComponentModel;
using SkiaSharp;

namespace CashGear.Capture.VideoEditor.Rendering;

/// <summary>
/// The looks' per-pixel work as SkSL, so the same code runs on the graphics card in the preview and on the processor
/// in the export, and the two draw the same picture. Each effect is compiled once.
/// </summary>
[Localizable(false)]
internal static class StudioShaders
{
    /// <summary>The sample counts motion blur is compiled for (SkSL's loops need a constant).</summary>
    private static readonly int[] MotionCounts = [2, 3, 4, 6, 8, 12, 16, 24, 32];

    /// <summary>
    /// A Gaussian-blurred rounded rectangle, worked out rather than blurred (Evan Wallace's fast rounded-rectangle
    /// shadows): a closed form across each row, four rows' worth down it.
    /// </summary>
    private const string ShadowCode = """
        uniform float4 box;
        uniform float radius;
        uniform float sigma;
        uniform half4 color;

        float gaussian(float x, float s) {
            return exp(-(x * x) / (2.0 * s * s)) / (2.5066283 * s);
        }

        float2 erf2(float2 x) {
            float2 s = sign(x);
            float2 a = abs(x);
            float2 v = 1.0 + (0.278393 + (0.230389 + 0.078108 * (a * a)) * a) * a;
            v *= v;
            return s - s / (v * v);
        }

        float acrossRow(float x, float y, float s, float r, float2 half_size) {
            float delta = min(half_size.y - r - abs(y), 0.0);
            float curved = half_size.x - r + sqrt(max(0.0, r * r - delta * delta));
            float2 integral = 0.5 + 0.5 * erf2((x + float2(-curved, curved)) * (0.70710678 / s));
            return integral.y - integral.x;
        }

        half4 main(float2 p) {
            float2 center = (box.xy + box.zw) * 0.5;
            float2 half_size = (box.zw - box.xy) * 0.5;
            float2 q = p - center;
            float low = q.y - half_size.y;
            float high = q.y + half_size.y;
            float start = clamp(-3.0 * sigma, low, high);
            float end = clamp(3.0 * sigma, low, high);
            float step = (end - start) / 4.0;
            float y = start + step * 0.5;
            float value = 0.0;
            for (int i = 0; i < 4; i++) {
                value += acrossRow(q.x, q.y - y, sigma, radius, half_size) * gaussian(y, sigma) * step;
                y += step;
            }
            return color * half(clamp(value, 0.0, 1.0));
        }
        """;

    /// <summary>The picture and what replaces its background, mixed by the mask (shared by every sample count).</summary>
    private const string MatteCode = """
        uniform shader picture;
        uniform shader mask;
        uniform shader back;
        uniform float2 edge;

        half4 main(float2 p) {
            float m = float(mask.eval(p).r);
            half a = half(smoothstep(edge.x, edge.y, m));
            return picture.eval(p) * a + back.eval(p) * (1.0 - a);
        }
        """;

    private static readonly Lazy<SKRuntimeEffect> Shadows = new(() => Compile(ShadowCode));

    private static readonly Lazy<SKRuntimeEffect> Mattes = new(() => Compile(MatteCode));

    private static readonly Lazy<SKRuntimeEffect>[] MotionEffects = [.. MotionCounts.Select(count => new Lazy<SKRuntimeEffect>(() => Compile(MotionCode(count))))];

    /// <summary>
    /// A soft shadow of <paramref name="box"/>: <paramref name="sigma"/> is the blur in canvas pixels,
    /// <paramref name="color"/> carries its strength. Draw it in canvas coordinates.
    /// </summary>
    public static SKShader Shadow(SKRect box, float radius, float sigma, SKColor color)
    {
        var effect = Shadows.Value;
        var alpha = color.Alpha / 255f;
        using var uniforms = new SKRuntimeEffectUniforms(effect)
        {
            ["box"] = new[] { box.Left, box.Top, box.Right, box.Bottom },
            ["radius"] = Math.Max(0, Math.Min(radius, Math.Min(box.Width, box.Height) / 2)),
            ["sigma"] = Math.Max(0.5f, sigma),
            ["color"] = new[] { color.Red / 255f * alpha, color.Green / 255f * alpha, color.Blue / 255f * alpha, alpha },
        };
        return effect.ToShader(uniforms);
    }

    /// <summary>The nearest sample count there is for at most <paramref name="wanted"/> samples.</summary>
    public static int MotionSamples(int wanted)
    {
        var chosen = MotionCounts[0];
        foreach (var count in MotionCounts)
        {
            if (count <= wanted)
            {
                chosen = count;
            }
        }

        return chosen;
    }

    /// <summary>
    /// <paramref name="layer"/> averaged along the camera's movement: each pixel reads the layer
    /// <paramref name="samples"/> times between where the shutter opens (<paramref name="from"/>: scale x, y, shift x,
    /// y) and where it closes (<paramref name="to"/>). See-through parts stay see-through in proportion.
    /// </summary>
    public static SKShader MotionBlur(SKShader layer, ReadOnlySpan<float> from, ReadOnlySpan<float> to, int samples)
    {
        var effect = MotionEffects[Array.IndexOf(MotionCounts, MotionSamples(samples))].Value;
        using var uniforms = new SKRuntimeEffectUniforms(effect)
        {
            ["from"] = from.ToArray(),
            ["to"] = to.ToArray(),
        };
        using var children = new SKRuntimeEffectChildren(effect) { ["layer"] = layer };
        return effect.ToShader(uniforms, children);
    }

    /// <summary>The picture over what replaces its background, by the mask; all in the same coordinates.</summary>
    public static SKShader Matte(SKShader picture, SKShader mask, SKShader back, float low = 0.35f, float high = 0.65f)
    {
        var effect = Mattes.Value;
        using var uniforms = new SKRuntimeEffectUniforms(effect) { ["edge"] = new[] { low, high } };
        using var children = new SKRuntimeEffectChildren(effect)
        {
            ["picture"] = picture,
            ["mask"] = mask,
            ["back"] = back,
        };
        return effect.ToShader(uniforms, children);
    }

    private static string MotionCode(int count) => $$"""
        uniform shader layer;
        uniform float4 from;
        uniform float4 to;

        half4 main(float2 p) {
            float2 a = from.xy * p + from.zw;
            float2 b = to.xy * p + to.zw;
            half4 sum = half4(0);
            for (int i = 0; i < {{count}}; i++) {
                sum += layer.eval(mix(a, b, (float(i) + 0.5) / {{count}}.0));
            }
            return sum / {{count}}.0;
        }
        """;

    private static SKRuntimeEffect Compile(string code) =>
        SKRuntimeEffect.CreateShader(code, out var errors) ?? throw new InvalidOperationException($"A look's shader doesn't compile: {errors}");
}
