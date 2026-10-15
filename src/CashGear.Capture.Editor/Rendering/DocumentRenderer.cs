using CashGear.Capture.Editor.Document;
using CashGear.Capture.Editor.Imaging;
using CashGear.Capture.Editor.Model;
using SkiaSharp;

namespace CashGear.Capture.Editor.Rendering;

/// <summary>
/// Draws a document: the image, then the annotations, clipped to the image. Blur and pixelate areas go first (they
/// hide parts of the screenshot, never the marks drawn on it), then everything else in order. The screen and the
/// exported file both come from here, which is why the export matches what the editor shows at 100%.
/// </summary>
public static class DocumentRenderer
{
    /// <summary>Draws in image coordinates on <paramref name="canvas"/> (whatever zoom/pan matrix it has).</summary>
    /// <param name="extra">An annotation being drawn that is not in the document yet.</param>
    /// <param name="hidden">An annotation not to draw (text being edited in place).</param>
    public static void Render(
        SKCanvas canvas,
        DocumentState state,
        RenderContext context,
        Annotation? extra = null,
        AnnotationId? hidden = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(context);

        canvas.Save();
        canvas.ClipRect(SKRect.Create(state.Image.Width, state.Image.Height));
        canvas.DrawImage(state.Image, 0, 0, context.Sampling, null);
        foreach (var annotation in PaintOrder(state.Annotations, extra))
        {
            if (annotation.Id != hidden)
            {
                annotation.Render(canvas, context);
            }
        }

        canvas.Restore();
    }

    /// <summary>
    /// Bottom to top as drawn: effects first, then the rest, each in document order; <paramref name="extra"/> (a shape
    /// being drawn) goes last in its group. Hit-testing walks this in reverse.
    /// </summary>
    public static IEnumerable<Annotation> PaintOrder(IReadOnlyList<Annotation> annotations, Annotation? extra = null)
    {
        ArgumentNullException.ThrowIfNull(annotations);
        IEnumerable<Annotation> all = extra is null ? annotations : [.. annotations, extra];
        return all.Where(a => a is EffectAnnotation).Concat(all.Where(a => a is not EffectAnnotation));
    }

    /// <summary>The image with every annotation burned in, at full resolution.</summary>
    public static SKImage Flatten(DocumentState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        using var effects = new EffectCache();
        var context = new RenderContext(state.Image, effects, new SKSamplingOptions(SKFilterMode.Nearest));
        return SkiaImaging.Draw(state.Image.Width, state.Image.Height, canvas => Render(canvas, state, context));
    }
}
