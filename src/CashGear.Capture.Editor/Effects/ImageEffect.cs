using System.Text.Json.Serialization;
using SkiaSharp;

namespace CashGear.Capture.Editor.Effects;

/// <summary>Sides of an image (torn and faded edges).</summary>
[Flags]
public enum Edges
{
    None = 0,
    Top = 1,
    Right = 2,
    Bottom = 4,
    Left = 8,
    All = Top | Right | Bottom | Left,
}

/// <summary>A place on a 3 × 3 grid: where a watermark goes, or where the image sits on a new canvas.</summary>
public enum Anchor
{
    TopLeft,
    Top,
    TopRight,
    Left,
    Center,
    Right,
    BottomLeft,
    Bottom,
    BottomRight,
}

/// <summary>An effect's new image, and where the old image's top-left corner is in it (the margin a shadow or border added).</summary>
public readonly record struct EffectOutput(SKImage Image, SKPointI Offset);

/// <summary>
/// A change to a whole image (or to the selected part of one): a shadow, an edge, a colour adjustment, a watermark.
/// Effects are immutable settings, so a list of them is a preset that can be saved and applied again. The same
/// <see cref="Apply"/> makes the editor's preview, the committed edit and the batch processor's output, which is
/// why they are identical.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ShadowEffect), "shadow")]
[JsonDerivedType(typeof(BorderEffect), "border")]
[JsonDerivedType(typeof(TornEdgeEffect), "tornEdge")]
[JsonDerivedType(typeof(FadeEdgeEffect), "fadeEdge")]
[JsonDerivedType(typeof(RoundedCornersEffect), "roundedCorners")]
[JsonDerivedType(typeof(PerspectiveEffect), "perspective")]
[JsonDerivedType(typeof(AdjustEffect), "adjust")]
[JsonDerivedType(typeof(GrayscaleEffect), "grayscale")]
[JsonDerivedType(typeof(SharpenEffect), "sharpen")]
[JsonDerivedType(typeof(WatermarkEffect), "watermark")]
public abstract record ImageEffect
{
    /// <summary>Lower-case name for undo ("Undo drop shadow") and lists.</summary>
    [JsonIgnore]
    public abstract string Name { get; }

    /// <summary>
    /// The effect only adds around the image (a shadow, a border) and moves it by <see cref="EffectOutput.Offset"/>,
    /// so annotations can stay editable objects. Every other effect changes what the annotations are drawn over, so
    /// they are merged into the image first.
    /// </summary>
    [JsonIgnore]
    public virtual bool KeepsAnnotations => false;

    /// <summary>A new image with the effect (never <paramref name="source"/> itself; the caller owns it).</summary>
    public abstract EffectOutput Apply(SKImage source);

    /// <summary>The same effect with every value in its range.</summary>
    public abstract ImageEffect Normalize();

    protected static readonly SKSamplingOptions Exact = new(SKFilterMode.Nearest);
}
