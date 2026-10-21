using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Rendering;
using SkiaSharp;

namespace CashGear.Capture.VideoEditor.Editing;

/// <summary>Adding annotations and changing their place on the preview.</summary>
public static class AnnotationEdits
{
    /// <summary>Lines are picked this far from them (canvas pixels, at least).</summary>
    public const double LineReach = 10;

    /// <summary>
    /// A new annotation from <paramref name="at"/> for <paramref name="length"/> seconds, in the middle of the canvas
    /// with a look that suits its kind, on the first annotation track with room for it (a new one on top if none has).
    /// </summary>
    public static (VideoProject Project, string Id) Add(VideoProject project, AnnotationShape shape, double at, double length = AnnotationItem.DefaultLength)
    {
        ArgumentNullException.ThrowIfNull(project);
        var w = project.Canvas.Width;
        var h = project.Canvas.Height;
        var line = Math.Max(2, Math.Round(h / 120.0));
        var (keyframe, look, text) = shape switch
        {
            AnnotationShape.Text => (new AnnotationKeyframe(0, w * 0.38, h * 0.42, 0, 0), new AnnotationLook(Color: "#FFFFFF", Fill: "#B0141414", FontSize: Math.Round(h / 15.0)), "Text"),
            AnnotationShape.Arrow or AnnotationShape.Line => (new AnnotationKeyframe(0, w * 0.36, h * 0.62, w * 0.14, -h * 0.18), new AnnotationLook(StrokeWidth: line), ""),
            AnnotationShape.Highlight => (new AnnotationKeyframe(0, w * 0.35, h * 0.46, w * 0.3, h * 0.08), new AnnotationLook(Color: "#FFF100"), ""),
            AnnotationShape.Blur or AnnotationShape.Pixelate => (new AnnotationKeyframe(0, w * 0.375, h * 0.425, w * 0.25, h * 0.15), new AnnotationLook(Strength: Math.Max(4, Math.Round(h / 50.0))), ""),
            _ => (new AnnotationKeyframe(0, w * 0.35, h * 0.35, w * 0.3, h * 0.3), new AnnotationLook(StrokeWidth: line), ""),
        };
        var item = new AnnotationItem
        {
            Id = Ids.New(),
            Start = Times.Round(Math.Max(0, at)),
            Length = Times.Round(Math.Max(TimelineEdits.ShortestItem, length)),
            Shape = shape,
            Text = text,
            Look = look,
            Keyframes = [keyframe],
            FadeIn = 0.2,
            FadeOut = 0.2,
        };

        var free = project.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Annotation && !t.Locked && !t.Items.Any(i => i.Overlaps(item.Start, item.End)));
        if (free is not null)
        {
            return (project.WithTrack(free.WithItems([.. free.Items, item])), item.Id);
        }

        var count = project.Tracks.Count(t => t.Kind == TrackKind.Annotation);
        var track = new Track(Ids.New(), TrackKind.Annotation, count == 0 ? "Annotations" : $"Annotations {count + 1}", [item]);
        return (project with { Tracks = [.. project.Tracks, track] }, item.Id);
    }

    /// <summary>Whether <paramref name="point"/> is on the annotation at <paramref name="time"/>, and on which grip.</summary>
    public static PreviewGrip? HitTest(AnnotationItem item, double time, SKPoint point, double grip)
    {
        ArgumentNullException.ThrowIfNull(item);
        var place = item.At(time);
        if (item.IsLine)
        {
            var start = new SKPoint((float)place.X, (float)place.Y);
            var end = new SKPoint((float)(place.X + place.Width), (float)(place.Y + place.Height));
            if (SKPoint.Distance(point, end) <= grip)
            {
                return PreviewGrip.End;
            }

            if (SKPoint.Distance(point, start) <= grip)
            {
                return PreviewGrip.Start;
            }

            return Distance(point, start, end) <= Math.Max(LineReach, item.Look.StrokeWidth) ? PreviewGrip.None : null;
        }

        var bounds = FrameCompositor.Bounds(item, time);
        if (item.Shape != AnnotationShape.Text)
        {
            var reach = Math.Min(grip, Math.Min(bounds.Width, bounds.Height) / 4);
            if (Math.Abs(point.X - bounds.Right) <= reach && Math.Abs(point.Y - bounds.Bottom) <= reach)
            {
                return PreviewGrip.Corner;
            }
        }

        return bounds.Contains(point) ? PreviewGrip.None : null;
    }

    /// <summary>
    /// The annotation dragged on the preview at <paramref name="time"/> from <paramref name="from"/> to
    /// <paramref name="to"/> (canvas pixels) by <paramref name="grip"/>: moved, resized, or one end of a line moved.
    /// The change goes in a keyframe at that time (see <see cref="AnnotationItem.PlacedAt"/>).
    /// </summary>
    public static AnnotationItem Drag(AnnotationItem item, double time, PreviewGrip grip, SKPoint from, SKPoint to, int fps)
    {
        ArgumentNullException.ThrowIfNull(item);
        var place = item.At(time);
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var moved = grip switch
        {
            PreviewGrip.Corner => place with { Width = Math.Max(8, place.Width + dx), Height = Math.Max(8, place.Height + dy) },
            PreviewGrip.Start => place with { X = place.X + dx, Y = place.Y + dy, Width = place.Width - dx, Height = place.Height - dy },
            PreviewGrip.End => place with { Width = place.Width + dx, Height = place.Height + dy },
            _ => place with { X = place.X + dx, Y = place.Y + dy },
        };
        return item.PlacedAt(time, Rounded(moved), fps);
    }

    private static AnnotationKeyframe Rounded(AnnotationKeyframe k) =>
        k with { X = Math.Round(k.X, 1), Y = Math.Round(k.Y, 1), Width = Math.Round(k.Width, 1), Height = Math.Round(k.Height, 1) };

    /// <summary>The distance from <paramref name="point"/> to the segment from <paramref name="a"/> to <paramref name="b"/>.</summary>
    private static double Distance(SKPoint point, SKPoint a, SKPoint b)
    {
        var ab = b - a;
        var length = (ab.X * ab.X) + (ab.Y * ab.Y);
        var t = length == 0 ? 0 : Math.Clamp((((point.X - a.X) * ab.X) + ((point.Y - a.Y) * ab.Y)) / length, 0, 1);
        var nearest = new SKPoint(a.X + (float)(ab.X * t), a.Y + (float)(ab.Y * t));
        return SKPoint.Distance(point, nearest);
    }
}
