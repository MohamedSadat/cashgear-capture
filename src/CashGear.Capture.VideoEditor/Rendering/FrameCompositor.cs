using CashGear.Capture.Editor.Model;
using CashGear.Capture.Editor.Rendering;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Playback;
using SkiaSharp;

namespace CashGear.Capture.VideoEditor.Rendering;

/// <summary>Where the compositor gets each clip's pictures: decoded ahead for the preview, read in turn for the export.</summary>
public interface IFrameProvider
{
    /// <summary>The picture of <paramref name="clip"/> shown at <paramref name="sourceTime"/> (AddRef'd: the caller releases it), or null when it isn't ready.</summary>
    VideoFrame? Frame(Clip clip, MediaAsset asset, double sourceTime);
}

/// <summary>What drawing a frame found.</summary>
/// <param name="Missing">Pictures that weren't decoded yet (the preview draws again when they are).</param>
public readonly record struct CompositionResult(int Missing);

/// <summary>
/// Draws a project at a moment: the background, then the picture tracks from the bottom up (each clip where its
/// placement puts it, faded in and out, crossfading where clips overlap), then the annotations and effects. The
/// preview (on the GPU or in memory) and the export both draw through it, in the canvas's own pixels: the caller
/// scales the canvas to the view or to the export's size.
/// </summary>
public sealed class FrameCompositor
{
    /// <summary>Rounded rectangles' corners, as a part of the shorter side (as the recording's webcam overlay draws them).</summary>
    public const double CornerRadius = 0.12;

    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.None);

    private readonly IFrameProvider _frames;
    private readonly MetadataOverlays _overlays = new();

    /// <summary>The recordings' metadata the effect tracks draw from.</summary>
    public MetadataOverlays Overlays => _overlays;

    public FrameCompositor(IFrameProvider frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        _frames = frames;
    }

    public CompositionResult Render(SKCanvas canvas, VideoProject project, double time)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(project);
        canvas.Clear(Color(project.Canvas.Background, SKColors.Black));
        return new CompositionResult(RenderTracks(canvas, project, time, project.Tracks.Count));
    }

    /// <summary>
    /// The annotation as the image editor would draw it at <paramref name="time"/> (its place then, its look with its
    /// fade), in canvas pixels; null for blur and pixelate, which the compositor draws itself.
    /// </summary>
    public static Annotation? AnnotationFor(AnnotationItem item, double time)
    {
        ArgumentNullException.ThrowIfNull(item);
        var place = item.At(time);
        var look = item.Look;
        var style = new AnnotationStyle
        {
            Color = Color(look.Color, EditorColors.Red),
            StrokeWidth = (float)look.StrokeWidth,
            Fill = look.Fill is null ? SKColors.Transparent : Color(look.Fill, SKColors.Transparent),
            FontFamily = look.FontFamily,
            FontSize = (float)look.FontSize,
            Bold = look.Bold,
            Strength = (float)look.Strength,
        }.Normalize() with { Opacity = (float)Math.Clamp(look.Opacity * item.Fade(time), 0, 1) };
        var id = new AnnotationId(0);
        var from = new SKPoint((float)place.X, (float)place.Y);
        var to = new SKPoint((float)(place.X + place.Width), (float)(place.Y + place.Height));
        var box = SKRect.Create((float)Math.Min(place.X, place.X + place.Width), (float)Math.Min(place.Y, place.Y + place.Height), (float)Math.Abs(place.Width), (float)Math.Abs(place.Height));
        return item.Shape switch
        {
            AnnotationShape.Text => new TextAnnotation(id, style, from, item.Text.Length == 0 ? " " : item.Text),
            AnnotationShape.Arrow => new ArrowAnnotation(id, style, from, to),
            AnnotationShape.Line => new LineAnnotation(id, style, from, to),
            AnnotationShape.Rectangle => new RectangleAnnotation(id, style, box),
            AnnotationShape.Ellipse => new EllipseAnnotation(id, style, box),
            AnnotationShape.Highlight => new HighlightAnnotation(id, style with { Color = Color(look.Color, EditorColors.Highlighter) }, box),
            _ => null,
        };
    }

    /// <summary>What an annotation covers at <paramref name="time"/> (canvas pixels): for picking it on the preview.</summary>
    public static SKRect Bounds(AnnotationItem item, double time)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (AnnotationFor(item, time) is { } annotation)
        {
            return annotation.Bounds;
        }

        var place = item.At(time);
        return SKRect.Create((float)place.X, (float)place.Y, (float)place.Width, (float)place.Height);
    }

    /// <summary>The tracks before <paramref name="count"/>, bottom up; returns the pictures that weren't ready.</summary>
    private int RenderTracks(SKCanvas canvas, VideoProject project, double time, int count)
    {
        var size = project.Canvas;
        var missing = 0;
        for (var index = 0; index < count; index++)
        {
            var track = project.Tracks[index];
            if (track.Hidden)
            {
                continue;
            }

            if (track.Kind == TrackKind.Effect)
            {
                foreach (var effect in track.Items.OfType<EffectItem>().Where(e => e.Covers(time)))
                {
                    _overlays.Draw(canvas, project, index, effect, time);
                }

                continue;
            }

            if (track.Kind == TrackKind.Annotation)
            {
                foreach (var item in track.Items.OfType<AnnotationItem>().Where(a => a.Covers(time)))
                {
                    missing += DrawAnnotation(canvas, project, index, item, time);
                }

                continue;
            }

            if (!track.HasPictures)
            {
                continue;
            }

            Clip? previous = null;
            foreach (var clip in track.Clips.Where(c => c.Covers(time)))
            {
                if (project.Asset(clip.AssetId) is not { } asset)
                {
                    continue;
                }

                var opacity = Fade(clip, time);
                if (previous is not null && previous.End > clip.Start + Times.Epsilon)
                {
                    // Crossfade: the later clip comes in over the overlap.
                    opacity *= Math.Clamp((time - clip.Start) / (previous.End - clip.Start), 0, 1);
                }

                previous = clip;
                if (opacity <= 0)
                {
                    continue;
                }

                var frame = _frames.Frame(clip, asset, clip.SourceAt(time));
                if (frame is null)
                {
                    missing++;
                    continue;
                }

                try
                {
                    var stream = asset.Stream(clip.Stream);
                    var pictureWidth = stream is { Width: > 0 } ? stream.Width : frame.Width;
                    var pictureHeight = stream is { Height: > 0 } ? stream.Height : frame.Height;
                    DrawPicture(canvas, frame, clip.Placement ?? Placement.Fit, size, pictureWidth, pictureHeight, opacity);
                }
                finally
                {
                    frame.Release();
                }
            }
        }

        return missing;
    }

    private int DrawAnnotation(SKCanvas canvas, VideoProject project, int trackIndex, AnnotationItem item, double time)
    {
        if (item.Fade(time) * item.Look.Opacity <= 0.005)
        {
            return 0;
        }

        if (AnnotationFor(item, time) is { } annotation)
        {
            annotation.Render(canvas, new RenderContext(Blank, NoEffects, Sampling));
            return 0;
        }

        return DrawHidden(canvas, project, trackIndex, item, time);
    }

    /// <summary>
    /// A blurred or pixelated area: what the tracks below draw there is drawn again on its own (with a margin, so a
    /// blur doesn't darken at its edges), blurred or pixelated, and drawn over them.
    /// </summary>
    private int DrawHidden(SKCanvas canvas, VideoProject project, int trackIndex, AnnotationItem item, double time)
    {
        var place = item.At(time);
        var canvasArea = SKRectI.Create(project.Canvas.Width, project.Canvas.Height);
        var area = SKRectI.Intersect(SKRectI.Round(SKRect.Create((float)Math.Min(place.X, place.X + place.Width), (float)Math.Min(place.Y, place.Y + place.Height), (float)Math.Abs(place.Width), (float)Math.Abs(place.Height))), canvasArea);
        if (area.Width <= 0 || area.Height <= 0)
        {
            return 0;
        }

        var strength = (float)Math.Clamp(item.Look.Strength, AnnotationStyle.MinStrength, AnnotationStyle.MaxStrength);
        var blur = item.Shape == AnnotationShape.Blur;
        var margin = blur ? (int)Math.Ceiling(strength * 3) : 0;
        var region = SKRectI.Intersect(SKRectI.Inflate(area, margin, margin), canvasArea);
        using var surface = SKSurface.Create(new SKImageInfo(region.Width, region.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (surface is null)
        {
            return 0;
        }

        surface.Canvas.Clear(Color(project.Canvas.Background, SKColors.Black));
        surface.Canvas.Translate(-region.Left, -region.Top);
        var missing = RenderTracks(surface.Canvas, project, time, trackIndex);
        using var under = surface.Snapshot();
        var inner = SKRectI.Create(area.Left - region.Left, area.Top - region.Top, area.Width, area.Height);
        using var patch = blur ? EffectCache.Blur(under, SKRectI.Create(region.Width, region.Height), strength) : EffectCache.Pixelate(under, inner, strength);
        var source = blur ? SKRect.Create(inner.Left, inner.Top, inner.Width, inner.Height) : SKRect.Create(inner.Width, inner.Height);
        using var paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(Math.Clamp(item.Fade(time) * item.Look.Opacity, 0, 1) * 255)) };
        canvas.DrawImage(patch, source, SKRect.Create(area.Left, area.Top, area.Width, area.Height), new SKSamplingOptions(SKFilterMode.Nearest), paint);
        return missing;
    }

    private static readonly SKImage Blank = SKImage.Create(new SKImageInfo(1, 1));

    private static readonly EffectCache NoEffects = new();

    /// <summary>A clip's fade at <paramref name="time"/>: 0 to 1 over its fade in, 1 to 0 over its fade out.</summary>
    public static double Fade(Clip clip, double time)
    {
        ArgumentNullException.ThrowIfNull(clip);
        var level = 1.0;
        if (clip.FadeIn > 0)
        {
            level = Math.Min(level, (time - clip.Start) / clip.FadeIn);
        }

        if (clip.FadeOut > 0)
        {
            level = Math.Min(level, (clip.End - time) / clip.FadeOut);
        }

        return Math.Clamp(level, 0, 1);
    }

    /// <summary>A <c>#RRGGBB</c> / <c>#AARRGGBB</c> colour, or <paramref name="fallback"/>.</summary>
    public static SKColor Color(string? text, SKColor fallback) => text is not null && SKColor.TryParse(text, out var color) ? color : fallback;

    /// <summary>The outline of a picture placed in <paramref name="area"/>: its rectangle, rounded, or the circle inside it.</summary>
    public static SKPath Outline(PlacementShape shape, SKRect area)
    {
        using var builder = new SKPathBuilder();
        var radius = Math.Min(area.Width, area.Height) * (float)CornerRadius;
        switch (shape)
        {
            case PlacementShape.Circle:
                builder.AddOval(area);
                break;
            case PlacementShape.Rounded:
                builder.AddRoundRect(new SKRoundRect(area, radius, radius));
                break;
            default:
                builder.AddRect(area);
                break;
        }

        return builder.Detach();
    }

    private static void DrawPicture(SKCanvas canvas, VideoFrame frame, Placement placement, ProjectCanvas size, int pictureWidth, int pictureHeight, double opacity)
    {
        var (x, y, width, height) = placement.Area(size.Width, size.Height, pictureWidth, pictureHeight);
        var target = SKRect.Create((float)x, (float)y, (float)width, (float)height);
        var source = SKRect.Create(frame.Width, frame.Height);
        if (placement.Shape == PlacementShape.Circle)
        {
            var side = Math.Min(frame.Width, frame.Height);
            source = SKRect.Create((frame.Width - side) / 2f, (frame.Height - side) / 2f, side, side);
        }

        using var outline = Outline(placement.Shape, target);

        var alpha = (byte)Math.Round(Math.Clamp(opacity * placement.Opacity, 0, 1) * 255);
        using var image = frame.ToImage();
        using (var paint = new SKPaint { Color = SKColors.White.WithAlpha(alpha) })
        {
            canvas.Save();
            if (placement.Shape != PlacementShape.Rectangle)
            {
                canvas.ClipPath(outline, SKClipOperation.Intersect, antialias: true);
            }

            if (placement.Mirror)
            {
                canvas.Scale(-1, 1, target.MidX, 0);
            }

            canvas.DrawImage(image, source, target, Sampling, paint);
            canvas.Restore();
        }

        if (placement.BorderWidth > 0 && placement.Border is not null)
        {
            using var border = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)placement.BorderWidth,
                Color = Color(placement.Border, SKColors.White).WithAlpha(alpha),
            };
            canvas.DrawPath(outline, border);
        }
    }
}
