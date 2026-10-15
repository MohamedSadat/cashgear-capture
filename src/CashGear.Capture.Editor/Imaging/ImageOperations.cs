using System.Collections.Immutable;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Editor.Document;
using CashGear.Capture.Editor.Model;
using SkiaSharp;

namespace CashGear.Capture.Editor.Imaging;

/// <summary>
/// Whole-image edits. Each takes a document state and returns a new one: a new image, plus the annotations moved
/// to stay over the same content. The input is never modified, so the old state stays valid for undo.
/// </summary>
public static class ImageOperations
{
    public const int MaxDimension = 30_000;

    private static readonly SKSamplingOptions Exact = new(SKFilterMode.Nearest);

    /// <summary>Keeps <paramref name="area"/> (clipped to the image). Annotations entirely outside it are dropped.</summary>
    public static DocumentState Crop(DocumentState state, SKRectI area)
    {
        ArgumentNullException.ThrowIfNull(state);
        area = SKRectI.Intersect(area, SKRectI.Create(state.Image.Width, state.Image.Height));
        if (area.Width <= 0 || area.Height <= 0)
        {
            throw new ArgumentException("The crop area does not overlap the image.", nameof(area));
        }

        var image = SkiaImaging.Draw(area.Width, area.Height, c => c.DrawImage(state.Image, -area.Left, -area.Top, Exact, null));
        var kept = SKRect.Create(area.Width, area.Height);
        var annotations = Transform(state.Annotations, SKMatrix.CreateTranslation(-area.Left, -area.Top), 1)
            .Where(a => a.Bounds.IntersectsWith(kept))
            .ToImmutableList();
        return new DocumentState(image, annotations);
    }

    /// <summary>Turns the image a quarter turn; exact (no resampling).</summary>
    public static DocumentState Rotate(DocumentState state, bool clockwise)
    {
        ArgumentNullException.ThrowIfNull(state);
        var w = state.Image.Width;
        var h = state.Image.Height;

        // Clockwise: (x, y) → (h − y, x). Counter-clockwise: (x, y) → (y, w − x).
        var matrix = clockwise
            ? new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1)
            : new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1);
        var image = SkiaImaging.Draw(h, w, c =>
        {
            c.SetMatrix(in matrix);
            c.DrawImage(state.Image, 0, 0, Exact, null);
        });
        return new DocumentState(image, Transform(state.Annotations, matrix, 1));
    }

    /// <summary>Scales the image to <paramref name="width"/> × <paramref name="height"/>; annotations scale with it.</summary>
    public static DocumentState Resize(DocumentState state, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, MaxDimension);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, MaxDimension);

        var sx = (float)width / state.Image.Width;
        var sy = (float)height / state.Image.Height;
        var image = SkiaImaging.Draw(width, height, c => c.DrawImage(
            state.Image, SKRect.Create(width, height), new SKSamplingOptions(SKCubicResampler.Mitchell), null));
        return new DocumentState(image, Transform(state.Annotations, SKMatrix.CreateScale(sx, sy), (sx + sy) / 2));
    }

    /// <summary>A solid frame of <paramref name="thickness"/> pixels around the image (the canvas grows; transparent parts stay transparent).</summary>
    public static DocumentState AddBorder(DocumentState state, int thickness, SKColor color)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(thickness);
        var w = state.Image.Width + (2 * thickness);
        var h = state.Image.Height + (2 * thickness);
        var image = SkiaImaging.Draw(w, h, c =>
        {
            using var paint = new SKPaint { Color = color, Style = SKPaintStyle.Fill };
            c.DrawRect(0, 0, w, thickness, paint);
            c.DrawRect(0, h - thickness, w, thickness, paint);
            c.DrawRect(0, thickness, thickness, h - (2 * thickness), paint);
            c.DrawRect(w - thickness, thickness, thickness, h - (2 * thickness), paint);
            c.DrawImage(state.Image, thickness, thickness, Exact, null);
        });
        return new DocumentState(image, Transform(state.Annotations, SKMatrix.CreateTranslation(thickness, thickness), 1));
    }

    /// <summary>The same soft drop shadow as window captures (<see cref="DropShadow"/>), on a transparent margin.</summary>
    public static DocumentState AddShadow(DocumentState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var metadata = new CaptureMetadata
        {
            Kind = CaptureKind.Region,
            SourceBounds = new PixelRect(0, 0, state.Image.Width, state.Image.Height),
        };
        using var source = SkiaImaging.ToCapturedImage(state.Image, metadata);
        using var shadowed = DropShadow.Apply(source);
        var margin = DropShadow.MarginFor(DropShadow.DefaultRadius, DropShadow.DefaultOffsetY);
        var image = SkiaImaging.ToSKImage(shadowed);
        return new DocumentState(image, Transform(state.Annotations, SKMatrix.CreateTranslation(margin, margin), 1));
    }

    private static ImmutableList<Annotation> Transform(ImmutableList<Annotation> annotations, SKMatrix matrix, float scale) =>
        annotations.Select(a => a.Transform(matrix, scale)).ToImmutableList();
}
