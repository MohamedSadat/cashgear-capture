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

/// <summary>The masks of webcam pictures: where the person is, at a source time.</summary>
public interface IWebcamMasks
{
    /// <summary>The person's mask for that picture as a grey image (the caller disposes it), or null when it isn't ready.</summary>
    SKImage? Mask(MediaAsset asset, int stream, double sourceTime);
}

/// <summary>What drawing a frame found.</summary>
/// <param name="Missing">Pictures that weren't decoded yet (the preview draws again when they are).</param>
public readonly record struct CompositionResult(int Missing);

/// <summary>
/// Draws a project at a moment: the background, then the tracks from the bottom up: pictures (each clip where its
/// placement or the layout track puts it, with its corners, shadow and frame, faded in and out, crossfading where clips
/// overlap; the screen through the zooms' camera, blurred along its movement), annotations, effects and captions. The
/// preview (on the GPU or in memory) and the export both draw through it, in the canvas's own pixels: the caller scales
/// the canvas to the view or to the export's size.
/// </summary>
public sealed class FrameCompositor
{
    /// <summary>Rounded rectangles' corners, as a part of the shorter side (as the recording's webcam overlay draws them).</summary>
    public const double CornerRadius = 0.12;

    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.None);

    /// <summary>Things kept for the processor's frames: a background and a shadow for each of a few pictures.</summary>
    private const int MaxKept = 6;

    private readonly IFrameProvider _frames;
    private readonly MetadataOverlays _overlays = new();
    private readonly ZoomCamera _camera;
    private readonly CaptionRenderer _captions = new();
    private readonly Dictionary<string, SKImage?> _pictures = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(object Key, SKMatrix Matrix, SKRectI Area, SKImage Image)> _kept = [];
    private (string? Clip, double Time, double Blur, SKImage? Image) _blurredScreen;

    /// <summary>The webcam clips' masks, for a background that is blurred, replaced or removed.</summary>
    public IWebcamMasks? Masks { get; set; }

    /// <summary>The most samples a frame's motion blur takes. The export may take more than the preview.</summary>
    public int MotionSamples { get; set; } = 16;

    /// <summary>The recordings' metadata the effect tracks draw from.</summary>
    public MetadataOverlays Overlays => _overlays;

    /// <summary>The zooms' camera over the screen pictures.</summary>
    public ZoomCamera Camera => _camera;

    public FrameCompositor(IFrameProvider frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        _frames = frames;
        _camera = new ZoomCamera(_overlays);
    }

    public CompositionResult Render(SKCanvas canvas, VideoProject project, double time)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(project);
        var camera = _camera.View(project, time).Matrix(_camera.Viewport(project, time));
        var missing = DrawBackground(canvas, project, time, camera);
        var pass = new Pass(camera, LayoutEngine.ScreenCell(project, time), _camera.Motion(project, time, MotionSamples));
        return new CompositionResult(missing + RenderTracks(canvas, project, time, project.Tracks.Count, pass));
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

    /// <summary>
    /// The tracks before <paramref name="count"/>, bottom up, the screen pictures (and what marks a place in them)
    /// through the camera of <paramref name="pass"/>; returns the pictures that weren't ready.
    /// </summary>
    private int RenderTracks(SKCanvas canvas, VideoProject project, double time, int count, Pass pass)
    {
        var camera = pass.Camera;
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
                    _overlays.Draw(canvas, project, index, effect, time, camera, pass.Cell);
                }

                continue;
            }

            if (track.Kind == TrackKind.Caption)
            {
                foreach (var caption in track.Items.OfType<CaptionItem>().Where(c => c.Covers(time) && c.Text.Length > 0))
                {
                    _captions.Draw(canvas, caption, track.Captions ?? new CaptionStyle(), size);
                }

                continue;
            }

            if (track.Kind == TrackKind.Annotation)
            {
                foreach (var item in track.Items.OfType<AnnotationItem>().Where(a => a.Covers(time)))
                {
                    missing += DrawAnnotation(canvas, project, index, item, time, pass);
                }

                continue;
            }

            if (!track.HasPictures)
            {
                continue;
            }

            if (track.Kind == TrackKind.Video && pass.Motion is { } motion)
            {
                missing += DrawMoving(canvas, pass.Cell, motion, layer => DrawClips(layer, project, track, time));
                continue;
            }

            var zoomed = track.Kind == TrackKind.Video && !camera.IsIdentity;
            if (zoomed)
            {
                canvas.Save();
                if (pass.Cell is { } cell)
                {
                    canvas.ClipRect(cell);
                }

                canvas.Concat(in camera);
            }

            missing += DrawClips(canvas, project, track, time);
            if (zoomed)
            {
                canvas.Restore();
            }
        }

        return missing;
    }

    /// <summary>A picture track's clips at <paramref name="time"/>; returns the pictures that weren't ready.</summary>
    private int DrawClips(SKCanvas canvas, VideoProject project, Track track, double time)
    {
        var missing = 0;
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
            var box = LayoutEngine.Box(project, track, clip, time);
            if (opacity <= 0 || box is { Opacity: <= 0 })
            {
                continue;
            }

            var sourceTime = clip.SourceAt(time);
            var frame = _frames.Frame(clip, asset, sourceTime);
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
                DrawPicture(canvas, project, clip, asset, sourceTime, frame, box, pictureWidth, pictureHeight, opacity);
            }
            finally
            {
                frame.Release();
            }
        }

        return missing;
    }

    /// <summary>
    /// Pictures on the move: drawn once through the camera in the middle of the shutter's time into a layer of the
    /// target's own pixels (on its graphics card when it has one), then read along the camera's path by a shader.
    /// </summary>
    private static int DrawMoving(SKCanvas canvas, SKRect? cell, CameraMotion motion, Func<SKCanvas, int> draw)
    {
        var bounds = canvas.DeviceClipBounds;
        var info = new SKImageInfo(Math.Max(1, bounds.Width), Math.Max(1, bounds.Height), SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = (canvas.Context is { } gpu ? SKSurface.Create(gpu, false, info) : null) ?? SKSurface.Create(info);
        var outer = canvas.TotalMatrix;
        var middle = outer.PreConcat(motion.Middle);
        if (surface is null || bounds.Width <= 0 || bounds.Height <= 0 || !outer.TryInvert(out var outerBack) || !motion.From.TryInvert(out var fromBack) || !motion.To.TryInvert(out var toBack))
        {
            canvas.Save();
            canvas.Concat(motion.Middle);
            var drawn = draw(canvas);
            canvas.Restore();
            return drawn;
        }

        var layer = surface.Canvas;
        layer.Clear(SKColors.Transparent);
        layer.Translate(-bounds.Left, -bounds.Top);
        layer.Concat(in middle);
        var missing = draw(layer);
        surface.Flush();

        // Where a pixel of the target was in the layer as the shutter opened and as it closed.
        float[] Along(SKMatrix back)
        {
            var map = middle.PreConcat(back).PreConcat(outerBack);
            return [map.ScaleX, map.ScaleY, map.TransX - bounds.Left, map.TransY - bounds.Top];
        }

        using var image = surface.Snapshot();
        using var shader = image.ToShader(SKShaderTileMode.Decal, SKShaderTileMode.Decal, Sampling);
        using var blurred = StudioShaders.MotionBlur(shader, Along(fromBack), Along(toBack), motion.Samples);
        canvas.Save();
        if (cell is { } part)
        {
            canvas.ClipRect(part);
        }

        canvas.ResetMatrix();
        if (canvas.Context is null)
        {
            DrawInTiles(canvas, bounds, blurred);
        }
        else
        {
            using var paint = new SKPaint { Shader = blurred };
            canvas.DrawRect(SKRect.Create(bounds.Left, bounds.Top, bounds.Width, bounds.Height), paint);
        }

        canvas.Restore();
        return missing;
    }

    /// <summary>
    /// A shader over <paramref name="bounds"/> (device pixels) on the processor, in strips side by side: the same code
    /// as on the graphics card, where a pixel reading a picture many times is otherwise far too slow one at a time.
    /// </summary>
    private static void DrawInTiles(SKCanvas canvas, SKRectI bounds, SKShader shader)
    {
        using var result = new SKBitmap(new SKImageInfo(bounds.Width, bounds.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var strips = Math.Clamp(Environment.ProcessorCount, 1, 32);
        Parallel.For(0, strips, strip =>
        {
            var top = bounds.Height * strip / strips;
            var bottom = bounds.Height * (strip + 1) / strips;
            using var part = new SKBitmap();
            if (bottom > top && result.ExtractSubset(part, SKRectI.Create(0, top, bounds.Width, bottom - top)))
            {
                using var target = new SKCanvas(part);
                target.Translate(-bounds.Left, -bounds.Top - top);
                using var paint = new SKPaint { Shader = shader };
                target.DrawRect(SKRect.Create(bounds.Left, bounds.Top + top, bounds.Width, bottom - top), paint);
            }
        });
        using var image = SKImage.FromBitmap(result);
        canvas.DrawImage(image, bounds.Left, bounds.Top, new SKSamplingOptions(SKFilterMode.Nearest));
    }

    /// <summary>
    /// Something that doesn't change from frame to frame, drawn by <paramref name="draw"/> in canvas coordinates. On the
    /// graphics card it is simply drawn. On the processor (an export of many frames) its pixels are kept for as long as
    /// the same key and the same place on the target ask for them, and copied.
    /// </summary>
    private void DrawKept(SKCanvas canvas, object key, SKRect area, Action<SKCanvas> draw)
    {
        if (canvas.Context is not null)
        {
            draw(canvas);
            return;
        }

        var matrix = canvas.TotalMatrix;
        var outer = SKRectI.Ceiling(matrix.MapRect(area), true);
        var clip = canvas.DeviceClipBounds;
        outer = SKRectI.Intersect(outer, clip);
        if (outer.Width <= 0 || outer.Height <= 0)
        {
            return;
        }

        SKImage? kept = null;
        foreach (var entry in _kept)
        {
            if (entry.Key.Equals(key) && entry.Matrix == matrix && entry.Area == outer)
            {
                kept = entry.Image;
                break;
            }
        }

        if (kept is null)
        {
            using var surface = SKSurface.Create(new SKImageInfo(outer.Width, outer.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            if (surface is null)
            {
                draw(canvas);
                return;
            }

            surface.Canvas.Translate(-outer.Left, -outer.Top);
            surface.Canvas.Concat(in matrix);
            draw(surface.Canvas);
            kept = surface.Snapshot();
            _kept.Add((key, matrix, outer, kept));
            if (_kept.Count > MaxKept)
            {
                _kept[0].Image.Dispose();
                _kept.RemoveAt(0);
            }
        }

        canvas.Save();
        canvas.ResetMatrix();
        canvas.DrawImage(kept, outer.Left, outer.Top, new SKSamplingOptions(SKFilterMode.Nearest));
        canvas.Restore();
    }

    private int DrawAnnotation(SKCanvas canvas, VideoProject project, int trackIndex, AnnotationItem item, double time, Pass pass)
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

        return DrawHidden(canvas, project, trackIndex, item, time, pass);
    }

    /// <summary>
    /// A blurred or pixelated area: what the tracks below draw there is drawn again on its own (with a margin, so a
    /// blur doesn't darken at its edges), blurred or pixelated, and drawn over them.
    /// </summary>
    private int DrawHidden(SKCanvas canvas, VideoProject project, int trackIndex, AnnotationItem item, double time, Pass pass)
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

        surface.Canvas.Translate(-region.Left, -region.Top);
        var missing = DrawBackground(surface.Canvas, project, time, pass.Camera);
        missing += RenderTracks(surface.Canvas, project, time, trackIndex, pass);
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

    /// <summary>The radius of a picture's corners in <paramref name="area"/>: its own, or a rounded shape's usual ones, or half a circle.</summary>
    public static float Radius(Placement placement, SKRect area)
    {
        ArgumentNullException.ThrowIfNull(placement);
        var shorter = Math.Min(area.Width, area.Height);
        return placement.Shape switch
        {
            PlacementShape.Circle => shorter / 2,
            _ when placement.CornerRadius > 0 => (float)placement.CornerRadius,
            PlacementShape.Rounded => shorter * (float)CornerRadius,
            _ => 0,
        };
    }

    private void DrawPicture(SKCanvas canvas, VideoProject project, Clip clip, MediaAsset asset, double sourceTime, VideoFrame frame, PictureBox? box, int pictureWidth, int pictureHeight, double opacity)
    {
        var placement = clip.Placement ?? Placement.Fit;
        var size = project.Canvas;
        var (x, y, width, height) = placement.Area(size.Width, size.Height, pictureWidth, pictureHeight);
        var target = box?.Area ?? SKRect.Create((float)x, (float)y, (float)width, (float)height);
        if (target.Width <= 0 || target.Height <= 0)
        {
            return;
        }

        var source = SKRect.Create(frame.Width, frame.Height);
        if (box is { Cover: true })
        {
            source = CutToShape(frame.Width, frame.Height, target.Width / target.Height);
        }
        else if (placement.Shape == PlacementShape.Circle)
        {
            var side = Math.Min(frame.Width, frame.Height);
            source = SKRect.Create((frame.Width - side) / 2f, (frame.Height - side) / 2f, side, side);
        }

        var radius = Math.Max(box?.Radius ?? Radius(placement, target), DeviceFrames.PictureRadius(placement.Frame, target));
        using var outline = box is not null || placement.CornerRadius > 0 || placement.Frame != DeviceFrame.None
            ? Rounded(target, radius)
            : Outline(placement.Shape, target);

        var alpha = (byte)Math.Round(Math.Clamp(opacity * placement.Opacity * (box?.Opacity ?? 1), 0, 1) * 255);
        if (placement.Shadow is { Opacity: > 0 } shadow)
        {
            var (body, bodyRadius) = placement.Frame == DeviceFrame.None ? (target, radius) : DeviceFrames.Body(placement.Frame, target);
            var reach = (float)((Math.Max(0.5, shadow.Blur) * 3) + 2 + Math.Max(Math.Abs(shadow.OffsetX), Math.Abs(shadow.OffsetY)));
            DrawKept(canvas, (shadow, body, bodyRadius, alpha), SKRect.Inflate(body, reach, reach), target => DrawShadow(target, shadow, body, bodyRadius, alpha));
        }

        DeviceFrames.Draw(canvas, placement.Frame, target, alpha);
        using var image = frame.ToImage();
        using (var paint = new SKPaint { Color = SKColors.White.WithAlpha(alpha) })
        {
            canvas.Save();

            // A look's corners are drawn as a rounded rectangle filled with the picture: on the processor a soft-edged
            // clip costs more than the rest of the frame. Only what 1.1.1 knew (a rounded shape, a circle) clips, as it did.
            var rounded = box is not null || placement.CornerRadius > 0 || placement.Frame != DeviceFrame.None;
            if (!rounded && placement.Shape != PlacementShape.Rectangle)
            {
                canvas.ClipPath(outline, SKClipOperation.Intersect, antialias: true);
            }

            if (placement.Mirror)
            {
                canvas.Scale(-1, 1, target.MidX, 0);
            }

            if (clip.Background is { Effect: not BackgroundEffect.None } background && Masks?.Mask(asset, clip.Stream, sourceTime) is { } mask)
            {
                using (mask)
                {
                    DrawMatted(canvas, project, background, image, mask, source, target, frame.Width, frame.Height, paint, radius);
                }
            }
            else if (rounded && radius > 0)
            {
                using var picture = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, Sampling, Mapping(source, target));
                paint.Shader = picture;
                paint.IsAntialias = true;
                canvas.DrawRoundRect(target, radius, radius, paint);
                paint.Shader = null;
            }
            else
            {
                canvas.DrawImage(image, source, target, Sampling, paint);
            }

            canvas.Restore();
        }

        if (placement.InsetBorderWidth > 0 && placement.InsetBorder is { } inset)
        {
            // A line just inside the edge: its own outline, half its width in, with corners to match.
            var color = Color(inset, SKColors.White);
            var thickness = (float)placement.InsetBorderWidth;
            using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = thickness, Color = color.WithAlpha((byte)Math.Round(color.Alpha * (alpha / 255f))) };
            canvas.DrawRoundRect(SKRect.Inflate(target, -thickness / 2, -thickness / 2), Math.Max(0, radius - (thickness / 2)), Math.Max(0, radius - (thickness / 2)), line);
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

    private static SKPath Rounded(SKRect area, float radius)
    {
        using var builder = new SKPathBuilder();
        if (radius > 0)
        {
            builder.AddRoundRect(new SKRoundRect(area, radius, radius));
        }
        else
        {
            builder.AddRect(area);
        }

        return builder.Detach();
    }

    /// <summary>The middle of a picture cut to a shape (width over height).</summary>
    private static SKRect CutToShape(float width, float height, float shape)
    {
        if (width / height > shape)
        {
            var cut = height * shape;
            return SKRect.Create((width - cut) / 2, 0, cut, height);
        }

        var tall = width / shape;
        return SKRect.Create(0, (height - tall) / 2, width, tall);
    }

    /// <summary>A soft shadow under <paramref name="body"/>. Only what shows round it is worked out.</summary>
    private static void DrawShadow(SKCanvas canvas, PlacementShadow shadow, SKRect body, float radius, byte alpha)
    {
        var sigma = (float)Math.Max(0.5, shadow.Blur);
        var box = SKRect.Create(body.Left + (float)shadow.OffsetX, body.Top + (float)shadow.OffsetY, body.Width, body.Height);
        var color = Color(shadow.Color, SKColors.Black);
        var strength = (byte)Math.Round(Math.Clamp(shadow.Opacity, 0, 1) * alpha);
        using var shader = StudioShaders.Shadow(box, radius, sigma, color.WithAlpha(strength));
        using var paint = new SKPaint { Shader = shader };
        canvas.Save();
        var inner = SKRect.Inflate(body, -2, -2);
        if (inner.Width > 0 && inner.Height > 0)
        {
            canvas.ClipRoundRect(new SKRoundRect(inner, Math.Max(0, radius - 2), Math.Max(0, radius - 2)), SKClipOperation.Difference, antialias: true);
        }

        canvas.DrawRect(SKRect.Inflate(box, (sigma * 3) + 2, (sigma * 3) + 2), paint);
        canvas.Restore();
    }

    /// <summary>A webcam's picture with what is behind the person blurred, replaced or taken out, by the person's mask.</summary>
    private void DrawMatted(SKCanvas canvas, VideoProject project, WebcamBackground background, SKImage image, SKImage mask, SKRect source, SKRect target, int frameWidth, int frameHeight, SKPaint paint, float radius)
    {
        var toTarget = Mapping(source, target);
        using var picture = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, Sampling, toTarget);
        var maskToFrame = SKMatrix.CreateScale((float)frameWidth / mask.Width, (float)frameHeight / mask.Height);
        using var masked = mask.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, Sampling, toTarget.PreConcat(maskToFrame));
        using var back = BackOf(project, background, image, source, target);
        using var matte = StudioShaders.Matte(picture, masked, back);
        paint.Shader = matte;
        paint.IsAntialias = true;
        canvas.DrawRoundRect(target, radius, radius, paint);
        paint.Shader = null;
    }

    private SKShader BackOf(VideoProject project, WebcamBackground background, SKImage image, SKRect source, SKRect target)
    {
        if (background.Effect == BackgroundEffect.Remove)
        {
            return SKShader.CreateColor(SKColors.Transparent);
        }

        if (background.Effect == BackgroundEffect.Blur)
        {
            // Blurred small, so it costs little: the processor does it for the preview and the export alike.
            const int shrink = 4;
            var width = Math.Max(4, (int)Math.Ceiling(source.Width / shrink));
            var height = Math.Max(4, (int)Math.Ceiling(source.Height / shrink));
            using var small = Blurred(image, source, width, height, (float)Math.Max(0.5, background.Strength * source.Height / Math.Max(1, target.Height) / shrink));
            if (small is not null)
            {
                return small.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, Sampling, Mapping(SKRect.Create(width, height), target));
            }
        }

        if (background.Effect == BackgroundEffect.Replace && background.PictureAssetId is { } id && PictureOf(project, id) is { } replacement)
        {
            var cut = CutToShape(replacement.Width, replacement.Height, target.Width / target.Height);
            return replacement.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, Sampling, Mapping(cut, target));
        }

        return SKShader.CreateColor(Color(background.Color, SKColors.Black));
    }

    private static SKMatrix Mapping(SKRect from, SKRect to)
    {
        var (scaleX, scaleY) = (to.Width / from.Width, to.Height / from.Height);
        return SKMatrix.CreateScaleTranslation(scaleX, scaleY, to.Left - (from.Left * scaleX), to.Top - (from.Top * scaleY));
    }

    /// <summary>A part of a picture made small and blurred, on the processor (so the preview and the export get the same one).</summary>
    private static SKImage? Blurred(SKImage image, SKRect source, int width, int height, float sigma)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var small = SKSurface.Create(info);
        using var blurred = SKSurface.Create(info);
        if (small is null || blurred is null)
        {
            return null;
        }

        small.Canvas.DrawImage(image, source, SKRect.Create(width, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        using var shrunk = small.Snapshot();
        using var filter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp);
        using var paint = new SKPaint { ImageFilter = filter };
        blurred.Canvas.DrawImage(shrunk, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), paint);
        return blurred.Snapshot();
    }

    /// <summary>What is behind everything; returns the pictures that weren't ready.</summary>
    private int DrawBackground(SKCanvas canvas, VideoProject project, double time, SKMatrix camera)
    {
        var background = project.Canvas.Background;
        canvas.Clear(Color(background.Color, SKColors.Black));
        if (background.Kind == BackgroundKind.Color)
        {
            return 0;
        }

        var size = project.Canvas;
        var area = SKRect.Create(size.Width, size.Height);
        canvas.Save();
        if (background.Parallax > 0 && camera.ScaleX > 1)
        {
            // A little of the zoom, about the one point the camera keeps in place.
            var grow = 1 + ((camera.ScaleX - 1) * (float)Math.Clamp(background.Parallax, 0, 1) * 0.1f);
            var pivotX = Math.Clamp(camera.TransX / (1 - camera.ScaleX), 0, size.Width);
            var pivotY = Math.Clamp(camera.TransY / (1 - camera.ScaleY), 0, size.Height);
            canvas.Scale(grow, grow, pivotX, pivotY);
        }

        var missing = 0;
        switch (background.Kind)
        {
            case BackgroundKind.LinearGradient:
            case BackgroundKind.RadialGradient:
                DrawKept(canvas, (background, size.Width, size.Height), area, target =>
                {
                    using var shader = Gradient(background, size);
                    using var paint = new SKPaint { Shader = shader, IsDither = true };
                    target.DrawRect(area, paint);
                });
                break;

            case BackgroundKind.Picture when background.PictureAssetId is { } id && PictureOf(project, id) is { } picture:
                DrawKept(canvas, (background, size.Width, size.Height), area, target =>
                    target.DrawImage(picture, CutToShape(picture.Width, picture.Height, (float)size.Width / size.Height), area, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)));
                break;

            case BackgroundKind.BlurredScreen:
                missing += DrawBlurredScreen(canvas, project, time, background, area);
                break;
        }

        canvas.Restore();
        return missing;
    }

    private static SKShader Gradient(CanvasBackground background, ProjectCanvas size)
    {
        var first = Color(background.Color, SKColors.Black);
        var second = Color(background.Color2, first);
        if (background.Kind == BackgroundKind.RadialGradient)
        {
            var radius = (float)Math.Sqrt((size.Width * size.Width) + (size.Height * size.Height)) / 2;
            return SKShader.CreateRadialGradient(new SKPoint(size.Width / 2f, size.Height / 2f), radius, [first, second], SKShaderTileMode.Clamp);
        }

        var angle = background.Angle * Math.PI / 180;
        var (dx, dy) = (Math.Cos(angle), Math.Sin(angle));
        var reach = (Math.Abs(dx) * size.Width / 2) + (Math.Abs(dy) * size.Height / 2);
        var (midX, midY) = (size.Width / 2.0, size.Height / 2.0);
        return SKShader.CreateLinearGradient(
            new SKPoint((float)(midX - (dx * reach)), (float)(midY - (dy * reach))),
            new SKPoint((float)(midX + (dx * reach)), (float)(midY + (dy * reach))),
            [first, second],
            SKShaderTileMode.Clamp);
    }

    /// <summary>The top screen clip's picture covering the canvas, blurred. Made small on the processor; the last one stays while a picture is on its way.</summary>
    private int DrawBlurredScreen(SKCanvas canvas, VideoProject project, double time, CanvasBackground background, SKRect area)
    {
        const int shrink = 8;
        var missing = 0;
        if (ZoomCamera.TopVideo(project, time) is { } top && project.Asset(top.Clip.AssetId) is { } asset)
        {
            var sourceTime = top.Clip.SourceAt(time);
            if (_blurredScreen.Clip != top.Clip.Id || _blurredScreen.Time != sourceTime || _blurredScreen.Blur != background.Blur)
            {
                var frame = _frames.Frame(top.Clip, asset, sourceTime);
                if (frame is null)
                {
                    missing++;
                }
                else
                {
                    using (var image = frame.ToImage())
                    {
                        var width = Math.Max(4, project.Canvas.Width / shrink);
                        var height = Math.Max(4, project.Canvas.Height / shrink);
                        var made = Blurred(image, CutToShape(frame.Width, frame.Height, area.Width / area.Height), width, height, (float)Math.Max(0.5, background.Blur / shrink));
                        _blurredScreen.Image?.Dispose();
                        _blurredScreen = (top.Clip.Id, sourceTime, background.Blur, made);
                    }

                    frame.Release();
                }
            }
        }

        if (_blurredScreen.Image is { } blurred)
        {
            canvas.DrawImage(blurred, SKRect.Create(blurred.Width, blurred.Height), area, new SKSamplingOptions(SKCubicResampler.Mitchell));
        }

        return missing;
    }

    /// <summary>A picture file among the project's assets, read once; null when it can't be read.</summary>
    private SKImage? PictureOf(VideoProject project, string assetId)
    {
        if (project.Asset(assetId) is not { } asset)
        {
            return null;
        }

        if (_pictures.TryGetValue(asset.Path, out var known))
        {
            return known;
        }

        SKImage? picture = null;
        if (File.Exists(asset.Path))
        {
            using var encoded = SKImage.FromEncodedData(asset.Path);
            picture = encoded?.ToRasterImage(ensurePixelData: true);
        }

        _pictures[asset.Path] = picture;
        return picture;
    }

    /// <summary>What a frame's tracks are drawn with: the camera, the part of the canvas it shows the screen in, and its movement.</summary>
    private sealed record Pass(SKMatrix Camera, SKRect? Cell, CameraMotion? Motion);
}
