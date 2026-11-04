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

    /// <summary>A rounded box with a tail, and a text in it.</summary>
    SpeechBubble,

    /// <summary>A pen stroke through its <see cref="AnnotationItem.Points"/>.</summary>
    Freehand,

    /// <summary>A numbered badge.</summary>
    StepNumber,

    /// <summary>Everything outside its box dimmed.</summary>
    Spotlight,

    /// <summary>A circle of what is under it shown larger in a circle beside it.</summary>
    Magnify,

    /// <summary>A picture in its box: a template's logo.</summary>
    Picture,
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
/// <param name="Color">Its colour here (<c>#RRGGBB</c>), going to the next keyframe's (null: the look's).</param>
/// <param name="Opacity">Its opacity here, 0 to 1 (null: the look's).</param>
/// <param name="Ease">How it goes on from here (null: the annotation's <see cref="AnnotationItem.Easing"/>).</param>
/// <param name="Curve">A <see cref="MotionEasing.Bezier"/>'s x1, y1, x2, y2.</param>
public sealed record AnnotationKeyframe(double Time, double X, double Y, double Width, double Height, string? Color = null, double? Opacity = null, MotionEasing? Ease = null, IReadOnlyList<double>? Curve = null);

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

    /// <summary>A pen stroke's points, as parts of its box from the top-left (x, y, x, y…).</summary>
    public IReadOnlyList<double>? Points { get; init; }

    /// <summary>A step's number.</summary>
    public int Number { get; init; } = 1;

    /// <summary>A magnifier's circle, from the middle of what it shows, in canvas pixels; a speech bubble's tip, as parts of its box from the top-left.</summary>
    public double CalloutX { get; init; }

    public double CalloutY { get; init; }

    /// <summary>A picture's file among the project's assets.</summary>
    public string? PictureAssetId { get; init; }

    /// <summary>How its fade in goes.</summary>
    public MotionEasing FadeInCurve { get; init; }

    /// <summary>How its fade out goes.</summary>
    public MotionEasing FadeOutCurve { get; init; }

    /// <summary>How it comes in (null: it is simply there).</summary>
    public TextAnimation? TextIn { get; init; }

    /// <summary>What it does while it is there.</summary>
    public TextAnimation? TextDuring { get; init; }

    /// <summary>How it goes out.</summary>
    public TextAnimation? TextOut { get; init; }

    /// <summary>Its position, scale, rotation, opacity and crop over time, on top of its keyframes (null: none).</summary>
    public ItemMotion? Motion { get; init; }

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
                if (previous.Ease is { } own)
                {
                    t = MotionCurves.Ease(own, t, previous.Curve);
                }
                else if (Easing == Easing.Smooth)
                {
                    t = t * t * (3 - (2 * t)); // smoothstep: eases in and out
                }

                return new AnnotationKeyframe(
                    into,
                    previous.X + ((next.X - previous.X) * t),
                    previous.Y + ((next.Y - previous.Y) * t),
                    previous.Width + ((next.Width - previous.Width) * t),
                    previous.Height + ((next.Height - previous.Height) * t),
                    MixedColor(previous.Color, next.Color, t),
                    previous.Opacity is null && next.Opacity is null ? null : (previous.Opacity ?? Look.Opacity) + (((next.Opacity ?? Look.Opacity) - (previous.Opacity ?? Look.Opacity)) * t));
            }
        }

        return Keyframes[^1] with { Time = into };
    }

    /// <summary>A colour between two keyframes' (<c>#RRGGBB</c>), or null when neither has one of its own.</summary>
    private string? MixedColor(string? from, string? to, double t)
    {
        if (from is null && to is null)
        {
            return null;
        }

        static (int R, int G, int B) Parts(string text) => text.Length >= 7
            ? (Convert.ToInt32(text[^6..^4], 16), Convert.ToInt32(text[^4..^2], 16), Convert.ToInt32(text[^2..], 16))
            : (0, 0, 0);
        var (a, b) = (Parts(from ?? Look.Color), Parts(to ?? Look.Color));
        int Mix(int x, int y) => (int)Math.Round(Math.Clamp(x + ((y - x) * t), 0, 255));
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"#{Mix(a.R, b.R):X2}{Mix(a.G, b.G):X2}{Mix(a.B, b.B):X2}");
    }

    /// <summary>Its level at <paramref name="time"/> from the fades (0 to 1).</summary>
    public double Fade(double time)
    {
        var level = 1.0;
        if (FadeIn > 0)
        {
            level = Math.Min(level, MotionCurves.Ease(FadeInCurve, (time - Start) / FadeIn));
        }

        if (FadeOut > 0)
        {
            level = Math.Min(level, MotionCurves.Ease(FadeOutCurve, (End - time) / FadeOut));
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
            Motion = Motion?.Sliced(shift, length),
            TextIn = from <= Start + Times.Epsilon ? TextIn : null,
            TextOut = to >= End - Times.Epsilon ? TextOut : null,
        };
    }

    public override TimelineItem Lengthen(ItemEdge edge, double more) => edge == ItemEdge.Start
        ? this with { Start = Times.Round(Start - more), Length = Times.Round(Length + more), Keyframes = [.. Keyframes.Select(k => k with { Time = Times.Round(k.Time + more) })], Motion = Motion?.Shifted(more) }
        : this with { Length = Times.Round(Length + more) };
}
