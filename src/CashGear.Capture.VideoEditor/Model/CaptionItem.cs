namespace CashGear.Capture.VideoEditor.Model;

public enum CaptionPosition
{
    Bottom,
    Top,
}

/// <summary>
/// How a caption track's captions look when burned in: font, size in canvas pixels (0: a size that suits the
/// canvas), colours as <c>#RRGGBB</c> / <c>#AARRGGBB</c>, a box behind each line (null: none), and where they go
/// (<see cref="Margin"/> from the edge and <see cref="MaxWidth"/> as parts of the canvas).
/// </summary>
public sealed record CaptionStyle(
    string FontFamily = "Segoe UI",
    double FontSize = 0,
    string Color = "#FFFFFF",
    string? Box = CaptionStyle.DefaultBox,
    bool Bold = false,
    CaptionPosition Position = CaptionPosition.Bottom,
    double Margin = 0.06,
    double MaxWidth = 0.8)
{
    public const string DefaultBox = "#B4141414";

    /// <summary>The font size used on a canvas <paramref name="canvasHeight"/> pixels high.</summary>
    public double SizeFor(int canvasHeight) => FontSize > 0 ? FontSize : Math.Max(12, Math.Round(canvasHeight / 18.0));
}

/// <summary>A word of a caption with its time, in seconds from the caption's start (from speech recognition).</summary>
public sealed record CaptionWord(double Start, double End, string Text);

/// <summary>
/// A caption: <see cref="Text"/> shown for <see cref="Length"/> seconds on a caption track. Captions made from
/// speech keep their <see cref="Words"/> with their times, so splitting one (or cutting the timeline through it)
/// shares its words out by time; a caption written or edited by hand has none, and keeps its whole text.
/// </summary>
public sealed record CaptionItem : TimelineItem
{
    public const double DefaultLength = 3;

    public string Text { get; init; } = "";

    public double Length { get; init; } = DefaultLength;

    public IReadOnlyList<CaptionWord>? Words { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public override double Duration => Length;

    /// <summary>The text of words, as recognized pieces joined (each has its own leading space).</summary>
    public static string Join(IEnumerable<CaptionWord> words) => string.Concat(words.Select(w => w.Text)).Trim();

    public override TimelineItem Slice(double from, double to)
    {
        from = Times.Round(Math.Max(from, Start));
        to = Times.Round(Math.Min(to, End));
        var length = Math.Max(0, to - from);
        var shift = from - Start;
        if (Words is null || (shift <= Times.Epsilon && length >= Length - Times.Epsilon))
        {
            return this with { Start = from, Length = length };
        }

        // The words said inside the slice (by their middle), moved with it.
        var kept = Words.Where(w => (w.Start + w.End) / 2 >= shift - Times.Epsilon && (w.Start + w.End) / 2 < shift + length - Times.Epsilon)
            .Select(w => w with { Start = Times.Round(Math.Max(0, w.Start - shift)), End = Times.Round(Math.Min(length, w.End - shift)) })
            .ToList();
        return this with { Start = from, Length = length, Words = kept, Text = Join(kept) };
    }

    public override TimelineItem Lengthen(ItemEdge edge, double more) => edge == ItemEdge.Start
        ? this with { Start = Times.Round(Start - more), Length = Times.Round(Length + more), Words = Words?.Select(w => w with { Start = Times.Round(w.Start + more), End = Times.Round(w.End + more) }).ToList() }
        : this with { Length = Times.Round(Length + more) };
}
