using CashGear.Capture.Editor.Rendering;
using SkiaSharp;

namespace CashGear.Capture.Editor.Model;

/// <summary>An annotation defined by two points, with a grip on each.</summary>
public abstract record TwoPointAnnotation(AnnotationId Id, AnnotationStyle Style, SKPoint Start, SKPoint End) : Annotation(Id, Style)
{
    public override StyleParts StyleParts => StyleParts.Color | StyleParts.StrokeWidth | StyleParts.Opacity;

    public float Length => Geometry.Distance(Start, End);

    public override SKRect Bounds => Geometry.Inflate(Geometry.RectFromPoints(Start, End), Reach + 1);

    /// <summary>How far the drawing extends sideways from the line.</summary>
    protected virtual float Reach => Style.StrokeWidth / 2;

    public override bool HitTest(SKPoint point, float tolerance) =>
        Geometry.DistanceToSegment(point, Start, End) <= (Style.StrokeWidth / 2) + tolerance;

    public override IReadOnlyList<Handle> GetHandles() => [new(HandleKind.Start, Start), new(HandleKind.End, End)];

    public override Annotation WithHandleMoved(HandleKind handle, SKPoint position, bool constrain) => handle switch
    {
        HandleKind.Start => this with { Start = constrain ? Geometry.SnapAngle(End, position) : position },
        HandleKind.End => this with { End = constrain ? Geometry.SnapAngle(Start, position) : position },
        _ => this,
    };

    public override Annotation Offset(float dx, float dy) =>
        this with { Start = Geometry.Offset(Start, dx, dy), End = Geometry.Offset(End, dx, dy) };

    public override Annotation Transform(SKMatrix matrix, float scale) =>
        this with { Start = matrix.MapPoint(Start), End = matrix.MapPoint(End), Style = Style.Scaled(scale) };
}

public sealed record LineAnnotation(AnnotationId Id, AnnotationStyle Style, SKPoint Start, SKPoint End)
    : TwoPointAnnotation(Id, Style, Start, End)
{
    public override AnnotationKind Kind => AnnotationKind.Line;

    protected override void RenderCore(SKCanvas canvas, RenderContext context)
    {
        using var paint = StrokePaint();
        canvas.DrawLine(Start, End, paint);
    }
}

/// <summary>A line with a filled head at <see cref="TwoPointAnnotation.End"/>; the head grows with the line width.</summary>
public sealed record ArrowAnnotation(AnnotationId Id, AnnotationStyle Style, SKPoint Start, SKPoint End)
    : TwoPointAnnotation(Id, Style, Start, End)
{
    public override AnnotationKind Kind => AnnotationKind.Arrow;

    protected override float Reach => MathF.Max(Style.StrokeWidth / 2, HeadHalfWidth(Style.StrokeWidth));

    public override bool HitTest(SKPoint point, float tolerance) =>
        base.HitTest(point, tolerance) || Geometry.Distance(point, End) <= HeadHalfWidth(Style.StrokeWidth) + tolerance;

    public static float HeadLength(float strokeWidth) => (3.5f * strokeWidth) + 8;

    public static float HeadHalfWidth(float strokeWidth) => (1.6f * strokeWidth) + 4;

    protected override void RenderCore(SKCanvas canvas, RenderContext context)
    {
        var length = Length;
        if (length < 0.5f)
        {
            return;
        }

        var ux = (End.X - Start.X) / length;
        var uy = (End.Y - Start.Y) / length;

        // Short arrows get a smaller head, so the head never swallows the whole line.
        var head = MathF.Min(HeadLength(Style.StrokeWidth), length * 0.6f);
        var halfWidth = HeadHalfWidth(Style.StrokeWidth) * head / HeadLength(Style.StrokeWidth);
        var headBase = new SKPoint(End.X - (ux * head), End.Y - (uy * head));

        // The shaft stops inside the head, so its round cap never pokes out of the tip.
        var shaftEnd = new SKPoint(headBase.X + (ux * head * 0.3f), headBase.Y + (uy * head * 0.3f));
        using (var shaft = StrokePaint())
        {
            canvas.DrawLine(Start, shaftEnd, shaft);
        }

        using var builder = new SKPathBuilder();
        builder.MoveTo(End);
        builder.LineTo(headBase.X - (uy * halfWidth), headBase.Y + (ux * halfWidth));
        builder.LineTo(headBase.X + (uy * halfWidth), headBase.Y - (ux * halfWidth));
        builder.Close();
        using var path = builder.Detach();
        using var fill = FillPaint(Style.Color);
        canvas.DrawPath(path, fill);
    }
}

/// <summary>A free-hand pen stroke through the points the mouse passed, smoothed with quadratic curves.</summary>
public sealed record FreehandAnnotation(AnnotationId Id, AnnotationStyle Style, IReadOnlyList<SKPoint> Points) : Annotation(Id, Style)
{
    public override AnnotationKind Kind => AnnotationKind.Freehand;

    public override StyleParts StyleParts => StyleParts.Color | StyleParts.StrokeWidth | StyleParts.Opacity;

    /// <summary>The box through the points themselves (the stroke reaches beyond it by half its width).</summary>
    public SKRect PointBounds
    {
        get
        {
            if (Points.Count == 0)
            {
                return SKRect.Empty;
            }

            float left = float.MaxValue, top = float.MaxValue, right = float.MinValue, bottom = float.MinValue;
            foreach (var p in Points)
            {
                left = MathF.Min(left, p.X);
                top = MathF.Min(top, p.Y);
                right = MathF.Max(right, p.X);
                bottom = MathF.Max(bottom, p.Y);
            }

            return new SKRect(left, top, right, bottom);
        }
    }

    public override SKRect Bounds => Geometry.Inflate(PointBounds, (Style.StrokeWidth / 2) + 1);

    public override bool HitTest(SKPoint point, float tolerance)
    {
        var reach = (Style.StrokeWidth / 2) + tolerance;
        if (Points.Count == 1)
        {
            return Geometry.Distance(point, Points[0]) <= reach;
        }

        for (var i = 1; i < Points.Count; i++)
        {
            if (Geometry.DistanceToSegment(point, Points[i - 1], Points[i]) <= reach)
            {
                return true;
            }
        }

        return false;
    }

    public override IReadOnlyList<Handle> GetHandles() => Points.Count > 1 ? Geometry.BoxHandles(PointBounds) : [];

    /// <summary>Stretches the stroke so its point box follows the dragged grip.</summary>
    public override Annotation WithHandleMoved(HandleKind handle, SKPoint position, bool constrain)
    {
        var from = PointBounds;
        var to = Geometry.ResizeBox(from, handle, position, constrain);
        var sx = from.Width > 0 ? to.Width / from.Width : 1;
        var sy = from.Height > 0 ? to.Height / from.Height : 1;
        return this with
        {
            Points = Points.Select(p => new SKPoint(to.Left + ((p.X - from.Left) * sx), to.Top + ((p.Y - from.Top) * sy))).ToArray(),
        };
    }

    public override Annotation Offset(float dx, float dy) =>
        this with { Points = Points.Select(p => Geometry.Offset(p, dx, dy)).ToArray() };

    public override Annotation Transform(SKMatrix matrix, float scale) =>
        this with { Points = Points.Select(matrix.MapPoint).ToArray(), Style = Style.Scaled(scale) };

    protected override void RenderCore(SKCanvas canvas, RenderContext context)
    {
        if (Points.Count == 0)
        {
            return;
        }

        if (Points.Count == 1)
        {
            using var dot = FillPaint(Style.Color);
            canvas.DrawCircle(Points[0], Style.StrokeWidth / 2, dot);
            return;
        }

        using var builder = new SKPathBuilder();
        builder.MoveTo(Points[0]);
        for (var i = 1; i < Points.Count - 1; i++)
        {
            var mid = new SKPoint((Points[i].X + Points[i + 1].X) / 2, (Points[i].Y + Points[i + 1].Y) / 2);
            builder.QuadTo(Points[i], mid);
        }

        builder.LineTo(Points[^1]);
        using var path = builder.Detach();
        using var paint = StrokePaint();
        canvas.DrawPath(path, paint);
    }
}
