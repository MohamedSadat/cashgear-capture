using CashGear.Capture.Editor.Document;
using CashGear.Capture.Editor.Imaging;
using CashGear.Capture.Editor.Model;
using SkiaSharp;

namespace CashGear.Capture.Editor.Rendering;

/// <summary>
/// Draws a document: the image, then each annotation in order, clipped to the image. The screen and the exported
/// file both come from here, which is why the export matches what the editor shows at 100%.
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
        foreach (var annotation in state.Annotations)
        {
            if (annotation.Id != hidden)
            {
                annotation.Render(canvas, context);
            }
        }

        extra?.Render(canvas, context);
        canvas.Restore();
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
