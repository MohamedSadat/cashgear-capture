using CashGear.Capture.Editor.Model;
using CashGear.Capture.VideoEditor.Model;

namespace CashGear.Capture.VideoEditor.Rendering;

/// <summary>What an annotation's animations do at a moment: all of it is a function of the item and the time.</summary>
/// <param name="Opacity">0 to 1, on top of its other levels.</param>
/// <param name="Lift">How far it is moved up, as a part of its own height.</param>
/// <param name="Scale">Its size about its middle.</param>
/// <param name="Reveal">How much of the text shows, 0 to 1.</param>
public readonly record struct TextMoment(double Opacity, double Lift, double Scale, double Reveal)
{
    public static TextMoment Still { get; } = new(1, 0, 1, 1);

    public bool Moves => Lift != 0 || Math.Abs(Scale - 1) > 1e-9;
}

/// <summary>Annotations coming in, going on and going out: fade, slide, pop and typewriter.</summary>
public static class TextAnimator
{
    /// <summary>A slide's distance, as a part of the annotation's own height.</summary>
    private const double SlideDistance = 0.6;

    /// <summary>A pop's size at its far end.</summary>
    private const double PopSize = 0.6;

    /// <summary>How much a gentle animation during the item does: its level, drift (of its height) and size.</summary>
    private const double GentleLevel = 0.25;

    private const double GentleDrift = 0.08;

    private const double GentleSize = 0.04;

    /// <summary>The moment <paramref name="time"/> of the timeline for <paramref name="item"/>.</summary>
    public static TextMoment At(AnnotationItem item, double time)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.TextIn is null && item.TextOut is null && item.TextDuring is null)
        {
            return TextMoment.Still;
        }

        var into = time - item.Start;
        var left = item.End - time;
        var inLength = Fitted(item.TextIn, item.Length);
        var outLength = Fitted(item.TextOut, item.Length);
        var moment = TextMoment.Still;
        if (item.TextDuring is { Kind: not TextAnimationKind.None } during)
        {
            moment = During(during, into, inLength, item.Length - inLength - outLength);
        }

        if (item.TextIn is { Kind: not TextAnimationKind.None } coming && into < inLength)
        {
            moment = Edge(moment, coming, MotionCurves.Ease(coming.Easing, into / inLength), arriving: true);
        }

        if (item.TextOut is { Kind: not TextAnimationKind.None } going && left < outLength)
        {
            moment = Edge(moment, going, MotionCurves.Ease(going.Easing, left / outLength), arriving: false);
        }

        return moment;
    }

    /// <summary>How many grapheme clusters of <paramref name="text"/> show at <paramref name="reveal"/>.</summary>
    public static int Graphemes(string text, double reveal) =>
        (int)Math.Floor((TextAnnotation.GraphemeCount(text) * Math.Clamp(reveal, 0, 1)) + 1e-9);

    /// <summary>An animation's length in an item: never more than half of it, so in and out both fit.</summary>
    private static double Fitted(TextAnimation? animation, double length) =>
        animation is { Kind: not TextAnimationKind.None } ? Math.Clamp(animation.Length, 0.001, Math.Max(0.002, length / 2)) : 0;

    private static TextMoment During(TextAnimation animation, double into, double start, double length)
    {
        var cycle = Math.Max(0.1, animation.Length);
        var wave = Math.Sin(2 * Math.PI * (into - start) / cycle);
        return animation.Kind switch
        {
            TextAnimationKind.Fade => TextMoment.Still with { Opacity = 1 - (GentleLevel * (0.5 + (0.5 * wave))) },
            TextAnimationKind.Slide => TextMoment.Still with { Lift = GentleDrift * wave },
            TextAnimationKind.Pop => TextMoment.Still with { Scale = 1 + (GentleSize * wave) },
            TextAnimationKind.Typewriter => TextMoment.Still with { Reveal = length <= 0 ? 1 : Math.Clamp((into - start) / length, 0, 1) },
            _ => TextMoment.Still,
        };
    }

    /// <summary>The moment with an edge's animation on it: <paramref name="amount"/> is 1 where it is in place.</summary>
    private static TextMoment Edge(TextMoment moment, TextAnimation animation, double amount, bool arriving) => animation.Kind switch
    {
        TextAnimationKind.Fade => moment with { Opacity = moment.Opacity * Math.Clamp(amount, 0, 1) },
        TextAnimationKind.Slide => moment with { Opacity = moment.Opacity * Math.Clamp(amount, 0, 1), Lift = moment.Lift + ((arriving ? -1 : 1) * SlideDistance * (1 - amount)) },
        TextAnimationKind.Pop => moment with { Opacity = moment.Opacity * Math.Clamp(amount * 2, 0, 1), Scale = moment.Scale * (PopSize + ((1 - PopSize) * amount)) },
        TextAnimationKind.Typewriter => moment with { Reveal = Math.Min(moment.Reveal, Math.Clamp(amount, 0, 1)) },
        _ => moment,
    };
}
