using CashGear.Capture.Editor.Rendering;
using CashGear.Capture.Editor.Text;
using SkiaSharp;

namespace CashGear.Capture.Editor.Model;

/// <summary>
/// Where the lines of a <see cref="TextAnnotation"/> go, relative to its top-left corner. Lines are shaped (Arabic
/// letters joined, right-to-left runs in order); text whose first letter is right to left is right-aligned.
/// </summary>
public sealed record TextLayout(IReadOnlyList<string> Lines, float Padding, float LineHeight, float Ascent, SKSize Size, IReadOnlyList<ShapedLine> Shaped, bool RightToLeft)
{
    /// <summary>Measures <paramref name="text"/> in <paramref name="style"/>'s font. Blank lines keep their height.</summary>
    public static TextLayout Measure(string text, AnnotationStyle style)
    {
        var lines = SplitLines(text);
        using var typeface = Fonts.CreateTypeface(style);
        using var font = Fonts.CreateFont(typeface, style.FontSize);
        var padding = PaddingFor(style);
        var rightToLeft = Bidi.IsRightToLeft(text);
        var shaped = lines.Select(line => TextShaper.Shape(line, typeface, style.FontSize, rightToLeft)).ToList();
        var width = shaped.Select(line => line.Width).DefaultIfEmpty(0).Max();
        width = MathF.Max(width, style.FontSize * 0.5f); // an empty box is still visible and clickable
        var lineHeight = font.Spacing;
        var size = new SKSize(MathF.Ceiling(width + (2 * padding)), MathF.Ceiling((lines.Count * lineHeight) + (2 * padding)));
        return new TextLayout(lines, padding, lineHeight, -font.Metrics.Ascent, size, shaped, rightToLeft);
    }

    /// <summary>Where line <paramref name="index"/> starts, from the box's left edge: the padding, or right-aligned.</summary>
    public float LineStart(int index) => RightToLeft ? Size.Width - Padding - Shaped[index].Width : Padding;

    /// <summary>Space between the text and the edge of its (optional) background.</summary>
    public static float PaddingFor(AnnotationStyle style) => MathF.Round(MathF.Max(2, style.FontSize * 0.2f));

    public static IReadOnlyList<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
}

/// <summary>One or more lines of text at <see cref="Position"/> (the top-left of its box), with an optional background.</summary>
public sealed record TextAnnotation(AnnotationId Id, AnnotationStyle Style, SKPoint Position, string Text) : Annotation(Id, Style)
{
    public override AnnotationKind Kind => AnnotationKind.Text;

    public override StyleParts StyleParts =>
        StyleParts.Color | StyleParts.Fill | StyleParts.Opacity | StyleParts.Font | StyleParts.FontSize;

    public TextLayout Layout => TextLayout.Measure(Text, Style);

    public override SKRect Bounds => SKRect.Create(Position, Layout.Size);

    public override bool HitTest(SKPoint point, float tolerance) => Geometry.Inflate(Bounds, tolerance).Contains(point);

    public override Annotation Offset(float dx, float dy) => this with { Position = Geometry.Offset(Position, dx, dy) };

    /// <summary>The text box moves with the image (and stays upright when the image is rotated).</summary>
    public override Annotation Transform(SKMatrix matrix, float scale)
    {
        var box = Geometry.MapRect(matrix, Bounds);
        return this with { Position = new SKPoint(box.Left, box.Top), Style = Style.Scaled(scale) };
    }

    protected override void RenderCore(SKCanvas canvas, RenderContext context)
    {
        var layout = Layout;
        if (Style.HasFill)
        {
            using var background = FillPaint(Style.Fill);
            canvas.DrawRect(SKRect.Create(Position, layout.Size), background);
        }

        using var typeface = Fonts.CreateTypeface(Style);
        using var font = Fonts.CreateFont(typeface, Style.FontSize);
        using var paint = FillPaint(Style.Color);
        var y = Position.Y + layout.Padding + layout.Ascent;
        for (var i = 0; i < layout.Shaped.Count; i++)
        {
            layout.Shaped[i].Draw(canvas, Position.X + layout.LineStart(i), y, font, paint);
            y += layout.LineHeight;
        }
    }
}

/// <summary>A numbered badge for step-by-step instructions. New ones take the next number (<see cref="Document.EditorDocument.NextStepNumber"/>).</summary>
public sealed record StepNumberAnnotation(AnnotationId Id, AnnotationStyle Style, SKPoint Center, int Number) : Annotation(Id, Style)
{
    public override AnnotationKind Kind => AnnotationKind.StepNumber;

    public override StyleParts StyleParts => StyleParts.Color | StyleParts.Opacity | StyleParts.FontSize;

    public float Radius => Style.FontSize * 0.75f;

    public override SKRect Bounds => Geometry.Inflate(new SKRect(Center.X, Center.Y, Center.X, Center.Y), Radius + 1);

    public override bool HitTest(SKPoint point, float tolerance) => Geometry.Distance(point, Center) <= Radius + tolerance;

    public override Annotation Offset(float dx, float dy) => this with { Center = Geometry.Offset(Center, dx, dy) };

    public override Annotation Transform(SKMatrix matrix, float scale) =>
        this with { Center = matrix.MapPoint(Center), Style = Style.Scaled(scale) };

    protected override void RenderCore(SKCanvas canvas, RenderContext context)
    {
        using (var circle = FillPaint(Style.Color))
        {
            canvas.DrawCircle(Center, Radius, circle);
        }

        var label = Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var size = Radius * (label.Length switch { 1 => 1.15f, 2 => 0.95f, _ => 0.75f });
        using var typeface = SKTypeface.FromFamilyName(AnnotationStyle.DefaultFontFamily, SKFontStyle.Bold);
        using var font = Fonts.CreateFont(typeface, size);
        font.MeasureText(label, out var ink);
        using var text = FillPaint(EditorColors.ContrastingText(Style.Color));
        canvas.DrawText(label, Center.X - ink.MidX, Center.Y - ink.MidY, SKTextAlign.Left, font, text);
    }
}

/// <summary>Typeface and font creation with the editor's settings, shared by measuring and drawing so they agree.</summary>
internal static class Fonts
{
    public static SKTypeface CreateTypeface(AnnotationStyle style) =>
        SKTypeface.FromFamilyName(style.FontFamily, style.Bold ? SKFontStyle.Bold : SKFontStyle.Normal) ?? SKTypeface.CreateDefault();

    public static SKFont CreateFont(SKTypeface typeface, float size) => new(typeface, size)
    {
        Edging = SKFontEdging.Antialias,
        Subpixel = true,
    };
}
