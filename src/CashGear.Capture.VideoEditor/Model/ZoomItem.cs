namespace CashGear.Capture.VideoEditor.Model;

public enum ZoomFocus
{
    /// <summary>The view follows the pointer (from the recording's metadata), as a camera on a spring would.</summary>
    FollowPointer,

    /// <summary>The view stays on (<see cref="ZoomItem.CenterX"/>, <see cref="ZoomItem.CenterY"/>).</summary>
    Fixed,
}

/// <summary>
/// A zoom into the screen pictures (the video tracks) while it lasts: from the whole picture it eases in to
/// <see cref="Scale"/> over <see cref="EaseIn"/> seconds, holds, and eases back out over <see cref="EaseOut"/>.
/// Zooms close together pan from one to the next without zooming out. It lives on an effect track; the webcam,
/// annotations and captions aren't zoomed.
/// </summary>
public sealed record ZoomItem : TimelineItem
{
    public const double DefaultLength = 3;
    public const double DefaultScale = 2;
    public const double DefaultEase = 0.6;
    public const double SmallestScale = 1.25;
    public const double LargestScale = 4;

    public double Length { get; init; } = DefaultLength;

    /// <summary>How much larger the pictures are when fully zoomed in (1.25 to 4).</summary>
    public double Scale { get; init; } = DefaultScale;

    public ZoomFocus Focus { get; init; }

    /// <summary>Where the view is centred (a fraction of the canvas) when it doesn't follow the pointer, or there is none.</summary>
    public double CenterX { get; init; } = 0.5;

    public double CenterY { get; init; } = 0.5;

    public double EaseIn { get; init; } = DefaultEase;

    public double EaseOut { get; init; } = DefaultEase;

    /// <summary>Made by Auto-Zoom, which replaces it when it runs again; a zoom changed by hand is kept.</summary>
    public bool Auto { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public override double Duration => Length;

    /// <summary>The scale used: <see cref="Scale"/> within its limits.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double ClampedScale => Math.Clamp(Scale, SmallestScale, LargestScale);

    /// <summary>The ease in and out, shortened in proportion when together they are longer than the zoom.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public (double In, double Out) Eases
    {
        get
        {
            var easeIn = Math.Max(0, EaseIn);
            var easeOut = Math.Max(0, EaseOut);
            var total = easeIn + easeOut;
            return total > Length && total > 0 ? (easeIn * Length / total, easeOut * Length / total) : (easeIn, easeOut);
        }
    }

    public override TimelineItem Slice(double from, double to)
    {
        from = Times.Round(Math.Max(from, Start));
        to = Times.Round(Math.Min(to, End));
        var length = Math.Max(0, to - from);
        return this with
        {
            Start = from,
            Length = length,
            EaseIn = KeptFade(EaseIn, from <= Start + Times.Epsilon, length),
            EaseOut = KeptFade(EaseOut, to >= End - Times.Epsilon, length),
        };
    }

    public override TimelineItem Lengthen(ItemEdge edge, double more) => edge == ItemEdge.Start
        ? this with { Start = Times.Round(Start - more), Length = Times.Round(Length + more) }
        : this with { Length = Times.Round(Length + more) };
}
