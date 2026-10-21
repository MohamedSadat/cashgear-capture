using System.Text.Json.Serialization;

namespace CashGear.Capture.VideoEditor.Model;

/// <summary>New ids for items, tracks, assets and link groups: short, random, unique within any project.</summary>
public static class Ids
{
    public static string New() => Guid.NewGuid().ToString("N")[..12];
}

/// <summary>Times in a project are rounded to the microsecond, so edits don't pile up rounding noise.</summary>
public static class Times
{
    /// <summary>Two times this close are the same time.</summary>
    public const double Epsilon = 1e-6;

    public static double Round(double seconds) => Math.Round(seconds, 6);

    /// <summary>The start of the frame <paramref name="seconds"/> falls in, at <paramref name="fps"/>.</summary>
    public static double FrameStart(double seconds, int fps) => Round(Math.Floor((seconds * fps) + Epsilon) / fps);

    /// <summary>The nearest frame boundary.</summary>
    public static double NearestFrame(double seconds, int fps) => Round(Math.Round(seconds * fps) / fps);
}

/// <summary>One end of a timeline item.</summary>
public enum ItemEdge
{
    Start,
    End,
}

/// <summary>Something on a track for a while: a clip, an annotation, an effect.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Clip), "clip")]
[JsonDerivedType(typeof(AnnotationItem), "annotation")]
[JsonDerivedType(typeof(EffectItem), "effect")]
[JsonDerivedType(typeof(ZoomItem), "zoom")]
[JsonDerivedType(typeof(CaptionItem), "caption")]
public abstract record TimelineItem
{
    public required string Id { get; init; }

    /// <summary>When it starts on the timeline, in seconds.</summary>
    public double Start { get; init; }

    [JsonIgnore]
    public abstract double Duration { get; }

    [JsonIgnore]
    public double End => Start + Duration;

    /// <summary>Whether it is shown at <paramref name="time"/> (from its start, up to but not including its end).</summary>
    public bool Covers(double time) => time >= Start - Times.Epsilon && time < End - Times.Epsilon;

    /// <summary>Whether it is on the timeline at some time inside (<paramref name="from"/>, <paramref name="to"/>).</summary>
    public bool Overlaps(double from, double to) => Start < to - Times.Epsilon && End > from + Times.Epsilon;

    /// <summary>
    /// The part of it from timeline time <paramref name="from"/> to <paramref name="to"/> (clamped to it), starting at
    /// <paramref name="from"/>, with the same id. Fades stay only on the ends that are its own.
    /// </summary>
    public abstract TimelineItem Slice(double from, double to);

    /// <summary>
    /// A copy <paramref name="more"/> seconds longer at one edge (moved earlier for the start). Clips are made longer
    /// by showing more of their source, which the timeline's trimming does itself.
    /// </summary>
    public virtual TimelineItem Lengthen(ItemEdge edge, double more) => this;

    /// <summary>A fade kept on a slice: only when the slice keeps that end, and never longer than the slice.</summary>
    protected static double KeptFade(double fade, bool keptEnd, double length) => keptEnd ? Math.Min(fade, length) : 0;
}

public enum PlacementShape
{
    Rectangle,
    Rounded,
    Circle,
}

/// <summary>
/// Where a clip's pictures go on the canvas. A <see cref="Size"/> of 1 is as large as fits the canvas, keeping the
/// picture's shape (a circle is a square cut from its middle); the picture is centred on (<see cref="CenterX"/>,
/// <see cref="CenterY"/>), fractions of the canvas. The border is in canvas pixels.
/// </summary>
public sealed record Placement(
    double CenterX = 0.5,
    double CenterY = 0.5,
    double Size = 1,
    PlacementShape Shape = PlacementShape.Rectangle,
    string? Border = null,
    double BorderWidth = 0,
    bool Mirror = false,
    double Opacity = 1)
{
    /// <summary>The whole canvas (letterboxed when the shapes differ).</summary>
    public static Placement Fit { get; } = new();

    /// <summary>The rectangle the picture fills on the canvas (x, y, width, height in canvas pixels).</summary>
    public (double X, double Y, double Width, double Height) Area(double canvasWidth, double canvasHeight, double pictureWidth, double pictureHeight)
    {
        var aspect = Shape == PlacementShape.Circle || pictureWidth <= 0 || pictureHeight <= 0 ? 1 : pictureWidth / pictureHeight;
        var width = canvasWidth;
        var height = width / aspect;
        if (height > canvasHeight)
        {
            height = canvasHeight;
            width = height * aspect;
        }

        width *= Size;
        height *= Size;
        return ((CenterX * canvasWidth) - (width / 2), (CenterY * canvasHeight) - (height / 2), width, height);
    }
}

/// <summary>
/// A part of a media stream on the timeline: from <see cref="SourceIn"/> to <see cref="SourceOut"/> (seconds on the
/// stream's clock), starting at <see cref="TimelineItem.Start"/>, played at <see cref="Speed"/> (changing evenly to
/// <see cref="EndSpeed"/> when it ramps), or a still of the picture at <see cref="SourceIn"/> for
/// <see cref="FreezeLength"/> seconds. Clips with the same <see cref="Link"/> (a recording's screen, webcam and sound)
/// are split, moved and deleted together.
/// </summary>
public sealed record Clip : TimelineItem
{
    public const double SlowestSpeed = 0.25;
    public const double FastestSpeed = 8;

    public required string AssetId { get; init; }

    /// <summary>The stream among all the file's streams.</summary>
    public int Stream { get; init; }

    public double SourceIn { get; init; }

    public double SourceOut { get; init; }

    public double Speed { get; init; } = 1;

    /// <summary>The speed at the clip's end when it ramps (null: <see cref="Speed"/> all along).</summary>
    public double? EndSpeed { get; init; }

    /// <summary>More than 0: the clip is a still of the picture at <see cref="SourceIn"/>, this long.</summary>
    public double FreezeLength { get; init; }

    /// <summary>Sound level: 1 as recorded, up to 2.</summary>
    public double Volume { get; init; } = 1;

    /// <summary>Seconds over which the clip fades in from black (and silence) at its start.</summary>
    public double FadeIn { get; init; }

    public double FadeOut { get; init; }

    /// <summary>Where its pictures go (null: the whole canvas).</summary>
    public Placement? Placement { get; init; }

    public string? Link { get; init; }

    [JsonIgnore]
    public bool IsFreeze => FreezeLength > 0;

    [JsonIgnore]
    public bool IsRamp => EndSpeed is { } end && Math.Abs(end - Speed) > 1e-9;

    /// <summary>How much of the source it plays.</summary>
    [JsonIgnore]
    public double Length => SourceOut - SourceIn;

    [JsonIgnore]
    public override double Duration => IsFreeze ? FreezeLength : IsRamp ? 2 * Length / (Speed + EndSpeed!.Value) : Length / Speed;

    public override TimelineItem Slice(double from, double to)
    {
        from = Times.Round(Math.Max(from, Start));
        to = Times.Round(Math.Min(to, End));
        var length = Math.Max(0, to - from);
        var keepsStart = from <= Start + Times.Epsilon;
        var keepsEnd = to >= End - Times.Epsilon;
        var fades = this with { FadeIn = KeptFade(FadeIn, keepsStart, length), FadeOut = KeptFade(FadeOut, keepsEnd, length) };
        if (IsFreeze)
        {
            return fades with { Start = from, FreezeLength = length };
        }

        var slice = fades with
        {
            Start = from,
            SourceIn = keepsStart ? SourceIn : Times.Round(SourceAt(from)),
            SourceOut = keepsEnd ? SourceOut : Times.Round(SourceAt(to)),
        };
        return IsRamp ? slice with { Speed = SpeedAt(from), EndSpeed = SpeedAt(to) } : slice;
    }

    /// <summary>The speed at timeline time <paramref name="time"/>.</summary>
    public double SpeedAt(double time)
    {
        if (IsFreeze)
        {
            return 0;
        }

        if (!IsRamp)
        {
            return Speed;
        }

        var into = Math.Clamp(time - Start, 0, Duration);
        return Speed + ((EndSpeed!.Value - Speed) * into / Duration);
    }

    /// <summary>The source time shown at timeline time <paramref name="time"/> (held at the clip's ends outside it).</summary>
    public double SourceAt(double time)
    {
        if (IsFreeze)
        {
            return SourceIn;
        }

        var into = Math.Clamp(time - Start, 0, Duration);
        if (!IsRamp)
        {
            return SourceIn + (into * Speed);
        }

        var change = EndSpeed!.Value - Speed;
        return SourceIn + (Speed * into) + (change * into * into / (2 * Duration));
    }

    /// <summary>The timeline time at which source time <paramref name="source"/> is shown.</summary>
    public double TimelineAt(double source)
    {
        if (IsFreeze)
        {
            return Start;
        }

        var into = Math.Clamp(source - SourceIn, 0, Length);
        if (!IsRamp)
        {
            return Start + (into / Speed);
        }

        // Solve s0 u + (s1 - s0) u² / (2D) = into for u.
        var a = (EndSpeed!.Value - Speed) / (2 * Duration);
        var u = (-Speed + Math.Sqrt((Speed * Speed) + (4 * a * into))) / (2 * a);
        return Start + u;
    }
}
