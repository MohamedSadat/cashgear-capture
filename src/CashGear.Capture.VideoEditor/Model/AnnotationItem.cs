using System.ComponentModel;

namespace CashGear.Capture.VideoEditor.Model;

public enum AnnotationShape
{
    Text,
    Arrow,
    Line,
    Rectangle,
    Ellipse,
    Highlight,
    Blur,
    Pixelate,
}

public enum Easing
{
    /// <summary>The same speed all the way.</summary>
    Linear,

    /// <summary>Starting and stopping gently.</summary>
    Smooth,
}

/// <summary>
/// Where an annotation is <see cref="Time"/> seconds after it starts, in canvas pixels: its box (a text's top-left
/// corner is (<see cref="X"/>, <see cref="Y"/>)), or, for a line or an arrow, from (X, Y) to (X + Width, Y + Height).
/// </summary>
public sealed record AnnotationKeyframe(double Time, double X, double Y, double Width, double Height);

/// <summary>How an annotation looks: colours as <c>#RRGGBB</c>, sizes in canvas pixels.</summary>
/// <param name="Fill">A rectangle's, an ellipse's or a text's background (null: none).</param>
/// <param name="Strength">Blur or pixelate strength.</param>
public sealed record AnnotationLook(
    string Color = "#E81123",
    double StrokeWidth = 6,
    string? Fill = null,
    double Opacity = 1,
    [Localizable(false)] string FontFamily = AnnotationLook.DefaultFontFamily,
    double FontSize = 48,
    bool Bold = false,
    double Strength = 14)
{
    /// <summary>The font of new text (a font's name, not text).</summary>
    [Localizable(false)]
    public const string DefaultFontFamily = "Segoe UI";
}

/// <summary>
/// Text, an arrow, a shape or a hidden area shown for <see cref="Length"/> seconds, fading in and out, at the place
/// its keyframes give it at each moment (moving between them, smoothly or evenly). Annotation tracks are drawn over
/// the picture tracks below them; a blur or pixelate area hides what is under it.
/// </summary>
public sealed record AnnotationItem : TimelineItem
{
    public const double DefaultLength = 3;

    public AnnotationShape Shape { get; init; }

    public double Length { get; init; } = DefaultLength;

    public string Text { get; init; } = "";

    public AnnotationLook Look { get; init; } = new();

    /// <summary>At least one, in time order; times are seconds from the annotation's start.</summary>
    public IReadOnlyList<AnnotationKeyframe> Keyframes { get; init; } = [];

    public Easing Easing { get; init; } = Easing.Smooth;

    public double FadeIn { get; init; }

    public double FadeOut { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public override double Duration => Length;

    /// <summary>Lines and arrows go from a point to a point; the others fill a box.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsLine => Shape is AnnotationShape.Arrow or AnnotationShape.Line;

    /// <summary>Where it is at timeline time <paramref name="time"/> (held at the first and last keyframes outside them).</summary>
    public AnnotationKeyframe At(double time)
    {
        var into = time - Start;
        if (Keyframes.Count == 0)
        {
            return new AnnotationKeyframe(into, 0, 0, 0, 0);
        }

        if (into <= Keyframes[0].Time)
        {
            return Keyframes[0] with { Time = into };
        }

        for (var i = 1; i < Keyframes.Count; i++)
        {
            var next = Keyframes[i];
            if (into <= next.Time)
            {
                var previous = Keyframes[i - 1];
                var span = next.Time - previous.Time;
                var t = span <= 0 ? 1 : (into - previous.Time) / span;
                if (Easing == Easing.Smooth)
                {
                    t = t * t * (3 - (2 * t)); // smoothstep: eases in and out
                }

                return new AnnotationKeyframe(
                    into,
                    previous.X + ((next.X - previous.X) * t),
                    previous.Y + ((next.Y - previous.Y) * t),
                    previous.Width + ((next.Width - previous.Width) * t),
                    previous.Height + ((next.Height - previous.Height) * t));
            }
        }

        return Keyframes[^1] with { Time = into };
    }

    /// <summary>Its level at <paramref name="time"/> from the fades (0 to 1).</summary>
    public double Fade(double time)
    {
        var level = 1.0;
        if (FadeIn > 0)
        {
            level = Math.Min(level, (time - Start) / FadeIn);
        }

        if (FadeOut > 0)
        {
            level = Math.Min(level, (End - time) / FadeOut);
        }

        return Math.Clamp(level, 0, 1);
    }

    /// <summary>The keyframe at <paramref name="time"/> (within half a frame at <paramref name="fps"/>), if there is one.</summary>
    public int KeyframeAt(double time, int fps)
    {
        var into = time - Start;
        for (var i = 0; i < Keyframes.Count; i++)
        {
            if (Math.Abs(Keyframes[i].Time - into) <= 0.5 / fps)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// A copy placed at <paramref name="place"/> at timeline time <paramref name="time"/>: that keyframe moved, or
    /// the only keyframe moved (it doesn't move), or a new keyframe there (it moves).
    /// </summary>
    public AnnotationItem PlacedAt(double time, AnnotationKeyframe place, int fps)
    {
        ArgumentNullException.ThrowIfNull(place);
        var into = Times.Round(Math.Clamp(time - Start, 0, Length));
        var index = KeyframeAt(time, fps);
        if (index < 0 && Keyframes.Count <= 1)
        {
            return this with { Keyframes = [place with { Time = Keyframes.Count == 1 ? Keyframes[0].Time : 0 }] };
        }

        var keyframes = Keyframes.ToList();
        if (index >= 0)
        {
            keyframes[index] = place with { Time = keyframes[index].Time };
        }
        else
        {
            keyframes.Add(place with { Time = into });
        }

        return this with { Keyframes = [.. keyframes.OrderBy(k => k.Time)] };
    }

    /// <summary>A copy with a keyframe at <paramref name="time"/> where it is now (so it can be moved from there).</summary>
    public AnnotationItem WithKeyframeAt(double time, int fps) =>
        KeyframeAt(time, fps) >= 0 ? this : this with { Keyframes = [.. Keyframes.Append(At(time) with { Time = Times.Round(Math.Clamp(time - Start, 0, Length)) }).OrderBy(k => k.Time)] };

    /// <summary>A copy without the keyframe at <paramref name="time"/> (the last one always stays).</summary>
    public AnnotationItem WithoutKeyframeAt(double time, int fps)
    {
        var index = KeyframeAt(time, fps);
        return index < 0 || Keyframes.Count <= 1 ? this : this with { Keyframes = [.. Keyframes.Where((_, i) => i != index)] };
    }

    public override TimelineItem Slice(double from, double to)
    {
        from = Times.Round(Math.Max(from, Start));
        to = Times.Round(Math.Min(to, End));
        var length = Math.Max(0, to - from);
        var shift = from - Start;

        // The keyframes inside, with where it is at both new ends, so the movement in the part kept is unchanged.
        var keyframes = new List<AnnotationKeyframe> { At(from) with { Time = 0 } };
        keyframes.AddRange(Keyframes.Where(k => k.Time > shift + Times.Epsilon && k.Time < shift + length - Times.Epsilon).Select(k => k with { Time = Times.Round(k.Time - shift) }));
        if (Keyframes.Count > 1)
        {
            keyframes.Add(At(to) with { Time = Times.Round(length) });
        }

        return this with
        {
            Start = from,
            Length = length,
            Keyframes = Keyframes.Count > 1 ? keyframes : [Keyframes.Count == 1 ? Keyframes[0] with { Time = 0 } : keyframes[0]],
            FadeIn = KeptFade(FadeIn, from <= Start + Times.Epsilon, length),
            FadeOut = KeptFade(FadeOut, to >= End - Times.Epsilon, length),
        };
    }

    public override TimelineItem Lengthen(ItemEdge edge, double more) => edge == ItemEdge.Start
        ? this with { Start = Times.Round(Start - more), Length = Times.Round(Length + more), Keyframes = [.. Keyframes.Select(k => k with { Time = Times.Round(k.Time + more) })] }
        : this with { Length = Times.Round(Length + more) };
}
