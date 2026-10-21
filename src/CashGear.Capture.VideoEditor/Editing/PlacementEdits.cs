using CashGear.Capture.VideoEditor.Model;
using SkiaSharp;

namespace CashGear.Capture.VideoEditor.Editing;

/// <summary>Which part of an item on the preview the pointer is on.</summary>
public enum PreviewGrip
{
    /// <summary>The item itself: dragging moves it.</summary>
    None,

    /// <summary>The bottom-right corner: dragging resizes it.</summary>
    Corner,

    /// <summary>A line's or an arrow's start.</summary>
    Start,

    /// <summary>A line's or an arrow's end.</summary>
    End,
}

/// <summary>What a pointer on the preview is over: the top item (an annotation, or a picture clip), and which grip.</summary>
public readonly record struct PreviewHit(string ItemId, PreviewGrip Grip = PreviewGrip.None)
{
    public bool OnGrip => Grip != PreviewGrip.None;
}

/// <summary>
/// Moving and resizing pictures on the preview (the webcam, a picture-in-picture), in canvas pixels. The grip is the
/// bottom-right corner of the picture's area; resizing keeps the centre, so the picture grows or shrinks around it.
/// </summary>
public static class PlacementEdits
{
    /// <summary>How close (canvas pixels, at 100%) the pointer must be to the grip.</summary>
    public const double GripSize = 24;

    public const double SmallestSize = 0.05;

    /// <summary>A clip's area on the canvas (canvas pixels).</summary>
    public static SKRect Area(VideoProject project, Clip clip)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(clip);
        var stream = project.Asset(clip.AssetId)?.Stream(clip.Stream);
        var (x, y, width, height) = (clip.Placement ?? Placement.Fit).Area(project.Canvas.Width, project.Canvas.Height, stream?.Width ?? project.Canvas.Width, stream?.Height ?? project.Canvas.Height);
        return SKRect.Create((float)x, (float)y, (float)width, (float)height);
    }

    /// <summary>The top annotation or picture clip at <paramref name="point"/> (canvas pixels) at <paramref name="time"/>, or null.</summary>
    /// <param name="grip">The grip's size in canvas pixels (larger when the preview is shown small).</param>
    public static PreviewHit? HitTest(VideoProject project, double time, SKPoint point, double grip = GripSize)
    {
        ArgumentNullException.ThrowIfNull(project);
        foreach (var track in project.Tracks.Reverse())
        {
            if (track.Hidden || track.Locked)
            {
                continue;
            }

            if (track.Kind == TrackKind.Annotation)
            {
                foreach (var item in track.Items.OfType<AnnotationItem>().Where(a => a.Covers(time)).Reverse())
                {
                    if (AnnotationEdits.HitTest(item, time, point, grip) is { } on)
                    {
                        return new PreviewHit(item.Id, on);
                    }
                }

                continue;
            }

            if (!track.HasPictures)
            {
                continue;
            }

            foreach (var clip in track.Clips.Where(c => c.Covers(time)).Reverse())
            {
                // The grip never takes more than a quarter of a small picture, so the rest still moves it.
                var area = Area(project, clip);
                var reach = Math.Min(grip, Math.Min(area.Width, area.Height) / 4);
                var onGrip = Math.Abs(point.X - area.Right) <= reach && Math.Abs(point.Y - area.Bottom) <= reach;
                if (onGrip || area.Contains(point))
                {
                    return new PreviewHit(clip.Id, onGrip ? PreviewGrip.Corner : PreviewGrip.None);
                }
            }
        }

        return null;
    }

    /// <summary>The placement moved by (<paramref name="dx"/>, <paramref name="dy"/>) canvas pixels, its centre kept on the canvas.</summary>
    public static Placement Move(Placement placement, ProjectCanvas canvas, double dx, double dy)
    {
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(canvas);
        return placement with
        {
            CenterX = Math.Round(Math.Clamp(placement.CenterX + (dx / canvas.Width), 0, 1), 4),
            CenterY = Math.Round(Math.Clamp(placement.CenterY + (dy / canvas.Height), 0, 1), 4),
        };
    }

    /// <summary>
    /// The placement resized so its bottom-right corner follows the grip dragged to <paramref name="point"/>, the
    /// centre kept (the size is the larger of the two directions' wishes, so the shape is kept).
    /// </summary>
    public static Placement Resize(Placement placement, SKRect area, SKPoint point)
    {
        ArgumentNullException.ThrowIfNull(placement);
        var center = new SKPoint(area.MidX, area.MidY);
        var halfWidth = Math.Max(1, area.Width / 2);
        var halfHeight = Math.Max(1, area.Height / 2);
        var scale = Math.Max((point.X - center.X) / halfWidth, (point.Y - center.Y) / halfHeight);
        return placement with { Size = Math.Round(Math.Clamp(placement.Size * scale, SmallestSize, 2), 4) };
    }

    /// <summary>
    /// A zoom whose frame on the preview (<paramref name="frame"/>, canvas pixels, fully zoomed in) was dragged by
    /// (<paramref name="dx"/>, <paramref name="dy"/>): moved, after which it stays there rather than following the
    /// pointer; or by its corner made larger (zooming out) or smaller (zooming in), its top-left corner kept. Either
    /// way it becomes the user's own (Auto-Zoom no longer replaces it).
    /// </summary>
    public static ZoomItem DragZoom(ZoomItem zoom, ProjectCanvas canvas, SKRect frame, PreviewGrip grip, double dx, double dy)
    {
        ArgumentNullException.ThrowIfNull(zoom);
        ArgumentNullException.ThrowIfNull(canvas);
        if (grip == PreviewGrip.Corner)
        {
            var width = frame.Width + Math.Max(dx, dy * canvas.Width / canvas.Height);
            var scale = Math.Clamp(canvas.Width / Math.Max(1, width), ZoomItem.SmallestScale, ZoomItem.LargestScale);
            var centerX = frame.Left + (canvas.Width / scale / 2);
            var centerY = frame.Top + (canvas.Height / scale / 2);
            return zoom with
            {
                Scale = Math.Round(scale, 2),
                CenterX = Math.Round(Math.Clamp(centerX / canvas.Width, 0, 1), 4),
                CenterY = Math.Round(Math.Clamp(centerY / canvas.Height, 0, 1), 4),
                Auto = false,
            };
        }

        return zoom with
        {
            Focus = ZoomFocus.Fixed,
            CenterX = Math.Round(Math.Clamp((frame.MidX + dx) / canvas.Width, 0, 1), 4),
            CenterY = Math.Round(Math.Clamp((frame.MidY + dy) / canvas.Height, 0, 1), 4),
            Auto = false,
        };
    }
}

/// <summary>Track edits that aren't about time.</summary>
public static class TrackEdits
{
    /// <summary>The project without the track (and without the media nothing else uses).</summary>
    public static VideoProject Remove(VideoProject project, string trackId)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.Track(trackId) is null)
        {
            return project;
        }

        var tracks = project.Tracks.Where(t => t.Id != trackId).ToList();
        var used = tracks.SelectMany(t => t.Clips).Select(c => c.AssetId).ToHashSet();
        return project with { Tracks = tracks, Assets = [.. project.Assets.Where(a => used.Contains(a.Id))] };
    }
}
