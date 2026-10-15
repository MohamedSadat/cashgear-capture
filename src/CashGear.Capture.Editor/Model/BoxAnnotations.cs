using CashGear.Capture.Editor.Rendering;
using SkiaSharp;

namespace CashGear.Capture.Editor.Model;

/// <summary>An annotation that fills or outlines a rectangle, resized by eight grips.</summary>
public abstract record BoxAnnotation(AnnotationId Id, AnnotationStyle Style, SKRect Rect) : Annotation(Id, Style)
{
    public override SKRect Bounds => Geometry.Inflate(Rect, OutlineReach + 1);

    /// <summary>How far the drawing reaches outside <see cref="Rect"/> (half the outline).</summary>
    protected virtual float OutlineReach => 0;

    public override bool HitTest(SKPoint point, float tolerance) => Geometry.Inflate(Rect, OutlineReach + tolerance).Contains(point);

    public override IReadOnlyList<Handle> GetHandles() => Geometry.BoxHandles(Rect);

    public override Annotation WithHandleMoved(HandleKind handle, SKPoint position, bool constrain) =>
        this with { Rect = Geometry.ResizeBox(Rect, handle, position, constrain) };

    public override Annotation Offset(float dx, float dy) => this with { Rect = Geometry.OffsetRect(Rect, dx, dy) };

    public override Annotation Transform(SKMatrix matrix, float scale) =>
        this with { Rect = Geometry.MapRect(matrix, Rect), Style = Style.Scaled(scale) };
}

public sealed record RectangleAnnotation(AnnotationId Id, AnnotationStyle Style, SKRect Rect) : BoxAnnotation(Id, Style, Rect)
{
    public override AnnotationKind Kind => AnnotationKind.Rectangle;

    public override StyleParts StyleParts => StyleParts.Color | StyleParts.StrokeWidth | StyleParts.Fill | StyleParts.Opacity;

    protected override float OutlineReach => Style.StrokeWidth / 2;

    /// <summary>Filled: anywhere inside. Outline only: on the outline, so shapes drawn inside it stay clickable.</summary>
    public override bool HitTest(SKPoint point, float tolerance)
    {
        var reach = OutlineReach + tolerance;
        if (!Geometry.Inflate(Rect, reach).Contains(point))
        {
            return false;
        }

        var inner = Geometry.Inflate(Rect, -reach);
        return Style.HasFill || inner.Width <= 0 || inner.Height <= 0 || !inner.Contains(point);
    }

    protected override void RenderCore(SKCanvas canvas, RenderContext context)
    {
        if (Style.HasFill)
        {
            using var fill = FillPaint(Style.Fill);
            canvas.DrawRect(Rect, fill);
        }

        using var stroke = StrokePaint(SKStrokeJoin.Miter);
        canvas.DrawRect(Rect, stroke);
    }
}

public sealed record EllipseAnnotation(AnnotationId Id, AnnotationStyle Style, SKRect Rect) : BoxAnnotation(Id, Style, Rect)
{
    public override AnnotationKind Kind => AnnotationKind.Ellipse;

    public override StyleParts StyleParts => StyleParts.Color | StyleParts.StrokeWidth | StyleParts.Fill | StyleParts.Opacity;

    protected override float OutlineReach => Style.StrokeWidth / 2;

    public override bool HitTest(SKPoint point, float tolerance)
    {
        var reach = OutlineReach + tolerance;
        var a = Rect.Width / 2;
        var b = Rect.Height / 2;
        if (!InsideEllipse(point, a + reach, b + reach))
        {
            return false;
        }

        return Style.HasFill || a - reach <= 0 || b - reach <= 0 || !InsideEllipse(point, a - reach, b - reach);
    }

    private bool InsideEllipse(SKPoint p, float a, float b)
    {
        var dx = (p.X - Rect.MidX) / a;
        var dy = (p.Y - Rect.MidY) / b;
        return (dx * dx) + (dy * dy) <= 1;
    }

    protected override void RenderCore(SKCanvas canvas, RenderContext context)
    {
        if (Style.HasFill)
        {
            using var fill = FillPaint(Style.Fill);
            canvas.DrawOval(Rect, fill);
        }

        using var stroke = StrokePaint();
        canvas.DrawOval(Rect, stroke);
    }
}

/// <summary>A highlighter: the colour multiplied into the image, so dark text under it stays dark and readable.</summary>
public sealed record HighlightAnnotation(AnnotationId Id, AnnotationStyle Style, SKRect Rect) : BoxAnnotation(Id, Style, Rect)
{
    public override AnnotationKind Kind => AnnotationKind.Highlight;

    public override StyleParts StyleParts => StyleParts.Color | StyleParts.Opacity;

    protected override bool UsesOpacityLayer => false; // a layer would multiply with nothing; the paint carries the alpha

    protected override void RenderCore(SKCanvas canvas, RenderContext context)
    {
        using var paint = FillPaint(Style.Color.WithAlpha(ToAlpha(Style.Opacity)));
        paint.BlendMode = SKBlendMode.Multiply;
        canvas.DrawRect(Rect, paint);
    }
}

/// <summary>An area of the image shown blurred or pixelated. The effect reads the captured image only, never other annotations.</summary>
public abstract record EffectAnnotation(AnnotationId Id, AnnotationStyle Style, SKRect Rect) : BoxAnnotation(Id, Style, Rect)
{
    public override StyleParts StyleParts => StyleParts.Strength;

    protected override bool UsesOpacityLayer => false;

    protected abstract EffectKind Effect { get; }

    /// <summary>The whole pixels the effect covers: <see cref="BoxAnnotation.Rect"/> rounded, clipped to the image.</summary>
    public SKRectI PixelArea(int imageWidth, int imageHeight) =>
        SKRectI.Intersect(SKRectI.Round(Rect), SKRectI.Create(imageWidth, imageHeight));

    protected override void RenderCore(SKCanvas canvas, RenderContext context)
    {
        var area = PixelArea(context.Image.Width, context.Image.Height);
        if (area.Width <= 0 || area.Height <= 0)
        {
            return;
        }

        var patch = context.Effects.Get(context.Image, area, Effect, Style.Strength);
        canvas.DrawImage(patch, area.Left, area.Top, context.Sampling, null);
    }
}

public sealed record BlurAnnotation(AnnotationId Id, AnnotationStyle Style, SKRect Rect) : EffectAnnotation(Id, Style, Rect)
{
    public override AnnotationKind Kind => AnnotationKind.Blur;

    protected override EffectKind Effect => EffectKind.Blur;
}

public sealed record PixelateAnnotation(AnnotationId Id, AnnotationStyle Style, SKRect Rect) : EffectAnnotation(Id, Style, Rect)
{
    public override AnnotationKind Kind => AnnotationKind.Pixelate;

    protected override EffectKind Effect => EffectKind.Pixelate;
}
