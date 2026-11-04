using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Rendering;
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

    /// <summary>A clip's area on the canvas at <paramref name="time"/>, where the layout track puts it.</summary>
    public static SKRect Area(VideoProject project, Clip clip, double time)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(clip);
        return (clip.Motion is not null || project.Tracks.Any(t => t.Kind == TrackKind.Layout)) && project.Find(clip.Id)?.Track is { } track ? Area(project, track, clip, time) : Area(project, clip);
    }

    /// <summary>A clip's area on the canvas at <paramref name="time"/>, where the layout track puts it.</summary>
    public static SKRect Area(VideoProject project, Track track, Clip clip, double time)
    {
        var area = LayoutEngine.Box(project, track, clip, time) is { } box ? box.Area : Area(project, clip);
        return clip.Motion is { } motion ? MotionDrawing.Bounds(motion.At(time - clip.Start), area, project.Canvas) : area;
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
                foreach (var item in track.Items.Where(a => a.Covers(time)).Reverse())
                {
                    if (item is AnnotationItem annotation && AnnotationEdits.HitTest(annotation, time, point, grip, project.Canvas) is { } on)
                    {
                        return new PreviewHit(item.Id, on);
                    }

                    if (item is TemplateItem template && TemplateEdits.Bounds(template, project, time).Contains(point))
                    {
                        return new PreviewHit(item.Id);
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
                if (LayoutEngine.Box(project, track, clip, time) is { Opacity: <= 0 })
                {
                    continue;
                }

                // The grip never takes more than a quarter of a small picture, so the rest still moves it.
                var area = Area(project, track, clip, time);
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

    /// <summary>
    /// The placement moved by (<paramref name="dx"/>, <paramref name="dy"/>) canvas pixels, its centre kept on the
    /// canvas (to a millionth of it, so a pixel's move by the keys is a pixel on any canvas).
    /// </summary>
    public static Placement Move(Placement placement, ProjectCanvas canvas, double dx, double dy)
    {
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(canvas);
        return placement with
        {
            CenterX = Math.Round(Math.Clamp(placement.CenterX + (dx / canvas.Width), 0, 1), 6),
            CenterY = Math.Round(Math.Clamp(placement.CenterY + (dy / canvas.Height), 0, 1), 6),
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
            CenterX = Math.Round(Math.Clamp((frame.MidX + dx) / canvas.Width, 0, 1), 6),
            CenterY = Math.Round(Math.Clamp((frame.MidY + dy) / canvas.Height, 0, 1), 6),
            Auto = false,
        };
    }
}

/// <summary>Track edits that aren't about time.</summary>
public static class TrackEdits
{
    /// <summary>
    /// Whether <paramref name="track"/> can hold <paramref name="item"/>: a clip's pictures go on picture tracks
    /// (the screen's or a webcam's) and its sound on sound tracks; annotations, captions, zooms and metadata effects
    /// each on their own kind of track (zooms and effects never share one).
    /// </summary>
    public static bool Accepts(VideoProject project, Track track, TimelineItem item)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(track);
        return item switch
        {
            Clip clip => IsSound(project, clip) ? track.HasSound : track.HasPictures,
            AnnotationItem or TemplateItem => track.Kind == TrackKind.Annotation,
            CaptionItem => track.Kind == TrackKind.Caption,
            LayoutItem => track.Kind == TrackKind.Layout,
            ZoomItem => track.Kind == TrackKind.Effect && track.Items.All(i => i is ZoomItem),
            EffectItem => track.Kind == TrackKind.Effect && !track.Items.Any(i => i is ZoomItem),
            _ => false,
        };
    }

    /// <summary>
    /// The project with item <paramref name="id"/> moved to track <paramref name="trackId"/> at the same time (its
    /// linked clips stay where they are). Unchanged when either track is locked, the track doesn't take it
    /// (<see cref="Accepts"/>) or something there is in its way.
    /// </summary>
    public static VideoProject MoveItem(VideoProject project, string id, string trackId)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.Find(id) is not { } found || project.Track(trackId) is not { } target || found.Track.Id == target.Id
            || found.Track.Locked || target.Locked || !Accepts(project, target, found.Item)
            || target.Items.Any(i => i.Overlaps(found.Item.Start, found.Item.End)))
        {
            return project;
        }

        return project
            .WithTrack(found.Track.WithItems(found.Track.Items.Where(i => i.Id != id)))
            .WithTrack(target.WithItems([.. target.Items, found.Item]));
    }

    /// <summary>
    /// A track one row up (<paramref name="direction"/> -1) or down (+1) on the timeline, among those of its own sort:
    /// sound tracks among sound tracks, the others among the others, where the higher a row the later it is drawn.
    /// </summary>
    public static VideoProject Reorder(VideoProject project, string trackId, int direction)
    {
        ArgumentNullException.ThrowIfNull(project);
        var rows = Timeline.TimelineView.Rows(project).Select(r => r.Track).ToList();
        var at = rows.FindIndex(t => t.Id == trackId);
        var to = at + Math.Sign(direction);
        return at < 0 || to < 0 || to >= rows.Count || rows[at].HasSound != rows[to].HasSound || direction == 0 ? project : MoveTo(project, trackId, rows[to].Id);
    }

    /// <summary>A track dragged to another's row (of its own sort), the ones between moving over.</summary>
    public static VideoProject MoveTo(VideoProject project, string trackId, string toTrackId)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (trackId == toTrackId || project.Track(trackId) is not { } moving || project.Track(toTrackId) is not { } target || moving.HasSound != target.HasSound)
        {
            return project;
        }

        var tracks = project.Tracks.ToList();
        var from = tracks.FindIndex(t => t.Id == trackId);
        var to = tracks.FindIndex(t => t.Id == toTrackId);
        tracks.RemoveAt(from);
        tracks.Insert(to, moving);
        return project with { Tracks = tracks };
    }

    /// <summary>Whether a clip plays sound (its stream's kind; the kind of its track when the media isn't known).</summary>
    private static bool IsSound(VideoProject project, Clip clip) =>
        project.Asset(clip.AssetId)?.Stream(clip.Stream)?.Kind is { } kind ? kind == StreamKind.Audio : project.Find(clip.Id)?.Track.HasSound == true;

    /// <summary>The project without the track (and without the media nothing else uses).</summary>
    public static VideoProject Remove(VideoProject project, string trackId)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.Track(trackId) is null)
        {
            return project;
        }

        // Media in the panel that was never on the timeline stays; what only this track used goes with it.
        var tracks = project.Tracks.Where(t => t.Id != trackId).ToList();
        var gone = project.Track(trackId)!.Clips.Select(c => c.AssetId).ToHashSet();
        var after = project with { Tracks = tracks };
        return after with { Assets = [.. project.Assets.Where(a => !gone.Contains(a.Id) || MediaEdits.IsUsed(after, a.Id))] };
    }
}
