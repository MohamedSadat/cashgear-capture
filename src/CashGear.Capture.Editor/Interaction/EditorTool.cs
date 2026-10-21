using CashGear.Capture.Editor.Model;
using SkiaSharp;

namespace CashGear.Capture.Editor.Interaction;

public enum EditorTool
{
    Select,
    Arrow,
    Line,
    Rectangle,
    Ellipse,
    Freehand,
    Highlight,
    Text,
    StepNumber,
    Blur,
    Pixelate,
    Crop,

    /// <summary>Selects a part of the image for effects (no annotation).</summary>
    Area,
}

[Flags]
public enum PointerModifiers
{
    None = 0,
    Shift = 1,
    Control = 2,
}

/// <summary>The mouse cursor the view should show; mapped to a platform cursor by the host.</summary>
public enum EditorCursor
{
    Arrow,
    Crosshair,
    Move,
    SizeNWSE,
    SizeNESW,
    SizeNS,
    SizeWE,
    IBeam,
}

/// <summary>Asks the host to show a text box for typing, over the image at <see cref="Position"/>.</summary>
/// <param name="Existing">The annotation being edited, or null for new text.</param>
public sealed record TextEditRequest(AnnotationId? Existing, SKPoint Position, string Text, AnnotationStyle Style);

public static class EditorTools
{
    /// <summary>The kind of annotation a drawing tool makes (null for Select and Crop).</summary>
    public static AnnotationKind? Creates(this EditorTool tool) => tool switch
    {
        EditorTool.Arrow => AnnotationKind.Arrow,
        EditorTool.Line => AnnotationKind.Line,
        EditorTool.Rectangle => AnnotationKind.Rectangle,
        EditorTool.Ellipse => AnnotationKind.Ellipse,
        EditorTool.Freehand => AnnotationKind.Freehand,
        EditorTool.Highlight => AnnotationKind.Highlight,
        EditorTool.Text => AnnotationKind.Text,
        EditorTool.StepNumber => AnnotationKind.StepNumber,
        EditorTool.Blur => AnnotationKind.Blur,
        EditorTool.Pixelate => AnnotationKind.Pixelate,
        _ => null,
    };

    /// <summary>The tool that makes <paramref name="kind"/>.</summary>
    public static EditorTool ToolFor(AnnotationKind kind) => kind switch
    {
        AnnotationKind.Arrow => EditorTool.Arrow,
        AnnotationKind.Line => EditorTool.Line,
        AnnotationKind.Rectangle => EditorTool.Rectangle,
        AnnotationKind.Ellipse => EditorTool.Ellipse,
        AnnotationKind.Freehand => EditorTool.Freehand,
        AnnotationKind.Highlight => EditorTool.Highlight,
        AnnotationKind.Text => EditorTool.Text,
        AnnotationKind.StepNumber => EditorTool.StepNumber,
        AnnotationKind.Blur => EditorTool.Blur,
        AnnotationKind.Pixelate => EditorTool.Pixelate,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>The style parts a tool's annotations use (nothing for Select and Crop).</summary>
    public static StyleParts Parts(this EditorTool tool) => tool switch
    {
        EditorTool.Arrow or EditorTool.Line or EditorTool.Freehand => StyleParts.Color | StyleParts.StrokeWidth | StyleParts.Opacity,
        EditorTool.Rectangle or EditorTool.Ellipse => StyleParts.Color | StyleParts.StrokeWidth | StyleParts.Fill | StyleParts.Opacity,
        EditorTool.Highlight => StyleParts.Color | StyleParts.Opacity,
        EditorTool.Text => StyleParts.Color | StyleParts.Fill | StyleParts.Opacity | StyleParts.Font | StyleParts.FontSize,
        EditorTool.StepNumber => StyleParts.Color | StyleParts.Opacity | StyleParts.FontSize,
        EditorTool.Blur or EditorTool.Pixelate => StyleParts.Strength,
        _ => StyleParts.None,
    };

    /// <summary>A sensible first style for each tool (red marks, yellow highlighter, strong enough blur to hide text).</summary>
    public static AnnotationStyle DefaultStyle(this EditorTool tool) => tool switch
    {
        EditorTool.Highlight => new AnnotationStyle { Color = EditorColors.Highlighter },
        EditorTool.Text => new AnnotationStyle { FontSize = 24 },
        EditorTool.StepNumber => new AnnotationStyle { FontSize = 28 },
        EditorTool.Blur => new AnnotationStyle { Strength = 8 },
        EditorTool.Pixelate => new AnnotationStyle { Strength = 12 },
        _ => new AnnotationStyle(),
    };
}

/// <summary>The last style used with each tool, so the next arrow looks like the previous one. Shared by all editor windows.</summary>
public sealed class ToolStyleMemory
{
    private readonly Dictionary<EditorTool, AnnotationStyle> _styles = [];

    public AnnotationStyle Get(EditorTool tool) => _styles.TryGetValue(tool, out var style) ? style : tool.DefaultStyle();

    public void Set(EditorTool tool, AnnotationStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        _styles[tool] = style.Normalize();
    }
}
