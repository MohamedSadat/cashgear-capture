using CashGear.Capture.Editor.Model;
using CashGear.Capture.Editor.Rendering;
using SkiaSharp;

namespace CashGear.Capture.VideoEditor.Rendering;

/// <summary>A speech bubble: a rounded box with a tail to a point, and a text in the box (shaped and ordered as all text is).</summary>
internal sealed record SpeechBubbleAnnotation(AnnotationId Id, AnnotationStyle Style, SKRect Rect, SKPoint Tail, string Text) : Annotation(Id, Style)
{
    public override AnnotationKind Kind => AnnotationKind.Text;

    public override StyleParts StyleParts => StyleParts.Color | StyleParts.Fill | StyleParts.Opacity | StyleParts.Font | StyleParts.FontSize;

    public override SKRect Bounds
    {
        get
        {
            var box = Rect;
            box.Left = Math.Min(box.Left, Tail.X);
            box.Top = Math.Min(box.Top, Tail.Y);
            box.Right = Math.Max(box.Right, Tail.X);
            box.Bottom = Math.Max(box.Bottom, Tail.Y);
            return box;
        }
    }

    public override bool HitTest(SKPoint point, float tolerance) => SKRect.Inflate(Rect, tolerance, tolerance).Contains(point);

    public override Annotation Offset(float dx, float dy) => this with { Rect = SKRect.Create(Rect.Left + dx, Rect.Top + dy, Rect.Width, Rect.Height), Tail = new SKPoint(Tail.X + dx, Tail.Y + dy) };

    public override Annotation Transform(SKMatrix matrix, float scale) => this with { Rect = matrix.MapRect(Rect), Tail = matrix.MapPoint(Tail), Style = Style.Scaled(scale) };

    protected override void RenderCore(SKCanvas canvas, RenderContext context)
    {
        var radius = Math.Min(Rect.Width, Rect.Height) * 0.2f;
        using var builder = new SKPathBuilder();
        builder.AddRoundRect(new SKRoundRect(Rect, radius, radius));

        // The tail leaves the box's nearest long side, a little to each side of the point nearest it.
        var along = Math.Clamp(Tail.X, Rect.Left + radius + 8, Rect.Right - radius - 8);
        var width = Math.Min(Rect.Width * 0.08f, 24);
        var onBottom = Tail.Y > Rect.MidY;
        var edge = onBottom ? Rect.Bottom : Rect.Top;
        builder.MoveTo(along - width, edge);
        builder.LineTo(Tail);
        builder.LineTo(along + width, edge);
        builder.Close();
        using var path = builder.Detach();
        using (var fill = FillPaint(Style.Fill))
        {
            canvas.DrawPath(path, fill);
        }

        var layout = TextLayout.Measure(Text, Style with { Opacity = 1 });
        var place = new SKPoint(Rect.MidX - (layout.Size.Width / 2), Rect.MidY - (layout.Size.Height / 2));
        new TextAnnotation(Id, Style with { Fill = SKColors.Transparent, Opacity = 1 }, place, Text).Render(canvas, context);
    }
}
