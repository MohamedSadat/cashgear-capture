using CashGear.Capture.Editor.Rendering;
using SkiaSharp;

namespace CashGear.Capture.Editor.Model;

/// <summary>
/// Dims everything outside its rectangle, to draw the eye to it. All spotlights share one dimmed layer (in the
/// newest one's colour and opacity), so several can be lit at once; it goes over blur and pixelate areas and under
/// the other annotations, which stay bright.
/// </summary>
public sealed record SpotlightAnnotation(AnnotationId Id, AnnotationStyle Style, SKRect Rect) : BoxAnnotation(Id, Style, Rect)
{
    public override AnnotationKind Kind => AnnotationKind.Spotlight;

    public override StyleParts StyleParts => StyleParts.Color | StyleParts.Opacity;

    protected override bool UsesOpacityLayer => false;

    /// <summary>Nothing alone: <see cref="DrawAll"/> draws every spotlight's shared layer.</summary>
    protected override void RenderCore(SKCanvas canvas, RenderContext context)
    {
    }

    /// <summary>The dimmed layer over <paramref name="area"/> (the image), with a hole for each spotlight.</summary>
    public static void DrawAll(SKCanvas canvas, IReadOnlyList<SpotlightAnnotation> spotlights, SKRect area)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(spotlights);
        if (spotlights.Count == 0)
        {
            return;
        }

        var style = spotlights[^1].Style;
        canvas.Save();
        foreach (var spotlight in spotlights)
        {
            canvas.ClipRect(spotlight.Rect, SKClipOperation.Difference, antialias: true);
        }

        using var dim = new SKPaint { Color = style.Color.WithAlpha(ToAlpha(style.Opacity)), Style = SKPaintStyle.Fill };
        canvas.DrawRect(area, dim);
        canvas.Restore();
    }
}

/// <summary>
/// A round magnifying callout: the circle of the image around <see cref="Source"/> (<see cref="Radius"/> pixels)
/// shown <see cref="Zoom"/> times larger in a circle at <see cref="Callout"/>, joined by a line. Dragging the
/// callout or the source circle moves that one alone; the grips set the source's size and the zoom.
/// </summary>
public sealed record MagnifyAnnotation(AnnotationId Id, AnnotationStyle Style, SKPoint Source, float Radius, SKPoint Callout) : Annotation(Id, Style)
{
    public const float MinRadius = 4;
    public const float MinZoom = 2;
    public const float MaxZoom = 8;

    /// <summary>Always smooth (never the view's sampling), so the export matches the screen at any zoom.</summary>
    private static readonly SKSamplingOptions Smooth = new(SKCubicResampler.Mitchell);

    public override AnnotationKind Kind => AnnotationKind.Magnify;

    public override StyleParts StyleParts => StyleParts.Color | StyleParts.StrokeWidth | StyleParts.Strength;

    /// <summary>How much larger the callout shows the source: the style's strength, 2 to 8.</summary>
    public float Zoom => Math.Clamp(Style.Strength, MinZoom, MaxZoom);

    public float CalloutRadius => Radius * Zoom;

    public override SKRect Bounds
    {
        get
        {
            var reach = Style.StrokeWidth + 6; // ring, and the callout's soft shadow
            var source = Circle(Source, Radius + reach);
            var callout = Circle(Callout, CalloutRadius + reach);
            return SKRect.Union(source, callout);
        }
    }

    public override bool HitTest(SKPoint point, float tolerance) =>
        InCallout(point, tolerance) || InSource(point, tolerance) || Geometry.DistanceToSegment(point, Source, Callout) <= (Style.StrokeWidth / 2) + tolerance;

    public override IReadOnlyList<Handle> GetHandles() =>
    [
        new(HandleKind.Start, new SKPoint(Source.X + Radius, Source.Y)),
        new(HandleKind.End, new SKPoint(Callout.X + CalloutRadius, Callout.Y)),
    ];

    /// <summary>Start: the source circle's size. End: the callout's size, which sets the zoom.</summary>
    public override Annotation WithHandleMoved(HandleKind handle, SKPoint position, bool constrain) => handle switch
    {
        HandleKind.Start => this with { Radius = MathF.Max(MinRadius, Geometry.Distance(Source, position)) },
        HandleKind.End => this with
        {
            Style = Style with { Strength = Math.Clamp(MathF.Round(Geometry.Distance(Callout, position) / Radius * 2) / 2, MinZoom, MaxZoom) },
        },
        _ => this,
    };

    public override Annotation Offset(float dx, float dy) =>
        this with { Source = Geometry.Offset(Source, dx, dy), Callout = Geometry.Offset(Callout, dx, dy) };

    /// <summary>A drag that starts on the callout moves the callout; on the source circle, the source; elsewhere, both.</summary>
    public override Annotation MoveFrom(SKPoint origin, float dx, float dy)
    {
        if (InCallout(origin, 0))
        {
            return this with { Callout = Geometry.Offset(Callout, dx, dy) };
        }

        return InSource(origin, 0) ? this with { Source = Geometry.Offset(Source, dx, dy) } : Offset(dx, dy);
    }

    /// <summary>Moves with the image; the zoom stays as it is (only the sizes scale).</summary>
    public override Annotation Transform(SKMatrix matrix, float scale) => this with
    {
        Source = matrix.MapPoint(Source),
        Callout = matrix.MapPoint(Callout),
        Radius = Radius * scale,
        Style = Style.Scaled(scale) with { Strength = Style.Strength },
    };

    /// <summary>
    /// A callout for the circle inside <paramref name="box"/> (dragged out with the tool), placed beside it where
    /// it fits in an image of <paramref name="imageSize"/>: up and to the right if there is room, else the next best.
    /// </summary>
    public static MagnifyAnnotation Place(AnnotationId id, AnnotationStyle style, SKRect box, SKSizeI imageSize)
    {
        ArgumentNullException.ThrowIfNull(style);
        var source = new SKPoint(box.MidX, box.MidY);
        var radius = MathF.Max(1, MathF.Min(box.Width, box.Height) / 2);
        var made = new MagnifyAnnotation(id, style, source, radius, source);
        return made with { Callout = made.BestCallout(imageSize) };
    }

    private SKPoint BestCallout(SKSizeI imageSize)
    {
        var r = CalloutRadius;
        var distance = Radius + r + MathF.Max(12, Radius * 0.5f);
        var diagonal = 1 / MathF.Sqrt(2);
        SKPoint[] directions = [new(diagonal, -diagonal), new(-diagonal, -diagonal), new(diagonal, diagonal), new(-diagonal, diagonal), new(1, 0), new(-1, 0), new(0, -1), new(0, 1)];
        SKPoint At(SKPoint d) => new(Source.X + (d.X * distance), Source.Y + (d.Y * distance));
        float Room(SKPoint c) => MathF.Min(MathF.Min(c.X - r, imageSize.Width - c.X - r), MathF.Min(c.Y - r, imageSize.Height - c.Y - r));

        foreach (var direction in directions)
        {
            if (Room(At(direction)) >= 0)
            {
                return At(direction);
            }
        }

        // Nowhere fits whole: the place that sticks out least, pulled inside the image as far as it goes.
        var best = directions.Select(At).MaxBy(Room);
        return new SKPoint(
            Math.Clamp(best.X, MathF.Min(r, imageSize.Width / 2f), MathF.Max(imageSize.Width - r, imageSize.Width / 2f)),
            Math.Clamp(best.Y, MathF.Min(r, imageSize.Height / 2f), MathF.Max(imageSize.Height - r, imageSize.Height / 2f)));
    }

    protected override void RenderCore(SKCanvas canvas, RenderContext context)
    {
        var width = Style.StrokeWidth;
        var r = CalloutRadius;
        using var ring = StrokePaint();

        // The line between the circles, from edge to edge (none when they overlap).
        var gap = Geometry.Distance(Source, Callout);
        if (gap > Radius + r)
        {
            var ux = (Callout.X - Source.X) / gap;
            var uy = (Callout.Y - Source.Y) / gap;
            using var line = StrokePaint();
            line.StrokeWidth = MathF.Max(1, width / 2);
            canvas.DrawLine(Source.X + (ux * Radius), Source.Y + (uy * Radius), Callout.X - (ux * r), Callout.Y - (uy * r), line);
        }

        using (var thin = StrokePaint())
        {
            thin.StrokeWidth = MathF.Max(1, width / 2);
            canvas.DrawCircle(Source, Radius, thin);
        }

        using (var shadow = new SKPaint { IsAntialias = true, Color = SKColors.Black.WithAlpha(70) })
        using (var blur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 3))
        {
            shadow.MaskFilter = blur;
            canvas.DrawCircle(Callout.X, Callout.Y + 2, r, shadow);
        }

        canvas.Save();
        using (var lens = new SKPathBuilder())
        {
            lens.AddCircle(Callout.X, Callout.Y, r);
            using var path = lens.Detach();
            canvas.ClipPath(path, SKClipOperation.Intersect, antialias: true);
        }

        // The zoomed pixels are made once on whole image pixels, then drawn like the image itself: the screen at
        // 100% and the export get the very same pixels whatever the view's own transform.
        var box = SKRectI.Create(
            (int)MathF.Floor(Callout.X - r),
            (int)MathF.Floor(Callout.Y - r),
            (int)MathF.Ceiling(Callout.X + r) - (int)MathF.Floor(Callout.X - r),
            (int)MathF.Ceiling(Callout.Y + r) - (int)MathF.Floor(Callout.Y - r));
        var image = context.Image;
        var patch = context.Effects.GetOrCreate(new LensKey(image.UniqueId, box, Source, Callout, Zoom), () => Lens(image, box));
        canvas.DrawImage(patch, box.Left, box.Top, context.Sampling, null);
        canvas.Restore();

        canvas.DrawCircle(Callout, r, ring);
    }

    private SKImage Lens(SKImage image, SKRectI box) => Imaging.SkiaImaging.Draw(Math.Max(1, box.Width), Math.Max(1, box.Height), c =>
    {
        c.Clear(SKColors.White);
        c.Translate(Callout.X - box.Left, Callout.Y - box.Top);
        c.Scale(Zoom);
        c.Translate(-Source.X, -Source.Y);
        c.DrawImage(image, 0, 0, Smooth, null);
    });

    private readonly record struct LensKey(uint ImageId, SKRectI Box, SKPoint Source, SKPoint Callout, float Zoom);

    private bool InCallout(SKPoint p, float tolerance) => Geometry.Distance(p, Callout) <= CalloutRadius + (Style.StrokeWidth / 2) + tolerance;

    private bool InSource(SKPoint p, float tolerance) => Geometry.Distance(p, Source) <= Radius + tolerance;

    private static SKRect Circle(SKPoint centre, float radius) => new(centre.X - radius, centre.Y - radius, centre.X + radius, centre.Y + radius);
}
