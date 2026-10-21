using System.Collections.Immutable;
using CashGear.Capture.Editor.Document;
using CashGear.Capture.Editor.Imaging;
using CashGear.Capture.Editor.Model;
using CashGear.Capture.Editor.Rendering;
using SkiaSharp;

namespace CashGear.Capture.Editor.Effects;

/// <summary>
/// Applies a list of effects to a document state, in order, to the whole image or to a selected area. The input is
/// never changed and every intermediate image is freed, so the result can become an undo step.
/// </summary>
public static class EffectRunner
{
    private static readonly SKSamplingOptions Exact = new(SKFilterMode.Nearest);

    /// <summary>
    /// The state after <paramref name="effects"/>. On the whole image, annotations stay objects through effects that
    /// only add around the image (moved with it) and are merged into the image before any other. On an
    /// <paramref name="area"/>, annotations are merged first and the area is lifted: filled with
    /// <paramref name="background"/>, and the effects' result of its pixels drawn back over it (a shadow or border
    /// reaches past it). Returns <paramref name="state"/> itself when there are no effects.
    /// </summary>
    public static DocumentState Apply(DocumentState state, IReadOnlyList<ImageEffect> effects, SKRectI? area = null, SKColor? background = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(effects);
        if (effects.Count == 0)
        {
            return state;
        }

        return area is { } part ? ApplyToArea(state, effects, part, background ?? SKColors.White) : ApplyToImage(state, effects);
    }

    /// <summary>The image alone through the effects (no annotations): for the batch processor. The caller owns the result.</summary>
    public static SKImage Apply(SKImage image, IReadOnlyList<ImageEffect> effects)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(effects);
        var current = image;
        foreach (var effect in effects)
        {
            var next = effect.Apply(current).Image;
            if (!ReferenceEquals(current, image))
            {
                current.Dispose();
            }

            current = next;
        }

        return ReferenceEquals(current, image) ? SkiaImaging.Copy(image, SKRectI.Create(image.Width, image.Height)) : current;
    }

    /// <summary>"drop shadow" for one effect, "effects" for several (the undo step's name).</summary>
    public static string NameOf(IReadOnlyList<ImageEffect> effects) => effects is [var only] ? only.Name : "effects";

    private static DocumentState ApplyToImage(DocumentState state, IReadOnlyList<ImageEffect> effects)
    {
        var image = state.Image;
        var annotations = state.Annotations;
        foreach (var effect in effects)
        {
            if (!effect.KeepsAnnotations && !annotations.IsEmpty)
            {
                var flat = DocumentRenderer.Flatten(new DocumentState(image, annotations));
                Release(image, state.Image);
                image = flat;
                annotations = [];
            }

            var output = effect.Apply(image);
            Release(image, state.Image);
            image = output.Image;
            if (!annotations.IsEmpty && output.Offset != default)
            {
                var move = SKMatrix.CreateTranslation(output.Offset.X, output.Offset.Y);
                annotations = annotations.Select(a => a.Transform(move, 1)).ToImmutableList();
            }
        }

        return new DocumentState(image, annotations);
    }

    private static DocumentState ApplyToArea(DocumentState state, IReadOnlyList<ImageEffect> effects, SKRectI area, SKColor background)
    {
        area = SKRectI.Intersect(area, SKRectI.Create(state.Image.Width, state.Image.Height));
        if (area.Width <= 0 || area.Height <= 0)
        {
            throw new ArgumentException("The selected area is outside the image.", nameof(area));
        }

        var flat = state.Annotations.IsEmpty ? state.Image : DocumentRenderer.Flatten(state);
        try
        {
            var piece = SkiaImaging.Copy(flat, area);
            var offset = default(SKPointI);
            foreach (var effect in effects)
            {
                var output = effect.Apply(piece);
                piece.Dispose();
                piece = output.Image;
                offset = new SKPointI(offset.X + output.Offset.X, offset.Y + output.Offset.Y);
            }

            using (piece)
            {
                var image = SkiaImaging.Draw(flat.Width, flat.Height, c =>
                {
                    c.DrawImage(flat, 0, 0, Exact, null);
                    using (var fill = new SKPaint { Color = background, BlendMode = SKBlendMode.Src })
                    {
                        c.DrawRect(SKRect.Create(area.Left, area.Top, area.Width, area.Height), fill);
                    }

                    c.DrawImage(piece, area.Left - offset.X, area.Top - offset.Y, Exact, null);
                });
                return new DocumentState(image, []);
            }
        }
        finally
        {
            Release(flat, state.Image);
        }
    }

    /// <summary>Frees an intermediate image (never the state's own).</summary>
    private static void Release(SKImage image, SKImage original)
    {
        if (!ReferenceEquals(image, original))
        {
            image.Dispose();
        }
    }
}
