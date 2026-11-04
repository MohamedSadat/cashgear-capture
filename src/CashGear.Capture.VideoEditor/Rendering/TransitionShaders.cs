using System.ComponentModel;
using CashGear.Capture.VideoEditor.Model;
using SkiaSharp;

namespace CashGear.Capture.VideoEditor.Rendering;

/// <summary>
/// The transitions and clip effects as SkSL, so the same code runs on the graphics card in the preview and on the processor
/// in the export, and the two draw the same picture. Every transition reads two layers in the pixels of what is drawn on:
/// <c>a</c> going, <c>b</c> coming (a transparent one at an edge with nothing to join), <c>t</c> from 0 to 1.
/// </summary>
[Localizable(false)]
internal static class TransitionShaders
{
    private const string Header = """
        uniform shader a;
        uniform shader b;
        uniform float t;
        uniform float2 origin;
        uniform float2 size;
        uniform float2 dir;
        uniform half4 color;

        half4 over(half4 top, half4 under) { return top + under * (1.0 - top.a); }
        """;

    private const string DissolveCode = Header + """

        half4 main(float2 p) { return mix(a.eval(p), b.eval(p), half(t)); }
        """;

    private const string DipCode = Header + """

        half4 main(float2 p) {
            return t < 0.5 ? mix(a.eval(p), color, half(t * 2.0)) : mix(color, b.eval(p), half((t - 0.5) * 2.0));
        }
        """;

    private const string WipeCode = Header + """

        half4 main(float2 p) {
            float2 u = (p - origin) / size;
            float s = dir.x != 0.0 ? (dir.x > 0.0 ? u.x : 1.0 - u.x) : (dir.y > 0.0 ? u.y : 1.0 - u.y);
            float soft = 0.006;
            float edge = t * (1.0 + 2.0 * soft) - soft;
            return mix(a.eval(p), b.eval(p), half(1.0 - smoothstep(edge - soft, edge + soft, s)));
        }
        """;

    private const string SlideCode = Header + """

        half4 main(float2 p) {
            return over(b.eval(p + dir * size * (1.0 - t)), a.eval(p));
        }
        """;

    private const string PushCode = Header + """

        half4 main(float2 p) {
            return over(b.eval(p + dir * size * (1.0 - t)), a.eval(p - dir * size * t));
        }
        """;

    private const string ZoomCode = Header + """

        half4 main(float2 p) {
            float2 middle = origin + size * 0.5;
            float2 q = p - middle;
            return mix(a.eval(middle + q / (1.0 + t)), b.eval(middle + q / (0.85 + 0.15 * t)), half(t));
        }
        """;

    private const string BlurCode = Header + """

        half4 main(float2 p) {
            float reach = size.y * 0.05 * sin(3.14159265 * t);
            float mixed = smoothstep(0.3, 0.7, t);
            half4 sum = half4(0);
            for (int i = 0; i < 16; i++) {
                float r = reach * sqrt((float(i) + 0.5) / 16.0);
                float angle = float(i) * 2.39996323;
                float2 at = p + float2(cos(angle), sin(angle)) * r;
                sum += mix(a.eval(at), b.eval(at), half(mixed));
            }
            return sum / 16.0;
        }
        """;

    private const string CircleCode = Header + """

        half4 main(float2 p) {
            float reach = length(size) * 0.5;
            float soft = size.y * 0.004;
            float radius = t * (reach + 2.0 * soft) - soft;
            float m = 1.0 - smoothstep(radius - soft, radius + soft, length(p - (origin + size * 0.5)));
            return mix(a.eval(p), b.eval(p), half(m));
        }
        """;

    /// <summary>A key's colour out, then the colour's changes, in the picture's own colours (not premultiplied).</summary>
    private const string EffectCode = """
        uniform shader picture;
        uniform float keyOn;
        uniform float3 key;
        uniform float2 edge;
        uniform float spill;
        uniform float brightness;
        uniform float contrast;
        uniform float saturation;
        uniform float temperature;

        float luma(float3 c) { return dot(c, float3(0.2126, 0.7152, 0.0722)); }

        half4 main(float2 p) {
            half4 c = picture.eval(p);
            if (c.a <= 0.0) { return half4(0); }
            float3 rgb = float3(c.rgb) / float(c.a);
            float alpha = float(c.a);
            if (keyOn > 0.5) {
                // How far from the key by the angle of its chroma (the difference from the light level), so a shadow on
                // the screen goes too; greys and faint tints have too little chroma to be the key at all.
                float3 keyChroma = key - luma(key);
                float3 chroma = rgb - luma(rgb);
                float3 way = keyChroma / max(length(keyChroma), 0.001);
                float along = dot(chroma, way);
                float across = length(chroma - way * along);
                float far = max(across / max(along, 0.001), 1.0 - smoothstep(0.1, 0.3, along));
                alpha *= smoothstep(edge.x, edge.y, far);

                // Spill: what is left of the key's colour in what stays comes down to what the others allow.
                if (key.g >= key.r && key.g >= key.b) {
                    float most = max(rgb.r, rgb.b);
                    rgb.g = mix(rgb.g, min(rgb.g, most), spill);
                } else if (key.b >= key.r) {
                    float most = max(rgb.r, rgb.g);
                    rgb.b = mix(rgb.b, min(rgb.b, most), spill);
                } else {
                    float most = max(rgb.g, rgb.b);
                    rgb.r = mix(rgb.r, min(rgb.r, most), spill);
                }
            }
            rgb += brightness;
            rgb = (rgb - 0.5) * contrast + 0.5;
            rgb = mix(float3(luma(rgb)), rgb, saturation);
            rgb *= float3(1.0 + temperature * 0.25, 1.0, 1.0 - temperature * 0.25);
            rgb = clamp(rgb, 0.0, 1.0);
            return half4(half3(rgb * alpha), half(alpha));
        }
        """;

    /// <summary>A .cube table laid out as slices in a row, read between its cells on all three axes.</summary>
    private const string LutCode = """
        uniform shader picture;
        uniform shader table;
        uniform float cells;
        uniform float strength;

        half4 main(float2 p) {
            half4 c = picture.eval(p);
            if (c.a <= 0.0) { return half4(0); }
            float3 rgb = clamp(float3(c.rgb) / float(c.a), 0.0, 1.0);
            float slice = rgb.b * (cells - 1.0);
            float low = floor(slice);
            float high = min(low + 1.0, cells - 1.0);
            float2 at = float2(rgb.r * (cells - 1.0) + 0.5, rgb.g * (cells - 1.0) + 0.5);
            float3 first = float3(table.eval(at + float2(low * cells, 0.0)).rgb);
            float3 second = float3(table.eval(at + float2(high * cells, 0.0)).rgb);
            float3 looked = mix(first, second, slice - low);
            return half4(half3(mix(rgb, looked, strength) * float(c.a)), c.a);
        }
        """;

    /// <summary>A shadow from a picture's own see-through shape: its level round about, offset.</summary>
    private const string ShapeShadowCode = """
        uniform shader picture;
        uniform float2 offset;
        uniform float reach;
        uniform half4 color;

        half4 main(float2 p) {
            float2 q = p - offset;
            float sum = 0.0;
            for (int i = 0; i < 32; i++) {
                float r = reach * sqrt((float(i) + 0.5) / 32.0);
                float angle = float(i) * 2.39996323;
                sum += float(picture.eval(q + float2(cos(angle), sin(angle)) * r).a);
            }
            return color * half(sum / 32.0);
        }
        """;

    private static readonly Lazy<SKRuntimeEffect> Dissolves = new(() => Compile(DissolveCode));
    private static readonly Lazy<SKRuntimeEffect> Dips = new(() => Compile(DipCode));
    private static readonly Lazy<SKRuntimeEffect> Wipes = new(() => Compile(WipeCode));
    private static readonly Lazy<SKRuntimeEffect> Slides = new(() => Compile(SlideCode));
    private static readonly Lazy<SKRuntimeEffect> Pushes = new(() => Compile(PushCode));
    private static readonly Lazy<SKRuntimeEffect> Zooms = new(() => Compile(ZoomCode));
    private static readonly Lazy<SKRuntimeEffect> Blurs = new(() => Compile(BlurCode));
    private static readonly Lazy<SKRuntimeEffect> Circles = new(() => Compile(CircleCode));
    private static readonly Lazy<SKRuntimeEffect> Effects = new(() => Compile(EffectCode));
    private static readonly Lazy<SKRuntimeEffect> Luts = new(() => Compile(LutCode));
    private static readonly Lazy<SKRuntimeEffect> ShapeShadows = new(() => Compile(ShapeShadowCode));

    /// <summary>A transition's shader over layers in the pixels of <paramref name="bounds"/> (<paramref name="progress"/> is eased already).</summary>
    public static SKShader Transition(ClipTransition transition, SKShader going, SKShader coming, float progress, SKRectI bounds)
    {
        var effect = (transition.Kind switch
        {
            TransitionKind.DipToColor => Dips,
            TransitionKind.Wipe => Wipes,
            TransitionKind.Slide => Slides,
            TransitionKind.Push => Pushes,
            TransitionKind.Zoom => Zooms,
            TransitionKind.Blur => Blurs,
            TransitionKind.Circle => Circles,
            _ => Dissolves,
        }).Value;
        var (dx, dy) = transition.Direction switch
        {
            TransitionDirection.Right => (1f, 0f),
            TransitionDirection.Up => (0f, -1f),
            TransitionDirection.Down => (0f, 1f),
            _ => (-1f, 0f),
        };
        var color = FrameCompositor.Color(transition.Color ?? ClipTransition.DefaultColor, SKColors.Black);
        var alpha = color.Alpha / 255f;
        using var uniforms = new SKRuntimeEffectUniforms(effect)
        {
            ["t"] = progress,
            ["origin"] = new float[] { bounds.Left, bounds.Top },
            ["size"] = new float[] { bounds.Width, bounds.Height },
            ["dir"] = new[] { dx, dy },
            ["color"] = new[] { color.Red / 255f * alpha, color.Green / 255f * alpha, color.Blue / 255f * alpha, alpha },
        };
        using var children = new SKRuntimeEffectChildren(effect) { ["a"] = going, ["b"] = coming };
        return effect.ToShader(uniforms, children);
    }

    /// <summary>A picture with a key and a colour change, or the same shader with neither.</summary>
    public static SKShader Effect(SKShader picture, ClipEffects effects)
    {
        var effect = Effects.Value;
        var key = effects.Key;
        var keyColor = FrameCompositor.Color(key?.Color, SKColors.Lime);
        var low = (float)Math.Clamp(key?.Tolerance ?? 0, 0, 1);
        using var uniforms = new SKRuntimeEffectUniforms(effect)
        {
            ["keyOn"] = key is null ? 0f : 1f,
            ["key"] = new[] { keyColor.Red / 255f, keyColor.Green / 255f, keyColor.Blue / 255f },
            ["edge"] = new[] { low, low + Math.Max(0.001f, (float)Math.Clamp(key?.Softness ?? 0, 0, 1)) },
            ["spill"] = (float)Math.Clamp(key?.Spill ?? 0, 0, 1),
            ["brightness"] = (float)effects.Brightness * 0.5f,
            ["contrast"] = effects.Contrast >= 0 ? 1f + ((float)effects.Contrast * 2f) : 1f + (float)effects.Contrast,
            ["saturation"] = 1f + (float)effects.Saturation,
            ["temperature"] = (float)effects.Temperature,
        };
        using var children = new SKRuntimeEffectChildren(effect) { ["picture"] = picture };
        return effect.ToShader(uniforms, children);
    }

    /// <summary>A picture through a look-up table made by <see cref="CubeLut"/>.</summary>
    public static SKShader Lut(SKShader picture, SKImage table, int cells, float strength)
    {
        var effect = Luts.Value;
        using var tableShader = table.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        using var uniforms = new SKRuntimeEffectUniforms(effect) { ["cells"] = (float)cells, ["strength"] = Math.Clamp(strength, 0, 1) };
        using var children = new SKRuntimeEffectChildren(effect) { ["picture"] = picture, ["table"] = tableShader };
        return effect.ToShader(uniforms, children);
    }

    /// <summary>A soft shadow of a picture's see-through shape (premultiplied, drawn under it).</summary>
    public static SKShader ShapeShadow(SKShader picture, float offsetX, float offsetY, float reach, SKColor color)
    {
        var effect = ShapeShadows.Value;
        var alpha = color.Alpha / 255f;
        using var uniforms = new SKRuntimeEffectUniforms(effect)
        {
            ["offset"] = new[] { offsetX, offsetY },
            ["reach"] = Math.Max(0.5f, reach),
            ["color"] = new[] { color.Red / 255f * alpha, color.Green / 255f * alpha, color.Blue / 255f * alpha, alpha },
        };
        using var children = new SKRuntimeEffectChildren(effect) { ["picture"] = picture };
        return effect.ToShader(uniforms, children);
    }

    private static SKRuntimeEffect Compile(string code) =>
        SKRuntimeEffect.CreateShader(code, out var errors) ?? throw new InvalidOperationException($"A transition's shader doesn't compile: {errors}");
}
