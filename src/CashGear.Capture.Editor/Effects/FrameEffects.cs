using CashGear.Capture.Editor.Imaging;
using CashGear.Capture.Editor.Resources;
using SkiaSharp;

namespace CashGear.Capture.Editor.Effects;

public enum PerspectiveAxis
{
    /// <summary>Turned about a vertical line: one side comes closer, the other goes away.</summary>
    Horizontal,

    /// <summary>Turned about a horizontal line: the top or the bottom goes away.</summary>
    Vertical,
}

/// <summary>
/// A soft shadow of the image's shape (it follows torn edges and rounded corners) on a transparent margin, which
/// grows the canvas. Opacity is in percent; the colour is black unless set.
/// </summary>
public sealed record ShadowEffect(int Blur = 10, int OffsetX = 0, int OffsetY = 4, int Opacity = 45, SKColor? Color = null) : ImageEffect
{
    public const int MaxBlur = 60;
    public const int MaxOffset = 100;

    public override string Name => EditorStrings.EffectDropShadow;

    public override bool KeepsAnnotations => true;

    public SKColor ShadowColor => (Color ?? SKColors.Black).WithAlpha((byte)Math.Round(Math.Clamp(Opacity, 0, 100) * 2.55));

    /// <summary>The transparent margin on every side: room for the blur and the offset.</summary>
    public int Margin => (int)Math.Ceiling(3.0 * Blur) + Math.Max(Math.Abs(OffsetX), Math.Abs(OffsetY));

    public override EffectOutput Apply(SKImage source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var margin = Margin;
        var image = SkiaImaging.Draw(source.Width + (2 * margin), source.Height + (2 * margin), c =>
        {
            using var shadow = SKImageFilter.CreateDropShadowOnly(OffsetX, OffsetY, Blur, Blur, ShadowColor);
            using (var paint = new SKPaint { ImageFilter = shadow })
            {
                c.DrawImage(source, margin, margin, Exact, paint);
            }

            c.DrawImage(source, margin, margin, Exact, null);
        });
        return new EffectOutput(image, new SKPointI(margin, margin));
    }

    public override ImageEffect Normalize() => this with
    {
        Blur = Math.Clamp(Blur, 0, MaxBlur),
        OffsetX = Math.Clamp(OffsetX, -MaxOffset, MaxOffset),
        OffsetY = Math.Clamp(OffsetY, -MaxOffset, MaxOffset),
        Opacity = Math.Clamp(Opacity, 0, 100),
    };
}

/// <summary>
/// A solid frame <see cref="Thickness"/> pixels wide around the image's shape, on a canvas that grows by as much.
/// Around a plain rectangular capture it is a rectangle; around rounded corners or a torn edge it follows them.
/// </summary>
public sealed record BorderEffect(int Thickness = 4, SKColor? Color = null) : ImageEffect
{
    public const int MaxThickness = 200;

    public override string Name => EditorStrings.EffectBorder;

    public override bool KeepsAnnotations => true;

    public SKColor BorderColor => Color ?? SKColors.Black;

    public override EffectOutput Apply(SKImage source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var t = Thickness;
        var image = SkiaImaging.Draw(source.Width + (2 * t), source.Height + (2 * t), c =>
        {
            // The image's alpha in the border colour, grown by the thickness, then the image over it.
            using var tint = SKColorFilter.CreateBlendMode(BorderColor, SKBlendMode.SrcIn);
            using var colored = SKImageFilter.CreateColorFilter(tint);
            using var grown = SKImageFilter.CreateDilate(t, t, colored);
            using (var paint = new SKPaint { ImageFilter = grown })
            {
                c.DrawImage(source, t, t, Exact, paint);
            }

            c.DrawImage(source, t, t, Exact, null);
        });
        return new EffectOutput(image, new SKPointI(t, t));
    }

    public override ImageEffect Normalize() => this with { Thickness = Math.Clamp(Thickness, 1, MaxThickness) };
}

/// <summary>Corners cut round (transparent outside, smooth edge). The radius is at most half the shorter side.</summary>
public sealed record RoundedCornersEffect(int Radius = 16) : ImageEffect
{
    public const int MaxRadius = 500;

    public override string Name => EditorStrings.EffectRoundedCorners;

    public override EffectOutput Apply(SKImage source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var radius = Math.Min(Radius, Math.Min(source.Width, source.Height) / 2f);
        var image = SkiaImaging.Draw(source.Width, source.Height, c =>
        {
            using var shape = new SKRoundRect(SKRect.Create(source.Width, source.Height), radius, radius);
            c.ClipRoundRect(shape, SKClipOperation.Intersect, antialias: true);
            c.DrawImage(source, 0, 0, Exact, null);
        });
        return new EffectOutput(image, default);
    }

    public override ImageEffect Normalize() => this with { Radius = Math.Clamp(Radius, 1, MaxRadius) };
}

/// <summary>
/// Edges torn like paper: a jagged line up to <see cref="Depth"/> pixels in, with teeth about
/// <see cref="ToothWidth"/> pixels apart. The same <see cref="Seed"/> always tears the same way; each side has its
/// own pattern, so switching one side on or off leaves the others as they were.
/// </summary>
public sealed record TornEdgeEffect(Edges Edges = Edges.Bottom, int Depth = 12, int ToothWidth = 20, int Seed = 1) : ImageEffect
{
    public const int MaxDepth = 100;
    public const int MinToothWidth = 4;
    public const int MaxToothWidth = 200;

    public override string Name => EditorStrings.EffectTornEdge;

    public override EffectOutput Apply(SKImage source)
    {
        ArgumentNullException.ThrowIfNull(source);
        float w = source.Width;
        float h = source.Height;
        var depth = Math.Min(Depth, Math.Min(source.Width, source.Height) / 3f);
        var points = new List<SKPoint>();

        // Clockwise from the top-left corner; each side's inset is measured inwards from that side.
        foreach (var (x, inset) in Profile(Edges.Top, w, depth))
        {
            points.Add(new SKPoint(x, inset));
        }

        foreach (var (y, inset) in Profile(Edges.Right, h, depth))
        {
            points.Add(new SKPoint(w - inset, y));
        }

        foreach (var (x, inset) in Profile(Edges.Bottom, w, depth))
        {
            points.Add(new SKPoint(w - x, h - inset));
        }

        foreach (var (y, inset) in Profile(Edges.Left, h, depth))
        {
            points.Add(new SKPoint(inset, h - y));
        }

        using var builder = new SKPathBuilder();
        builder.MoveTo(points[0]);
        foreach (var point in points.Skip(1))
        {
            builder.LineTo(point);
        }

        builder.Close();
        using var path = builder.Detach();
        var image = SkiaImaging.Draw(source.Width, source.Height, c =>
        {
            c.ClipPath(path, SKClipOperation.Intersect, antialias: true);
            c.DrawImage(source, 0, 0, Exact, null);
        });
        return new EffectOutput(image, default);
    }

    /// <summary>Points along one side (position along it, inset): two straight ends if it isn't torn.</summary>
    private IEnumerable<(float Along, float Inset)> Profile(Edges side, float length, float depth)
    {
        if (!Edges.HasFlag(side))
        {
            yield return (0, 0);
            yield return (length, 0);
            yield break;
        }

        var random = new Random(unchecked((Seed * 31) + (int)side));
        var teeth = Math.Max(2, (int)Math.Round(length / ToothWidth));
        var step = length / teeth;
        for (var i = 0; i <= teeth; i++)
        {
            var along = i == 0 || i == teeth ? i * step : (i + (((float)random.NextDouble() - 0.5f) * 0.6f)) * step;

            // Peaks near the edge and valleys deeper in, each a little random: paper doesn't tear evenly.
            var inset = i % 2 == 0
                ? (float)random.NextDouble() * depth * 0.35f
                : depth * (0.6f + ((float)random.NextDouble() * 0.4f));
            yield return (along, inset);
        }
    }

    public override ImageEffect Normalize() => this with
    {
        Depth = Math.Clamp(Depth, 1, MaxDepth),
        ToothWidth = Math.Clamp(ToothWidth, MinToothWidth, MaxToothWidth),
        Edges = Edges & Edges.All,
    };
}

/// <summary>The chosen sides fade to transparent over <see cref="Width"/> pixels.</summary>
public sealed record FadeEdgeEffect(Edges Edges = Edges.All, int Width = 40) : ImageEffect
{
    public const int MaxWidth = 500;

    public override string Name => EditorStrings.EffectFadedEdges;

    public override EffectOutput Apply(SKImage source)
    {
        ArgumentNullException.ThrowIfNull(source);
        float w = source.Width;
        float h = source.Height;
        var image = SkiaImaging.Draw(source.Width, source.Height, c =>
        {
            c.DrawImage(source, 0, 0, Exact, null);

            // Each side multiplies the alpha by a ramp from 0 at the edge to 1 at the width; corners get both.
            if (Edges.HasFlag(Edges.Top))
            {
                Fade(c, SKRect.Create(w, Math.Min(Width, h)), new SKPoint(0, 0), new SKPoint(0, Width));
            }

            if (Edges.HasFlag(Edges.Bottom))
            {
                Fade(c, new SKRect(0, Math.Max(0, h - Width), w, h), new SKPoint(0, h), new SKPoint(0, h - Width));
            }

            if (Edges.HasFlag(Edges.Left))
            {
                Fade(c, SKRect.Create(Math.Min(Width, w), h), new SKPoint(0, 0), new SKPoint(Width, 0));
            }

            if (Edges.HasFlag(Edges.Right))
            {
                Fade(c, new SKRect(Math.Max(0, w - Width), 0, w, h), new SKPoint(w, 0), new SKPoint(w - Width, 0));
            }
        });
        return new EffectOutput(image, default);
    }

    private static void Fade(SKCanvas canvas, SKRect strip, SKPoint edge, SKPoint inside)
    {
        using var ramp = SKShader.CreateLinearGradient(edge, inside, [SKColors.Transparent, SKColors.Black], SKShaderTileMode.Clamp);
        using var paint = new SKPaint { Shader = ramp, BlendMode = SKBlendMode.DstIn };
        canvas.DrawRect(strip, paint);
    }

    public override ImageEffect Normalize() => this with { Width = Math.Clamp(Width, 1, MaxWidth), Edges = Edges & Edges.All };
}

/// <summary>
/// The image turned in space by <see cref="Angle"/> degrees and seen in perspective, like a card on a table. The
/// canvas fits the turned picture; the rest is transparent. Positive angles move the right (or bottom) side away.
/// </summary>
public sealed record PerspectiveEffect(int Angle = 20, PerspectiveAxis Axis = PerspectiveAxis.Horizontal) : ImageEffect
{
    public const int MaxAngle = 60;

    /// <summary>How far the eye is, in image sizes: closer looks more dramatic.</summary>
    private const float Distance = 2.5f;

    public override string Name => EditorStrings.EffectPerspective;

    public override EffectOutput Apply(SKImage source)
    {
        ArgumentNullException.ThrowIfNull(source);
        float w = source.Width;
        float h = source.Height;
        if (Angle == 0)
        {
            return new EffectOutput(SkiaImaging.Draw(source.Width, source.Height, c => c.DrawImage(source, 0, 0, Exact, null)), default);
        }

        SKPoint[] corners = [Project(0, 0, w, h), Project(w, 0, w, h), Project(w, h, w, h), Project(0, h, w, h)];
        var left = MathF.Floor(corners.Min(p => p.X));
        var top = MathF.Floor(corners.Min(p => p.Y));
        var width = (int)MathF.Ceiling(corners.Max(p => p.X) - left);
        var height = (int)MathF.Ceiling(corners.Max(p => p.Y) - top);
        var quad = corners.Select(p => new SKPoint(p.X - left, p.Y - top)).ToArray();
        var matrix = Homography.RectToQuad(w, h, quad);
        var image = SkiaImaging.Draw(width, height, c =>
        {
            c.SetMatrix(in matrix);
            using var paint = new SKPaint { IsAntialias = true };
            c.DrawImage(source, SKRect.Create(w, h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
        });
        return new EffectOutput(image, default);
    }

    /// <summary>A point of the flat image turned about its centre line and projected back onto the page.</summary>
    private SKPoint Project(float x, float y, float w, float h)
    {
        var radians = Angle * MathF.PI / 180;
        var eye = Distance * MathF.Max(w, h);
        var dx = x - (w / 2);
        var dy = y - (h / 2);
        float px, py, z;
        if (Axis == PerspectiveAxis.Horizontal)
        {
            (px, py, z) = (dx * MathF.Cos(radians), dy, dx * MathF.Sin(radians));
        }
        else
        {
            (px, py, z) = (dx, dy * MathF.Cos(radians), dy * MathF.Sin(radians));
        }

        var scale = eye / (eye + z);
        return new SKPoint((w / 2) + (px * scale), (h / 2) + (py * scale));
    }

    public override ImageEffect Normalize() => this with
    {
        Angle = Math.Clamp(Angle, -MaxAngle, MaxAngle),
        Axis = Enum.IsDefined(Axis) ? Axis : PerspectiveAxis.Horizontal,
    };
}

/// <summary>Projective maps between a rectangle and any four-cornered shape.</summary>
public static class Homography
{
    /// <summary>
    /// The matrix taking the rectangle 0,0 – <paramref name="width"/>,<paramref name="height"/> to <paramref name="quad"/>
    /// (top-left, top-right, bottom-right, bottom-left), after Heckbert's square-to-quad mapping.
    /// </summary>
    public static SKMatrix RectToQuad(float width, float height, IReadOnlyList<SKPoint> quad)
    {
        ArgumentNullException.ThrowIfNull(quad);
        if (quad.Count != 4)
        {
            throw new ArgumentException("A quad has four corners.", nameof(quad));
        }

        var (p0, p1, p2, p3) = (quad[0], quad[1], quad[2], quad[3]);
        var sx = p0.X - p1.X + p2.X - p3.X;
        var sy = p0.Y - p1.Y + p2.Y - p3.Y;
        float a, b, c, d, e, f, g, i;
        if (MathF.Abs(sx) < 1e-6f && MathF.Abs(sy) < 1e-6f)
        {
            (a, b, c) = (p1.X - p0.X, p2.X - p1.X, p0.X);
            (d, e, f) = (p1.Y - p0.Y, p2.Y - p1.Y, p0.Y);
            (g, i) = (0, 0);
        }
        else
        {
            var dx1 = p1.X - p2.X;
            var dx2 = p3.X - p2.X;
            var dy1 = p1.Y - p2.Y;
            var dy2 = p3.Y - p2.Y;
            var den = (dx1 * dy2) - (dx2 * dy1);
            g = ((sx * dy2) - (dx2 * sy)) / den;
            i = ((dx1 * sy) - (sx * dy1)) / den;
            (a, b, c) = (p1.X - p0.X + (g * p1.X), p3.X - p0.X + (i * p3.X), p0.X);
            (d, e, f) = (p1.Y - p0.Y + (g * p1.Y), p3.Y - p0.Y + (i * p3.Y), p0.Y);
        }

        var square = new SKMatrix(a, b, c, d, e, f, g, i, 1);
        return square.PreConcat(SKMatrix.CreateScale(1 / width, 1 / height));
    }
}
