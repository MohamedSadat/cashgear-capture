using CashGear.Capture.Editor.Model;
using CashGear.Capture.Editor.Rendering;
using CashGear.Capture.VideoEditor.Model;
using SkiaSharp;

namespace CashGear.Capture.VideoEditor.Rendering;

/// <summary>
/// Burned-in captions: the text wrapped to the style's width (the words in their order; each line is shaped on its
/// own, so Arabic joins and reads right to left in every line), each line drawn as the image editor draws text with
/// its box, centred, stacked from the bottom (or the top). Layouts are kept for the captions drawn lately.
/// </summary>
public sealed class CaptionRenderer
{
    private const int Kept = 64;

    private static readonly SKImage Blank = SKImage.Create(new SKImageInfo(1, 1));
    private static readonly EffectCache NoEffects = new();
    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.None);

    private readonly object _gate = new();
    private readonly Dictionary<(string Text, CaptionStyle Style, int Width, int Height), IReadOnlyList<TextAnnotation>> _layouts = [];
    private readonly Queue<(string, CaptionStyle, int, int)> _order = new();

    /// <summary>The image editor's text style for <paramref name="style"/> on a canvas <paramref name="height"/> pixels high.</summary>
    public static AnnotationStyle TextStyle(CaptionStyle style, int height)
    {
        ArgumentNullException.ThrowIfNull(style);
        return new AnnotationStyle
        {
            Color = FrameCompositor.Color(style.Color, SKColors.White),
            Fill = style.Box is null ? SKColors.Transparent : FrameCompositor.Color(style.Box, SKColors.Transparent),
            FontFamily = style.FontFamily,
            FontSize = (float)style.SizeFor(height),
            Bold = style.Bold,
        }.Normalize();
    }

    /// <summary>The text in lines no wider than the style allows: its own line breaks kept, a word that doesn't fit going to the next line.</summary>
    public static IReadOnlyList<string> Lines(string text, CaptionStyle style, ProjectCanvas canvas)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(canvas);
        var look = TextStyle(style, canvas.Height);
        var widest = canvas.Width * Math.Clamp(style.MaxWidth, 0.1, 1);
        var lines = new List<string>();
        foreach (var paragraph in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = "";
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var longer = line.Length == 0 ? word : $"{line} {word}";
                if (line.Length > 0 && Width(longer, look) > widest)
                {
                    lines.Add(line);
                    line = word;
                }
                else
                {
                    line = longer;
                }
            }

            if (line.Length > 0)
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    /// <summary>The lines of <paramref name="text"/> as placed text annotations on the canvas (canvas pixels).</summary>
    public IReadOnlyList<TextAnnotation> Layout(string text, CaptionStyle style, ProjectCanvas canvas)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(canvas);
        var key = (text, style, canvas.Width, canvas.Height);
        lock (_gate)
        {
            if (_layouts.TryGetValue(key, out var kept))
            {
                return kept;
            }
        }

        var look = TextStyle(style, canvas.Height);
        var lines = Lines(text, style, canvas).Select(l => new TextAnnotation(new AnnotationId(0), look, SKPoint.Empty, l)).ToList();
        var sizes = lines.Select(l => l.Layout.Size).ToList();
        var margin = (float)(canvas.Height * Math.Clamp(style.Margin, 0, 0.5));
        var y = style.Position == CaptionPosition.Top ? margin : canvas.Height - margin - sizes.Sum(s => s.Height);
        var placed = new List<TextAnnotation>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            placed.Add(lines[i] with { Position = new SKPoint(MathF.Round((canvas.Width - sizes[i].Width) / 2), MathF.Round(y)) });
            y += sizes[i].Height;
        }

        lock (_gate)
        {
            if (_layouts.TryAdd(key, placed))
            {
                _order.Enqueue(key);
                if (_order.Count > Kept)
                {
                    _layouts.Remove(_order.Dequeue());
                }
            }
        }

        return placed;
    }

    /// <summary>Draws <paramref name="caption"/> in <paramref name="style"/> on a canvas of <paramref name="size"/>.</summary>
    public void Draw(SKCanvas canvas, CaptionItem caption, CaptionStyle style, ProjectCanvas size)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(caption);
        foreach (var line in Layout(caption.Text, style, size))
        {
            line.Render(canvas, new RenderContext(Blank, NoEffects, Sampling));
        }
    }

    /// <summary>A line's width without the box's padding.</summary>
    private static float Width(string text, AnnotationStyle style)
    {
        var layout = TextLayout.Measure(text, style);
        return layout.Size.Width - (2 * layout.Padding);
    }
}
