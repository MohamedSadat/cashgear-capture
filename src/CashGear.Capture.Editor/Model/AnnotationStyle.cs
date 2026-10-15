using SkiaSharp;

namespace CashGear.Capture.Editor.Model;

/// <summary>Identifies an annotation across edits: annotations are immutable records that get replaced, so identity can't be the reference.</summary>
public readonly record struct AnnotationId(int Value)
{
    public override string ToString() => "#" + Value;
}

/// <summary>Which parts of <see cref="AnnotationStyle"/> an annotation kind uses (and the properties panel shows).</summary>
[Flags]
public enum StyleParts
{
    None = 0,
    Color = 1,
    StrokeWidth = 2,
    Fill = 4,
    Opacity = 8,

    /// <summary>Font family and bold.</summary>
    Font = 16,

    /// <summary>Text height, or the size of a step number.</summary>
    FontSize = 32,

    /// <summary>Blur radius or pixel-cell size.</summary>
    Strength = 64,
}

/// <summary>
/// How an annotation looks, in image pixels. Each kind uses the parts in <see cref="Annotation.StyleParts"/>; the
/// others ride along unchanged, so a style can move between tools without losing anything.
/// </summary>
public sealed record AnnotationStyle
{
    public const float MinStrokeWidth = 1;
    public const float MaxStrokeWidth = 40;
    public const float MinFontSize = 8;
    public const float MaxFontSize = 200;
    public const float MinStrength = 2;
    public const float MaxStrength = 60;
    public const string DefaultFontFamily = "Segoe UI";

    /// <summary>Line, outline and text colour; the highlighter colour; the fill of a step number.</summary>
    public SKColor Color { get; init; } = EditorColors.Red;

    public float StrokeWidth { get; init; } = 4;

    /// <summary>Shape fill or text background. Fully transparent means none.</summary>
    public SKColor Fill { get; init; } = SKColors.Transparent;

    /// <summary>0–1, applied to the annotation as a whole.</summary>
    public float Opacity { get; init; } = 1;

    public string FontFamily { get; init; } = DefaultFontFamily;

    /// <summary>Text height in image pixels; also sets the size of step numbers.</summary>
    public float FontSize { get; init; } = 24;

    public bool Bold { get; init; }

    /// <summary>Blur radius (Gaussian sigma) or pixel-cell size, in image pixels.</summary>
    public float Strength { get; init; } = 10;

    public bool HasFill => Fill.Alpha > 0;

    /// <summary>The same look on an image scaled by <paramref name="factor"/> (sizes scale; colours don't).</summary>
    public AnnotationStyle Scaled(float factor) => factor == 1
        ? this
        : this with { StrokeWidth = StrokeWidth * factor, FontSize = FontSize * factor, Strength = Strength * factor };

    /// <summary>Values clamped to their ranges (for anything typed in or loaded).</summary>
    public AnnotationStyle Normalize() => this with
    {
        StrokeWidth = Math.Clamp(StrokeWidth, MinStrokeWidth, MaxStrokeWidth),
        Opacity = Math.Clamp(Opacity, 0.05f, 1),
        FontFamily = string.IsNullOrWhiteSpace(FontFamily) ? DefaultFontFamily : FontFamily,
        FontSize = Math.Clamp(FontSize, MinFontSize, MaxFontSize),
        Strength = Math.Clamp(Strength, MinStrength, MaxStrength),
    };
}

/// <summary>The editor's colour palette.</summary>
public static class EditorColors
{
    public static readonly SKColor Red = new(0xE8, 0x11, 0x23);
    public static readonly SKColor Highlighter = new(0xFF, 0xF1, 0x00);
    public static readonly SKColor Accent = new(0x00, 0x78, 0xD4);

    public static IReadOnlyList<SKColor> Palette { get; } =
    [
        Red,
        new(0xFF, 0x8C, 0x00), // orange
        Highlighter,
        new(0x16, 0xC6, 0x0C), // green
        new(0x00, 0xB7, 0xC3), // teal
        Accent, // blue
        new(0x88, 0x6C, 0xE4), // purple
        new(0xE3, 0x00, 0x8C), // pink
        SKColors.Black,
        new(0x4C, 0x4C, 0x4C), // dark grey
        new(0xA0, 0xA0, 0xA0), // grey
        SKColors.White,
    ];

    /// <summary>"#RRGGBB" (alpha is not shown; the editor uses opaque colours and a separate opacity).</summary>
    public static string ToHex(SKColor color) => $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}";

    /// <summary>Text that is readable on <paramref name="background"/>: black on light colours, white otherwise.</summary>
    public static SKColor ContrastingText(SKColor background) =>
        (0.299 * background.Red) + (0.587 * background.Green) + (0.114 * background.Blue) > 160 ? SKColors.Black : SKColors.White;
}
