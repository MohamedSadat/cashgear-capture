using System.ComponentModel;
using System.Text.Json.Serialization;

namespace CashGear.Capture.VideoEditor.Model;

/// <summary>
/// A brand's colours and fonts, its logo, its intro and outro clips and its look. Templates take their colours and fonts
/// from the project's kit, so changing the kit changes every template that uses it. The files are media of the project.
/// </summary>
public sealed record BrandKit
{
    [Localizable(false)]
    public string Primary { get; init; } = "#0F62FE";

    [Localizable(false)]
    public string Secondary { get; init; } = "#161A23";

    [Localizable(false)]
    public string Accent { get; init; } = "#FFB000";

    [Localizable(false)]
    public string Text { get; init; } = "#FFFFFF";

    [Localizable(false)]
    public string HeadingFont { get; init; } = AnnotationLook.DefaultFontFamily;

    [Localizable(false)]
    public string BodyFont { get; init; } = AnnotationLook.DefaultFontFamily;

    /// <summary>The logo among the project's assets.</summary>
    public string? LogoAssetId { get; init; }

    public string? IntroAssetId { get; init; }

    public string? OutroAssetId { get; init; }

    /// <summary>The look's name or id (<see cref="Editing.Looks"/>), for new recordings and for Use the Brand's Look.</summary>
    public string? Look { get; init; }

    /// <summary>The kit of a project that has none yet.</summary>
    public static BrandKit Default { get; } = new();
}

/// <summary>Which of the brand's colours a part of a template has (None: its own).</summary>
public enum BrandColor
{
    None,
    Primary,
    Secondary,
    Accent,
    Text,
}

/// <summary>Which of the brand's fonts a text has (None: its own).</summary>
public enum BrandFont
{
    None,
    Heading,
    Body,
}

/// <summary>How a text sits on its place across: its start there, its middle there, or its end there.</summary>
public enum TemplateAlign
{
    Start,
    Center,
    End,
}

public enum TemplateKind
{
    Title,
    LowerThird,
    Callout,
    Saved,
}

/// <summary>A text a template asks for, with what it says until it is given.</summary>
public sealed record TemplateField(string Key, string Label, string Default);

/// <summary>
/// A part of a template: an annotation on a 1920 × 1080 canvas, its times from the template's start, with what it takes
/// from the brand and which text it shows.
/// </summary>
/// <param name="Fill">The brand's colour for a rectangle's or a text's background.</param>
/// <param name="Field">Which field it shows.</param>
/// <param name="Stretches">When the template lasts longer or shorter, it does too (its last part stays at the end).</param>
/// <param name="IsLogo">It is the brand's logo: a picture in its box.</param>
/// <param name="AssetName">A picture of its own, a file in the library's assets.</param>
public sealed record TemplateElement(
    AnnotationItem Item,
    BrandColor Color = BrandColor.None,
    BrandColor Fill = BrandColor.None,
    BrandFont Font = BrandFont.None,
    string? Field = null,
    TemplateAlign Align = TemplateAlign.Start,
    bool Stretches = true,
    bool IsLogo = false,
    string? AssetName = null);

/// <summary>A title, lower third or callout: parts drawn in code from a list, so it can be saved, shared and given another brand.</summary>
/// <param name="MirrorsForRightToLeft">Text written right to left puts everything on the other side of the canvas, as it reads.</param>
public sealed record TitleTemplate(
    string Id,
    string Name,
    TemplateKind Kind,
    double Length,
    IReadOnlyList<TemplateElement> Elements,
    IReadOnlyList<TemplateField>? Fields = null,
    bool MirrorsForRightToLeft = false)
{
    /// <summary>The canvas its parts are laid out on.</summary>
    public const double DesignWidth = 1920;

    public const double DesignHeight = 1080;
}

/// <summary>A template on an annotation track: a copy of it (so the project needs nothing else), with what its fields say.</summary>
public sealed record TemplateItem : TimelineItem
{
    public required TitleTemplate Template { get; init; }

    /// <summary>How long the template runs, however much of it is on the timeline.</summary>
    public double FullLength { get; init; }

    /// <summary>What part of it is on the timeline: how long.</summary>
    public double Length { get; init; }

    /// <summary>What part of it is on the timeline: from how far into it (a cut keeps the rest where it was).</summary>
    public double Offset { get; init; }

    /// <summary>What each field says, by its key.</summary>
    public IReadOnlyDictionary<string, string>? Values { get; init; }

    /// <summary>How far it is moved across and down from where it was designed, as parts of the canvas.</summary>
    public double MoveX { get; init; }

    public double MoveY { get; init; }

    /// <summary>A picture's asset id for each of its elements' files, by file name.</summary>
    public IReadOnlyDictionary<string, string>? AssetIds { get; init; }

    [JsonIgnore]
    public override double Duration => Length;

    /// <summary>What a field says.</summary>
    public string Text(TemplateField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return Values is not null && Values.TryGetValue(field.Key, out var value) ? value : field.Default;
    }

    public override TimelineItem Slice(double from, double to)
    {
        from = Times.Round(Math.Max(from, Start));
        to = Times.Round(Math.Min(to, End));
        return this with { Start = from, Length = Math.Max(0, to - from), Offset = Times.Round(Offset + (from - Start)) };
    }

    public override TimelineItem Lengthen(ItemEdge edge, double more) => edge == ItemEdge.Start
        ? this with { Start = Times.Round(Start - more), Length = Times.Round(Length + more), Offset = Times.Round(Math.Max(0, Offset - more)), FullLength = Times.Round(FullLength + Math.Max(0, more - Offset)) }
        : this with { Length = Times.Round(Length + more), FullLength = Times.Round(Math.Max(FullLength, Offset + Length + more)) };
}

/// <summary>A place in the project with a name, at a time on the timeline.</summary>
public sealed record TimelineMarker(string Id, double Time, string Name);
