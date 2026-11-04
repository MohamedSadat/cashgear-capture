using CashGear.Capture.VideoEditor.Model;
using SkiaSharp;

namespace CashGear.Capture.VideoEditor.Rendering;

/// <summary>What an item's motion does on the canvas, for drawing it and for finding it under the pointer.</summary>
public static class MotionDrawing
{
    /// <summary>The motion's matrix for something whose area is <paramref name="area"/>: scaled and turned about its middle, then moved.</summary>
    public static SKMatrix Matrix(MotionState state, SKRect area, ProjectCanvas canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        var (x, y) = (area.MidX, area.MidY);
        var matrix = SKMatrix.CreateTranslation((float)(state.X * canvas.Width), (float)(state.Y * canvas.Height));
        if (state.Rotation != 0)
        {
            matrix = matrix.PreConcat(SKMatrix.CreateRotationDegrees((float)state.Rotation, x, y));
        }

        if (Math.Abs(state.Scale - 1) > 1e-9)
        {
            matrix = matrix.PreConcat(SKMatrix.CreateScale((float)state.Scale, (float)state.Scale, x, y));
        }

        return matrix;
    }

    /// <summary>A rectangle with the crop's parts of it cut off; at least a pixel stays.</summary>
    public static SKRect Cropped(MotionState state, SKRect area)
    {
        var left = area.Left + (area.Width * (float)state.CropLeft);
        var top = area.Top + (area.Height * (float)state.CropTop);
        var right = Math.Max(left + 1, area.Right - (area.Width * (float)state.CropRight));
        var bottom = Math.Max(top + 1, area.Bottom - (area.Height * (float)state.CropBottom));
        return new SKRect(left, top, right, bottom);
    }

    /// <summary>What an area covers after the motion: the bounds of its cropped, turned and moved outline.</summary>
    public static SKRect Bounds(MotionState state, SKRect area, ProjectCanvas canvas) =>
        Matrix(state, area, canvas).MapRect(Cropped(state, area));
}
