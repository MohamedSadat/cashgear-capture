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

    public FrameCompositor(IFrameProvider frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        _frames = frames;
    }

    public CompositionResult Render(SKCanvas canvas, VideoProject project, double time)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(project);
        var size = project.Canvas;
        canvas.Clear(Color(size.Background, SKColors.Black));
        var missing = 0;
        foreach (var track in project.Tracks)
        {
            if (!track.HasPictures || track.Hidden)
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

        return new CompositionResult(missing);
    }

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
