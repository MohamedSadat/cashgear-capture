namespace CashGear.Capture.VideoEditor.Model;

/// <summary>How the screen and the webcam share the canvas.</summary>
public enum LayoutKind
{
    /// <summary>Each picture where its own placement puts it (usually the webcam in a corner).</summary>
    CornerBubble,

    /// <summary>Beside each other, or one above the other on a canvas that isn't wide.</summary>
    SideBySide,

    /// <summary>The webcam fills the canvas.</summary>
    CameraOnly,

    /// <summary>The webcam isn't shown.</summary>
    ScreenOnly,
}

/// <summary>
/// A stretch of the timeline with one layout, on a layout track. It sets the screen's and the webcam's places over its
/// time; the change into it takes <see cref="Transition"/> seconds from its start (and back to each picture's own
/// placement after its end, when nothing follows).
/// </summary>
public sealed record LayoutItem : TimelineItem
{
    public const double DefaultLength = 5;
    public const double DefaultTransition = 0.4;

    public LayoutKind Kind { get; init; }

    public double Length { get; init; } = DefaultLength;

    public double Transition { get; init; } = DefaultTransition;

    /// <summary>Put there by reframing for a narrower canvas, which takes it away again when the canvas goes back.</summary>
    public bool Auto { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public override double Duration => Length;

    public override TimelineItem Slice(double from, double to)
    {
        var start = Times.Round(Math.Max(from, Start));
        var end = Times.Round(Math.Min(to, End));
        var length = Math.Max(0, end - start);
        return this with { Start = start, Length = length, Transition = KeptFade(Transition, start <= Start + Times.Epsilon, length) };
    }

    public override TimelineItem Lengthen(ItemEdge edge, double more) => edge == ItemEdge.Start
        ? this with { Start = Times.Round(Start - more), Length = Times.Round(Length + more) }
        : this with { Length = Times.Round(Length + more) };
}
