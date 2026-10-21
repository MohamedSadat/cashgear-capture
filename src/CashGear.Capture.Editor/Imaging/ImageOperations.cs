using System.Collections.Immutable;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Editor.Document;
using CashGear.Capture.Editor.Model;
using CashGear.Capture.Editor.Rendering;
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

    /// <summary>
    /// A canvas of <paramref name="width"/> × <paramref name="height"/> with the image placed at <paramref name="anchor"/>
    /// (the rest filled with <paramref name="background"/>); a smaller canvas cuts the image. Annotations move with
    /// the image, and those left entirely outside are dropped.
    /// </summary>
    public static DocumentState CanvasSize(DocumentState state, int width, int height, Effects.Anchor anchor, SKColor background)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, MaxDimension);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, MaxDimension);

        var (column, row) = ((int)anchor % 3, (int)anchor / 3);
        var dx = column * (width - state.Image.Width) / 2;
        var dy = row * (height - state.Image.Height) / 2;
        var image = SkiaImaging.Draw(width, height, c =>
        {
            c.Clear(background);
            c.DrawImage(state.Image, dx, dy, Exact, null);
        });
        var kept = SKRect.Create(width, height);
        var annotations = Transform(state.Annotations, SKMatrix.CreateTranslation(dx, dy), 1)
            .Where(a => a.Bounds.IntersectsWith(kept))
            .ToImmutableList();
        return new DocumentState(image, annotations);
    }

    /// <summary>
    /// The part of the image inside its plain borders: each side is trimmed while its whole row (or column) is the
    /// colour of that side's first corner, each channel within <paramref name="tolerance"/>. Null when there is
    /// nothing to trim (or the image is one plain colour).
    /// </summary>
    public static SKRectI? FindTrim(SKImage image, int tolerance = 8)
    {
        ArgumentNullException.ThrowIfNull(image);
        var w = image.Width;
        var h = image.Height;
        var pixels = SkiaImaging.ReadUnpremul(image);
        uint At(int x, int y) => BitConverter.ToUInt32(pixels, ((y * w) + x) * 4);
        bool Same(uint a, uint b)
        {
            for (var shift = 0; shift < 32; shift += 8)
            {
                if (Math.Abs((int)((a >> shift) & 0xFF) - (int)((b >> shift) & 0xFF)) > tolerance)
                {
                    return false;
                }
            }

            return true;
        }

        bool RowIs(int y, int from, int to, uint colour)
        {
            for (var x = from; x < to; x++)
            {
                if (!Same(At(x, y), colour))
                {
                    return false;
                }
            }

            return true;
        }

        bool ColumnIs(int x, int from, int to, uint colour)
        {
            for (var y = from; y < to; y++)
            {
                if (!Same(At(x, y), colour))
                {
                    return false;
                }
            }

            return true;
        }

        var top = 0;
        var topColour = At(0, 0);
        while (top < h && RowIs(top, 0, w, topColour))
        {
            top++;
        }

        if (top == h)
        {
            return null; // all one colour
        }

        var bottom = h;
        var bottomColour = At(0, h - 1);
        while (bottom > top && RowIs(bottom - 1, 0, w, bottomColour))
        {
            bottom--;
        }

        var left = 0;
        var leftColour = At(0, top);
        while (left < w && ColumnIs(left, top, bottom, leftColour))
        {
            left++;
        }

        var right = w;
        var rightColour = At(w - 1, top);
        while (right > left && ColumnIs(right - 1, top, bottom, rightColour))
        {
            right--;
        }

        var area = new SKRectI(left, top, right, bottom);
        return area.Width <= 0 || area.Height <= 0 || area == SKRectI.Create(w, h) ? null : area;
    }

    /// <summary>
    /// Turns the image by <paramref name="degrees"/> (clockwise). Quarter turns are exact and keep annotations as
    /// objects; any other angle merges them into the image, smooths the pixels and grows the canvas to fit, with
    /// <paramref name="background"/> in the corners.
    /// </summary>
    public static DocumentState RotateBy(DocumentState state, double degrees, SKColor background)
    {
        ArgumentNullException.ThrowIfNull(state);
        var angle = degrees % 360;
        if (angle < 0)
        {
            angle += 360;
        }

        if (Math.Abs(angle % 90) < 1e-9)
        {
            var turns = (int)Math.Round(angle / 90) % 4;
            if (turns == 0)
            {
                return state;
            }

            if (turns == 3)
            {
                return Rotate(state, clockwise: false);
            }

            var turned = Rotate(state, clockwise: true);
            if (turns == 1)
            {
                return turned;
            }

            using (turned.Image)
            {
                return Rotate(turned, clockwise: true);
            }
        }

        var w = state.Image.Width;
        var h = state.Image.Height;
        var radians = angle * Math.PI / 180;
        var cos = Math.Abs(Math.Cos(radians));
        var sin = Math.Abs(Math.Sin(radians));
        var width = (int)Math.Ceiling((w * cos) + (h * sin) - 1e-6);
        var height = (int)Math.Ceiling((w * sin) + (h * cos) - 1e-6);
        if (width > MaxDimension || height > MaxDimension)
        {
            throw new ArgumentException("The turned image would be too large.", nameof(degrees));
        }

        var flat = state.Annotations.IsEmpty ? state.Image : DocumentRenderer.Flatten(state);
        try
        {
            var image = SkiaImaging.Draw(width, height, c =>
            {
                c.Clear(background);
                c.Translate(width / 2f, height / 2f);
                c.RotateDegrees((float)angle);
                c.Translate(-w / 2f, -h / 2f);
                using var paint = new SKPaint { IsAntialias = true };
                c.DrawImage(flat, SKRect.Create(w, h), new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
            });
            return new DocumentState(image, []);
        }
        finally
        {
            if (!ReferenceEquals(flat, state.Image))
            {
                flat.Dispose();
            }
        }
    }

    /// <summary>Mirrors the image left to right (or top to bottom); exact. Annotations are mirrored too, text stays readable.</summary>
    public static DocumentState Flip(DocumentState state, bool horizontal)
    {
        ArgumentNullException.ThrowIfNull(state);
        var w = state.Image.Width;
        var h = state.Image.Height;
        var matrix = horizontal ? new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1) : new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1);
        var image = SkiaImaging.Draw(w, h, c =>
        {
            c.SetMatrix(in matrix);
            c.DrawImage(state.Image, 0, 0, Exact, null);
        });
        return new DocumentState(image, Transform(state.Annotations, matrix, 1));
    }

    private static ImmutableList<Annotation> Transform(ImmutableList<Annotation> annotations, SKMatrix matrix, float scale) =>
        annotations.Select(a => a.Transform(matrix, scale)).ToImmutableList();
}
