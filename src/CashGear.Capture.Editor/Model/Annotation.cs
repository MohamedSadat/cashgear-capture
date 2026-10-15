using CashGear.Capture.Editor.Rendering;
using SkiaSharp;

namespace CashGear.Capture.Editor.Model;

public enum AnnotationKind
{
    Arrow,
    Line,
    Rectangle,
    Ellipse,
    Freehand,
    Highlight,
    Text,
    StepNumber,
    Blur,
    Pixelate,
}

/// <summary>
/// A vector object drawn over the captured image. Annotations are immutable: every edit makes a new record with
/// the same <see cref="Id"/>, which is what makes undo/redo a matter of swapping records. All coordinates are
/// image pixels; nothing touches the image itself until export.
/// </summary>
public abstract record Annotation(AnnotationId Id, AnnotationStyle Style)
{
    public abstract AnnotationKind Kind { get; }

    /// <summary>The style parts this kind uses (the properties panel shows only these).</summary>
    public abstract StyleParts StyleParts { get; }

    /// <summary>Everything the annotation may paint, including the stroke.</summary>
    public abstract SKRect Bounds { get; }

    /// <summary>Whether <paramref name="point"/> is on the annotation, allowing <paramref name="tolerance"/> pixels of slack.</summary>
    public abstract bool HitTest(SKPoint point, float tolerance);

    /// <summary>The grips for resizing; empty for annotations that can only be moved.</summary>
    public virtual IReadOnlyList<Handle> GetHandles() => [];

    /// <summary>A copy with <paramref name="handle"/> dragged to <paramref name="position"/>.</summary>
    /// <param name="constrain">Shift held: 45° lines, squares and circles, proportional resizing.</param>
    public virtual Annotation WithHandleMoved(HandleKind handle, SKPoint position, bool constrain) => this;

    public abstract Annotation Offset(float dx, float dy);

    /// <summary>
    /// A copy moved by an image-wide operation (crop, rotate, resize, border). <paramref name="scale"/> is how much the
    /// image grew, so line widths and font sizes keep their look.
    /// </summary>
    public abstract Annotation Transform(SKMatrix matrix, float scale);

    /// <summary>Draws the annotation in image coordinates (the caller sets up zoom and pan on the canvas).</summary>
    public void Render(SKCanvas canvas, RenderContext context)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(context);
        if (Style.Opacity >= 1 || !UsesOpacityLayer)
        {
            RenderCore(canvas, context);
            return;
        }

        // A layer, so overlapping parts (an arrow's shaft and head, a freehand stroke crossing itself) don't darken.
        using var layer = new SKPaint { Color = SKColors.White.WithAlpha(ToAlpha(Style.Opacity)) };
        canvas.SaveLayer(Geometry.Inflate(Bounds, 2), layer);
        RenderCore(canvas, context);
        canvas.Restore();
    }

    /// <summary>False for kinds that apply <see cref="AnnotationStyle.Opacity"/> themselves (or ignore it).</summary>
    protected virtual bool UsesOpacityLayer => true;

    protected abstract void RenderCore(SKCanvas canvas, RenderContext context);

    protected static byte ToAlpha(float opacity) => (byte)Math.Clamp(MathF.Round(opacity * 255), 0, 255);

    protected SKPaint StrokePaint(SKStrokeJoin join = SKStrokeJoin.Round)
    {
        var style = Style;
        return new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            Color = style.Color,
            StrokeWidth = style.StrokeWidth,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = join,
        };
    }

    protected static SKPaint FillPaint(SKColor color) => new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Fill,
        Color = color,
    };
}

public static class AnnotationKindExtensions
{
    /// <summary>Lower-case name for undo descriptions ("Undo add arrow").</summary>
    public static string DisplayName(this AnnotationKind kind) => kind switch
    {
        AnnotationKind.Freehand => "drawing",
        AnnotationKind.StepNumber => "step number",
        AnnotationKind.Pixelate => "pixelation",
        _ => kind.ToString().ToLowerInvariant(),
    };
}
