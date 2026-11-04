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
    private readonly Dictionary<string, PictureFile?> _stills = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SKImage> _solids = [];
    private readonly Dictionary<string, CubeLut?> _luts = new(StringComparer.OrdinalIgnoreCase);
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
        var color = place.Color ?? look.Color;
        var moment = TextAnimator.At(item, time);
        var level = (place.Opacity ?? look.Opacity) * (item.Motion?.At(time - item.Start).Opacity ?? 1) * moment.Opacity;
        var style = new AnnotationStyle
        {
            Color = Color(color, EditorColors.Red),
            StrokeWidth = (float)look.StrokeWidth,
            Fill = look.Fill is null ? SKColors.Transparent : Color(look.Fill, SKColors.Transparent),
            FontFamily = look.FontFamily,
            FontSize = (float)look.FontSize,
            Bold = look.Bold,
            Strength = (float)look.Strength,
        }.Normalize() with { Opacity = (float)Math.Clamp(level * item.Fade(time), 0, 1) };
        var id = new AnnotationId(0);
        var from = new SKPoint((float)place.X, (float)place.Y);
        var to = new SKPoint((float)(place.X + place.Width), (float)(place.Y + place.Height));
        var box = SKRect.Create((float)Math.Min(place.X, place.X + place.Width), (float)Math.Min(place.Y, place.Y + place.Height), (float)Math.Abs(place.Width), (float)Math.Abs(place.Height));
        return item.Shape switch
        {
            AnnotationShape.Text => new TextAnnotation(id, style, from, item.Text.Length == 0 ? " " : item.Text) { Revealed = moment.Reveal >= 1 ? null : TextAnimator.Graphemes(item.Text, moment.Reveal) },
            AnnotationShape.Arrow => new ArrowAnnotation(id, style, from, to),
            AnnotationShape.Line => new LineAnnotation(id, style, from, to),
            AnnotationShape.Rectangle => new RectangleAnnotation(id, style, box),
            AnnotationShape.Ellipse => new EllipseAnnotation(id, style, box),
            AnnotationShape.Highlight => new HighlightAnnotation(id, style with { Color = Color(color, EditorColors.Highlighter) }, box),
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

    /// <summary>What an annotation covers at <paramref name="time"/> with its motion, on <paramref name="canvas"/>.</summary>
    public static SKRect Bounds(AnnotationItem item, double time, ProjectCanvas canvas)
    {
        ArgumentNullException.ThrowIfNull(item);
        var bounds = Bounds(item, time);
        return item.Motion is { } motion ? MotionDrawing.Bounds(motion.At(time - item.Start), bounds, canvas) : bounds;
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
                var follows = track.MovesWithZoom && !camera.IsIdentity;
                foreach (var item in track.Items.OfType<AnnotationItem>().Where(a => a.Covers(time)))
                {
                    if (follows && AnnotationFor(item, time) is not null)
                    {
                        canvas.Save();
                        if (pass.Cell is { } part)
                        {
                            canvas.ClipRect(part);
                        }

                        canvas.Concat(in camera);
                        missing += DrawAnnotation(canvas, project, index, item, time, pass);
                        canvas.Restore();
                    }
                    else
                    {
                        missing += DrawAnnotation(canvas, project, index, item, time, pass, follows);
                    }
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

            var zoomed = (track.Kind == TrackKind.Video || track.MovesWithZoom) && !camera.IsIdentity;
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
        if (TransitionAt(track, time) is { } transition)
        {
            return DrawTransition(canvas, project, track, time, transition);
        }

        var missing = 0;
        Clip? previous = null;
        foreach (var clip in track.Clips.Where(c => c.Covers(time)))
        {
            var asset = project.Asset(clip.AssetId);
            if (asset is null && !clip.IsSolid)
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
            missing += DrawClip(canvas, project, track, clip, asset, time, opacity);
        }

        return missing;
    }

    /// <summary>One clip's picture at <paramref name="time"/>; returns 1 when it wasn't ready.</summary>
    private int DrawClip(SKCanvas canvas, VideoProject project, Track track, Clip clip, MediaAsset? asset, double time, double opacity)
    {
        var box = LayoutEngine.Box(project, track, clip, time);
        if (opacity <= 0 || box is { Opacity: <= 0 })
        {
            return 0;
        }

        if (asset is null)
        {
            DrawPicture(canvas, project, clip, asset, 0, Solid(clip.Solid!), box, project.Canvas.Width, project.Canvas.Height, opacity, time);
            return 0;
        }

        if (asset.IsPicture)
        {
            // A file that is gone draws nothing: there is nothing to wait for.
            if (StillOf(asset) is { } still)
            {
                DrawPicture(canvas, project, clip, asset, 0, still.At(time - clip.Start), box, still.Width, still.Height, opacity, time);
            }

            return 0;
        }

        var sourceTime = clip.SourceAt(time);
        var frame = _frames.Frame(clip, asset, sourceTime);
        if (frame is null)
        {
            return 1;
        }

        try
        {
            var stream = asset.Stream(clip.Stream);
            var pictureWidth = stream is { Width: > 0 } ? stream.Width : frame.Width;
            var pictureHeight = stream is { Height: > 0 } ? stream.Height : frame.Height;
            using var image = frame.ToImage();
            DrawPicture(canvas, project, clip, asset, sourceTime, image, box, pictureWidth, pictureHeight, opacity, time);
        }
        finally
        {
            frame.Release();
        }

        return 0;
    }

    /// <summary>The transition under way on a track at a moment: what goes and what comes (null: nothing there), and how far it is.</summary>
    internal readonly record struct ActiveTransition(ClipTransition Transition, Clip? From, Clip? To, double Progress);

    /// <summary>The transition under way at <paramref name="time"/> on <paramref name="track"/>, or null. A way in over an overlap comes before any other.</summary>
    internal static ActiveTransition? TransitionAt(Track track, double time)
    {
        ActiveTransition? edge = null;
        Clip? previous = null;
        foreach (var clip in track.Items.OfType<Clip>())
        {
            if (!clip.Covers(time))
            {
                continue;
            }

            var overlapped = previous is not null && previous.End > clip.Start + Times.Epsilon;
            if (clip.TransitionIn is { } coming)
            {
                var length = overlapped ? previous!.End - clip.Start : Math.Min(coming.Length, clip.Duration);
                if (time - clip.Start < length && length > 0)
                {
                    var progress = MotionCurves.Ease(coming.Easing, (time - clip.Start) / length);
                    if (overlapped)
                    {
                        return new ActiveTransition(coming, previous, clip, progress);
                    }

                    edge ??= new ActiveTransition(coming, null, clip, progress);
                }
            }

            if (clip.TransitionOut is { } going && !track.Items.OfType<Clip>().Any(c => c.Id != clip.Id && c.Start > clip.Start + Times.Epsilon && c.Start < clip.End - Times.Epsilon))
            {
                var length = Math.Min(going.Length, clip.Duration);
                if (clip.End - time <= length && length > 0)
                {
                    edge ??= new ActiveTransition(going, clip, null, MotionCurves.Ease(going.Easing, 1 - ((clip.End - time) / length)));
                }
            }

            previous = clip;
        }

        return edge;
    }

    /// <summary>
    /// A transition: each side drawn into a layer of the target's own pixels (on its graphics card when it has one), whole,
    /// and the two read by the transition's shader.
    /// </summary>
    private int DrawTransition(SKCanvas canvas, VideoProject project, Track track, double time, ActiveTransition active)
    {
        var bounds = canvas.DeviceClipBounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return 0;
        }

        var missing = 0;
        var outer = canvas.TotalMatrix;
        var info = new SKImageInfo(bounds.Width, bounds.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var toTarget = SKMatrix.CreateTranslation(bounds.Left, bounds.Top);
        SKShader Layer(Clip? clip)
        {
            if (clip is null)
            {
                return SKShader.CreateColor(SKColors.Transparent);
            }

            using var surface = (canvas.Context is { } gpu ? SKSurface.Create(gpu, false, info) : null) ?? SKSurface.Create(info);
            if (surface is null)
            {
                return SKShader.CreateColor(SKColors.Transparent);
            }

            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.Translate(-bounds.Left, -bounds.Top);
            surface.Canvas.Concat(in outer);
            missing += DrawClip(surface.Canvas, project, track, clip, project.Asset(clip.AssetId), time, Fade(clip, time));
            surface.Flush();
            using var image = surface.Snapshot();
            return image.ToShader(SKShaderTileMode.Decal, SKShaderTileMode.Decal, Sampling, toTarget);
        }

        using var going = Layer(active.From);
        using var coming = Layer(active.To);
        using var shader = TransitionShaders.Transition(active.Transition, going, coming, (float)active.Progress, bounds);
        canvas.Save();
        canvas.ResetMatrix();
        if (canvas.Context is null)
        {
            DrawInTiles(canvas, bounds, shader);
        }
        else
        {
            using var paint = new SKPaint { Shader = shader };
            canvas.DrawRect(SKRect.Create(bounds.Left, bounds.Top, bounds.Width, bounds.Height), paint);
        }

        canvas.Restore();
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

    private int DrawAnnotation(SKCanvas canvas, VideoProject project, int trackIndex, AnnotationItem item, double time, Pass pass, bool followsCamera = false)
    {
        var motion = item.Motion?.At(time - item.Start);
        var moment = TextAnimator.At(item, time);
        if (item.Fade(time) * (item.At(time).Opacity ?? item.Look.Opacity) * (motion?.Opacity ?? 1) * moment.Opacity <= 0.005)
        {
            return 0;
        }

        if (AnnotationFor(item, time) is { } annotation)
        {
            var context = new RenderContext(Blank, NoEffects, Sampling);
            if (motion is null && !moment.Moves)
            {
                annotation.Render(canvas, context);
                return 0;
            }

            var bounds = annotation.Bounds;
            canvas.Save();
            if (motion is { } state)
            {
                canvas.Concat(MotionDrawing.Matrix(state, bounds, project.Canvas));
            }

            if (moment.Moves)
            {
                canvas.Translate(0, (float)(-moment.Lift * bounds.Height));
                canvas.Scale((float)moment.Scale, (float)moment.Scale, bounds.MidX, bounds.MidY);
            }

            if (motion is { HasCrop: true } cropped)
            {
                canvas.ClipRect(MotionDrawing.Cropped(cropped, bounds), SKClipOperation.Intersect, antialias: true);
            }

            annotation.Render(canvas, context);
            canvas.Restore();
            return 0;
        }

        return DrawHidden(canvas, project, trackIndex, item, time, pass, motion, followsCamera);
    }

    /// <summary>
    /// A blurred or pixelated area: what the tracks below draw there is drawn again on its own (with a margin, so a
    /// blur doesn't darken at its edges), blurred or pixelated, and drawn over them.
    /// </summary>
    private int DrawHidden(SKCanvas canvas, VideoProject project, int trackIndex, AnnotationItem item, double time, Pass pass, MotionState? motion = null, bool followsCamera = false)
    {
        var place = item.At(time);
        var canvasArea = SKRectI.Create(project.Canvas.Width, project.Canvas.Height);
        var box = SKRect.Create((float)Math.Min(place.X, place.X + place.Width), (float)Math.Min(place.Y, place.Y + place.Height), (float)Math.Abs(place.Width), (float)Math.Abs(place.Height));
        if (motion is { } state)
        {
            // What is hidden is an upright box: it moves, grows and is cut, and doesn't turn.
            box = MotionDrawing.Matrix(state with { Rotation = 0 }, box, project.Canvas).MapRect(MotionDrawing.Cropped(state, box));
        }

        if (followsCamera)
        {
            box = pass.Camera.MapRect(box);
        }

        var area = SKRectI.Intersect(SKRectI.Round(box), canvasArea);
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
        using var paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)Math.Round(Math.Clamp(item.Fade(time) * (place.Opacity ?? item.Look.Opacity) * (motion?.Opacity ?? 1), 0, 1) * 255)) };
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
            level = Math.Min(level, MotionCurves.Ease(clip.FadeInCurve, (time - clip.Start) / clip.FadeIn));
        }

        if (clip.FadeOut > 0)
        {
            level = Math.Min(level, MotionCurves.Ease(clip.FadeOutCurve, (clip.End - time) / clip.FadeOut));
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

    private void DrawPicture(SKCanvas canvas, VideoProject project, Clip clip, MediaAsset? asset, double sourceTime, SKImage image, PictureBox? box, int pictureWidth, int pictureHeight, double opacity, double time)
    {
        var placement = clip.Placement ?? Placement.Fit;
        var size = project.Canvas;
        var (x, y, width, height) = placement.Area(size.Width, size.Height, pictureWidth, pictureHeight);
        var target = box?.Area ?? SKRect.Create((float)x, (float)y, (float)width, (float)height);
        if (target.Width <= 0 || target.Height <= 0)
        {
            return;
        }

        var source = SKRect.Create(image.Width, image.Height);
        if (box is { Cover: true })
        {
            source = CutToShape(image.Width, image.Height, target.Width / target.Height);
        }
        else if (placement.Shape == PlacementShape.Circle)
        {
            var side = Math.Min(image.Width, image.Height);
            source = SKRect.Create((image.Width - side) / 2f, (image.Height - side) / 2f, side, side);
        }

        // Its motion turns and moves it about its middle as placed; a crop cuts both the picture and the place it goes.
        var motion = clip.Motion?.At(time - clip.Start);
        var saved = canvas.SaveCount;
        if (motion is { } state)
        {
            canvas.Save();
            canvas.Concat(MotionDrawing.Matrix(state, target, size));
            opacity *= state.Opacity;
            if (state.HasCrop)
            {
                var kept = MotionDrawing.Cropped(state, target);
                var (acrossScale, downScale) = (source.Width / target.Width, source.Height / target.Height);
                source = new SKRect(
                    source.Left + ((kept.Left - target.Left) * acrossScale),
                    source.Top + ((kept.Top - target.Top) * downScale),
                    source.Right - ((target.Right - kept.Right) * acrossScale),
                    source.Bottom - ((target.Bottom - kept.Bottom) * downScale));
                target = kept;
            }
        }

        var radius = Math.Max(box?.Radius ?? Radius(placement, target), DeviceFrames.PictureRadius(placement.Frame, target));
        using var outline = box is not null || placement.CornerRadius > 0 || placement.Frame != DeviceFrame.None
            ? Rounded(target, radius)
            : Outline(placement.Shape, target);

        var alpha = (byte)Math.Round(Math.Clamp(opacity * placement.Opacity * (box?.Opacity ?? 1), 0, 1) * 255);
        var effects = clip.Effects is { IsActive: true } active ? active : null;
        SKShader Graded(SKShader picture) => effects is null ? picture : GradedShader(project, effects, picture);
        var seeThrough = effects?.Key is not null || (asset is { IsPicture: true } && StillOf(asset) is { HasTransparency: true });
        if (placement.Shadow is { Opacity: > 0 } shape && seeThrough)
        {
            // What can be seen through has a shadow of its own shape, not of its box.
            using var plain = image.ToShader(SKShaderTileMode.Decal, SKShaderTileMode.Decal, Sampling, Mapping(source, target));
            var keyed = Graded(plain);
            var reach = (float)Math.Max(0.5, shape.Blur) * 1.5f;
            var shadowColor = Color(shape.Color, SKColors.Black);
            using var fall = TransitionShaders.ShapeShadow(keyed, (float)shape.OffsetX, (float)shape.OffsetY, reach, shadowColor.WithAlpha((byte)Math.Round(Math.Clamp(shape.Opacity, 0, 1) * alpha)));
            using var falling = new SKPaint { Shader = fall };
            var margin = reach + 2 + (float)Math.Max(Math.Abs(shape.OffsetX), Math.Abs(shape.OffsetY));
            DrawFilled(canvas, SKRect.Inflate(target, margin, margin), 0, falling);
            if (!ReferenceEquals(keyed, plain))
            {
                keyed.Dispose();
            }
        }
        else if (placement.Shadow is { Opacity: > 0 } shadow)
        {
            var (body, bodyRadius) = placement.Frame == DeviceFrame.None ? (target, radius) : DeviceFrames.Body(placement.Frame, target);
            var reach = (float)((Math.Max(0.5, shadow.Blur) * 3) + 2 + Math.Max(Math.Abs(shadow.OffsetX), Math.Abs(shadow.OffsetY)));
            DrawKept(canvas, (shadow, body, bodyRadius, alpha), SKRect.Inflate(body, reach, reach), target => DrawShadow(target, shadow, body, bodyRadius, alpha));
        }

        DeviceFrames.Draw(canvas, placement.Frame, target, alpha);
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

            if (clip.Background is { Effect: not BackgroundEffect.None } background && asset is not null && Masks?.Mask(asset, clip.Stream, sourceTime) is { } mask)
            {
                using (mask)
                {
                    DrawMatted(canvas, project, background, image, mask, source, target, image.Width, image.Height, paint, radius, effects);
                }
            }
            else if (effects is not null)
            {
                using var picture = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, Sampling, Mapping(source, target));
                using var graded = GradedShader(project, effects, picture);
                paint.Shader = graded;
                paint.IsAntialias = true;
                DrawFilled(canvas, target, rounded ? radius : 0, paint);
                paint.Shader = null;
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

        canvas.RestoreToCount(saved);
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
    private void DrawMatted(SKCanvas canvas, VideoProject project, WebcamBackground background, SKImage image, SKImage mask, SKRect source, SKRect target, int frameWidth, int frameHeight, SKPaint paint, float radius, ClipEffects? effects = null)
    {
        var toTarget = Mapping(source, target);
        using var plain = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, Sampling, toTarget);
        using var graded = effects is null ? null : GradedShader(project, effects, plain);
        var picture = graded ?? plain;
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
        if (ZoomCamera.TopVideo(project, time) is { } top && project.Asset(top.Clip.AssetId) is { IsPicture: false } asset)
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

    /// <summary>A picture with its effects: the key and the colour's changes, then the look-up table.</summary>
    private SKShader GradedShader(VideoProject project, ClipEffects effects, SKShader picture)
    {
        var result = effects.HasColor || effects.Key is not null ? TransitionShaders.Effect(picture, effects) : picture;
        if (effects.LutAssetId is { } id && LutOf(project, id) is { } lut)
        {
            var looked = TransitionShaders.Lut(result, lut.Table, lut.Cells, (float)effects.LutStrength);
            if (!ReferenceEquals(result, picture))
            {
                result.Dispose();
            }

            return looked;
        }

        return ReferenceEquals(result, picture) ? TransitionShaders.Effect(picture, new ClipEffects()) : result;
    }

    /// <summary>A .cube file among the project's assets, read once; null when it can't be.</summary>
    private CubeLut? LutOf(VideoProject project, string assetId)
    {
        if (project.Asset(assetId) is not { } asset)
        {
            return null;
        }

        if (!_luts.TryGetValue(asset.Path, out var lut))
        {
            try
            {
                lut = CubeLut.Read(asset.Path);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                lut = null;
            }

            _luts[asset.Path] = lut;
        }

        return lut;
    }

    /// <summary>
    /// A rounded rectangle filled by a paint with a shader, in canvas coordinates. On the processor it is drawn in strips side
    /// by side, one per core: a shader per pixel costs too much for an export one strip at a time.
    /// </summary>
    private static void DrawFilled(SKCanvas canvas, SKRect target, float radius, SKPaint paint)
    {
        if (canvas.Context is not null)
        {
            canvas.DrawRoundRect(target, radius, radius, paint);
            return;
        }

        var matrix = canvas.TotalMatrix;
        var bounds = SKRectI.Intersect(SKRectI.Ceiling(matrix.MapRect(target), true), canvas.DeviceClipBounds);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        using var result = new SKBitmap(new SKImageInfo(bounds.Width, bounds.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var strips = Math.Clamp(Environment.ProcessorCount, 1, 32);
        Parallel.For(0, strips, strip =>
        {
            var top = bounds.Height * strip / strips;
            var bottom = bounds.Height * (strip + 1) / strips;
            using var part = new SKBitmap();
            if (bottom > top && result.ExtractSubset(part, SKRectI.Create(0, top, bounds.Width, bottom - top)))
            {
                using var strokes = new SKCanvas(part);
                using var own = paint.Clone();
                strokes.Translate(-bounds.Left, -bounds.Top - top);
                strokes.Concat(in matrix);
                strokes.DrawRoundRect(target, radius, radius, own);
            }
        });
        using var image = SKImage.FromBitmap(result);
        canvas.Save();
        canvas.ResetMatrix();
        canvas.DrawImage(image, bounds.Left, bounds.Top, new SKSamplingOptions(SKFilterMode.Nearest));
        canvas.Restore();
    }

    /// <summary>A picture clip's file, read once; null when it is gone.</summary>
    private PictureFile? StillOf(MediaAsset asset)
    {
        if (!_stills.TryGetValue(asset.Path, out var still))
        {
            still = PictureFile.TryRead(asset.Path);
            _stills[asset.Path] = still;
        }

        return still;
    }

    /// <summary>A pixel of a solid's colour, which the rest of the drawing stretches like any picture.</summary>
    private SKImage Solid(string color)
    {
        if (!_solids.TryGetValue(color, out var image))
        {
            using var surface = SKSurface.Create(new SKImageInfo(1, 1, SKColorType.Bgra8888, SKAlphaType.Premul));
            surface.Canvas.Clear(Color(color, SKColors.Black));
            image = surface.Snapshot().ToRasterImage(ensurePixelData: true);
            _solids[color] = image;
        }

        return image;
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
