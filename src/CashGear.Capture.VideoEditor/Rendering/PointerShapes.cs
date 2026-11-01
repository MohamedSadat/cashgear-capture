using System.ComponentModel;
using CashGear.Capture.Core.Overlays;
using SkiaSharp;

namespace CashGear.Capture.VideoEditor.Rendering;

internal enum PointerKind
{
    Arrow,
    Hand,
    IBeam,
}

/// <summary>A recorded pointer shape recognised as a standard one: how large it was, where it was, and whether it was dark.</summary>
/// <param name="UnitScale">How many of the recording's pixels a unit of the drawn shape is.</param>
/// <param name="OffsetX">Where the recorded picture's solid part starts from its hotspot, in the recording's pixels.</param>
/// <param name="OffsetY">The same, down.</param>
internal sealed record VectorPointer(PointerKind Kind, float UnitScale, bool Dark, float OffsetX, float OffsetY);

/// <summary>
/// The standard arrow, hand and I-beam as shapes, drawn instead of the scaled picture Windows gave, so they stay sharp
/// when they are made larger or zoomed into. A recorded shape counts as one of them by its outline and its hotspot;
/// anything else (a custom or coloured pointer) is drawn as recorded.
/// </summary>
[Localizable(false)]
internal static class PointerShapes
{
    private const byte Solid = 128;

    private static readonly SKPath Arrow = SKPath.ParseSvgPathData("M0 0V16.2L3.6 12.9 5.9 18.3 8.3 17.3 6 12 10.9 11.6Z");

    private static readonly SKPath Hand = SKPath.ParseSvgPathData("M4.2 1.4C4.2 .6 4.8 0 5.6 0S7 .6 7 1.4V8.2C7.3 7.9 7.7 7.8 8.1 7.8 8.8 7.8 9.4 8.2 9.6 8.8 9.9 8.5 10.3 8.4 10.7 8.4 11.4 8.4 12 8.9 12.2 9.5 12.5 9.3 12.8 9.2 13.2 9.2 14.1 9.2 14.8 9.9 14.8 10.8V15.2C14.8 18.4 12.6 20.6 9.6 20.6H8.4C6.4 20.6 5 19.7 3.9 18L.7 13C.2 12.2 .4 11.3 1.1 10.8 1.8 10.3 2.8 10.5 3.3 11.2L4.2 12.5Z");

    private static readonly SKPath IBeam = SKPath.ParseSvgPathData("M1 0H3.5C4 0 4.3 .3 4.5 .6 4.7 .3 5 0 5.5 0H8V1.4H5.6C5.4 1.4 5.2 1.6 5.2 1.8V15.2C5.2 15.4 5.4 15.6 5.6 15.6H8V17H5.5C5 17 4.7 16.7 4.5 16.4 4.3 16.7 4 17 3.5 17H1V15.6H3.4C3.6 15.6 3.8 15.4 3.8 15.2V1.8C3.8 1.6 3.6 1.4 3.4 1.4H1Z");

    /// <summary>The standard pointer a recorded shape is, or null when it isn't one.</summary>
    public static VectorPointer? Classify(MetadataCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        byte[] pixels;
        try
        {
            pixels = cursor.Pixels();
        }
        catch (FormatException)
        {
            return null;
        }

        if (pixels.Length < cursor.Width * cursor.Height * 4)
        {
            return null;
        }

        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1, count = 0;
        long light = 0, saturation = 0;
        for (var y = 0; y < cursor.Height; y++)
        {
            for (var x = 0; x < cursor.Width; x++)
            {
                var at = ((y * cursor.Width) + x) * 4;
                if (pixels[at + 3] < Solid)
                {
                    continue;
                }

                (left, top, right, bottom) = (Math.Min(left, x), Math.Min(top, y), Math.Max(right, x), Math.Max(bottom, y));
                count++;
                var (blue, green, red) = (pixels[at], pixels[at + 1], pixels[at + 2]);
                light += (red + green + blue) / 3;
                saturation += Math.Max(red, Math.Max(green, blue)) - Math.Min(red, Math.Min(green, blue));
            }
        }

        if (count < 8)
        {
            return null;
        }

        // A coloured pointer scheme keeps its own picture: the standard ones are black and white.
        if (saturation / count > 24)
        {
            return null;
        }

        var width = (float)(right - left + 1);
        var height = (float)(bottom - top + 1);
        var aspect = width / height;
        var hotX = (cursor.HotspotX - left) / width;
        var hotY = (cursor.HotspotY - top) / height;
        var filled = count / (width * height);
        var dark = light / count < 128;
        var (offsetX, offsetY) = ((float)(left - cursor.HotspotX), (float)(top - cursor.HotspotY));

        // Windows' I-beam isn't centred on its hotspot; the arrow and hand have theirs at their tips. A recorded I-beam
        // is black with a white edge whatever the share of each (the screen inverted under it is drawn that way).
        if (aspect < 0.62f && hotX is > 0.1f and < 0.9f && hotY is > 0.3f and < 0.7f && filled < 0.75f)
        {
            return new VectorPointer(PointerKind.IBeam, height / 17.7f, true, offsetX, offsetY);
        }

        if (hotX < 0.15f && hotY < 0.12f && aspect is > 0.42f and < 0.8f && filled is > 0.3f and < 0.75f)
        {
            return new VectorPointer(PointerKind.Arrow, height / 19.4f, dark, offsetX, offsetY);
        }

        if (hotY < 0.15f && hotX is > 0.2f and < 0.6f && aspect is > 0.55f and < 1f && filled > 0.4f)
        {
            return new VectorPointer(PointerKind.Hand, height / 21.7f, dark, offsetX, offsetY);
        }

        return null;
    }

    /// <summary>The pointer with its hotspot at (<paramref name="x"/>, <paramref name="y"/>), <paramref name="scale"/> times its recorded size.</summary>
    public static void Draw(SKCanvas canvas, VectorPointer pointer, float x, float y, float scale, float opacity)
    {
        var path = pointer.Kind switch
        {
            PointerKind.Hand => Hand,
            PointerKind.IBeam => IBeam,
            _ => Arrow,
        };
        var alpha = (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
        var line = pointer.Kind == PointerKind.IBeam ? 0.7f : 1.1f;
        var bounds = path.Bounds;
        canvas.Save();
        canvas.Translate(x, y);
        canvas.Scale(scale);

        // Where the recorded picture was round its hotspot, so the shape is where the pointer was seen.
        canvas.Translate(pointer.OffsetX, pointer.OffsetY);
        canvas.Scale(pointer.UnitScale);
        canvas.Translate((line / 2) - bounds.Left, (line / 2) - bounds.Top);

        // A thin line of the other colour keeps it visible on anything.
        using var outline = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = line, StrokeJoin = SKStrokeJoin.Round, Color = (pointer.Dark ? SKColors.White : SKColors.Black).WithAlpha(alpha) };
        canvas.DrawPath(path, outline);
        using var fill = new SKPaint { IsAntialias = true, Color = (pointer.Dark ? SKColors.Black : SKColors.White).WithAlpha(alpha) };
        canvas.DrawPath(path, fill);
        canvas.Restore();
    }

    /// <summary>
    /// How large the pointer is at <paramref name="time"/> because of a press: it shrinks to
    /// <see cref="Pressed"/> over 60 ms at each press, stays so while the button is down, and comes back over 120 ms.
    /// </summary>
    public static double PressScale(IReadOnlyList<MetadataClick> clicks, double time)
    {
        ArgumentNullException.ThrowIfNull(clicks);
        const double down = 0.06;
        const double up = 0.12;
        var least = 1.0;
        foreach (var click in clicks)
        {
            if (time < click.T || time > (click.Up ?? double.MaxValue) + up)
            {
                continue;
            }

            var into = Math.Clamp((time - click.T) / down, 0, 1);
            var back = click.Up is { } released ? Math.Clamp((time - released) / up, 0, 1) : 0;
            var amount = Smooth(into) * (1 - Smooth(back));
            least = Math.Min(least, 1 - ((1 - Pressed) * amount));
        }

        return least;
    }

    /// <summary>The pointer's size while a button is down.</summary>
    public const double Pressed = 0.82;

    private static double Smooth(double x) => x * x * (3 - (2 * x));
}
