using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Rendering;
using CashGear.Capture.VideoEditor.Resources;

namespace CashGear.Capture.VideoEditor.Editing;

/// <summary>How Auto-Zoom finds and shapes its zooms.</summary>
/// <param name="MaxScale">The most it zooms in.</param>
/// <param name="MinScale">Clicks and keys spread wider than this zoom shows get no zoom.</param>
/// <param name="Gap">Clicks and keys further apart in time than this (seconds) are different zooms.</param>
/// <param name="Lead">A zoom starts this long before its first click or key.</param>
/// <param name="Tail">And ends this long after its last.</param>
/// <param name="MinLength">The shortest zoom.</param>
public sealed record AutoZoomOptions(
    double MaxScale = ZoomItem.DefaultScale,
    double MinScale = ZoomItem.SmallestScale,
    double Gap = 2.5,
    double Lead = 0.8,
    double Tail = 1.5,
    double MinLength = 2,
    double Ease = ZoomItem.DefaultEase);

/// <summary>A click or a key on the timeline, where it happened on the canvas.</summary>
public sealed record InputEvent(double Time, double X, double Y, bool Typing);

/// <summary>
/// Zooms made from recordings' metadata: clicks and typing close together in time and place become a zoom around
/// them that follows the pointer (Screen Studio's auto-zoom). Events are read at each screen clip's source time, so
/// cuts and speed changes are taken into account.
/// </summary>
public static class AutoZoom
{
    /// <summary>Keys are placed at the last click this recent; else where the pointer is.</summary>
    private const double KeysFollowClick = 30;

    /// <summary>Room around the clicks and keys inside the view, as a part of the fully zoomed view on each side.</summary>
    private const double Margin = 0.15;

    /// <summary>The clicks and keys under the screen clips (on video tracks that are shown), in time order.</summary>
    public static IReadOnlyList<InputEvent> Events(VideoProject project, MetadataOverlays overlays)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(overlays);
        var events = new List<InputEvent>();
        foreach (var track in project.Tracks.Where(t => t.Kind == TrackKind.Video && !t.Hidden))
        {
            foreach (var clip in track.Clips.Where(c => !c.IsFreeze))
            {
                if (project.Asset(clip.AssetId) is not { } asset || ProjectFactory.MainVideo(asset)?.Index != clip.Stream || overlays.Metadata(asset) is not { } metadata)
                {
                    continue;
                }

                var part = overlays.PartStart(asset);
                var stream = asset.Stream(clip.Stream);
                double width = stream is { Width: > 0 } ? stream.Width : metadata.Area.Width;
                double height = stream is { Height: > 0 } ? stream.Height : metadata.Area.Height;
                var area = PlacementEdits.Area(project, clip);
                (double X, double Y) OnCanvas(double x, double y) => (area.Left + (x * area.Width / width), area.Top + (y * area.Height / height));
                bool Inside(double source) => source >= clip.SourceIn - Times.Epsilon && source < clip.SourceOut - Times.Epsilon;

                foreach (var click in metadata.Clicks)
                {
                    var source = click.T - part;
                    if (Inside(source))
                    {
                        var (x, y) = OnCanvas(click.X, click.Y);
                        events.Add(new InputEvent(clip.TimelineAt(source), x, y, Typing: false));
                    }
                }

                foreach (var key in metadata.Keys)
                {
                    var source = key.T - part;
                    if (!Inside(source))
                    {
                        continue;
                    }

                    var click = metadata.Clicks.LastOrDefault(c => c.T <= key.T && key.T - c.T <= KeysFollowClick);
                    var at = click is not null ? ((double)click.X, (double)click.Y) : overlays.Pointer(asset, source);
                    if (at is { } place)
                    {
                        var (x, y) = OnCanvas(place.Item1, place.Item2);
                        events.Add(new InputEvent(clip.TimelineAt(source), x, y, Typing: true));
                    }
                }
            }
        }

        return [.. events.OrderBy(e => e.Time)];
    }

    /// <summary>The zooms Auto-Zoom would make (marked <see cref="ZoomItem.Auto"/>), in time order, not overlapping.</summary>
    public static IReadOnlyList<ZoomItem> Plan(VideoProject project, MetadataOverlays overlays, AutoZoomOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        options ??= new AutoZoomOptions();
        var canvas = project.Canvas;
        var events = Events(project, overlays);

        // Clusters: close in time, and all inside one view zoomed in at least MinScale.
        var clusters = new List<List<InputEvent>>();
        foreach (var e in events)
        {
            if (clusters.Count > 0 && clusters[^1] is var last && e.Time - last[^1].Time <= options.Gap && ScaleFor([.. last, e], canvas, options) >= options.MinScale)
            {
                last.Add(e);
            }
            else
            {
                clusters.Add([e]);
            }
        }

        var zooms = new List<ZoomItem>();
        var duration = project.Duration;
        for (var i = 0; i < clusters.Count; i++)
        {
            var cluster = clusters[i];
            var scale = ScaleFor(cluster, canvas, options);
            if (scale < options.MinScale)
            {
                continue;
            }

            var start = Math.Max(0, cluster[0].Time - options.Lead);
            var end = Math.Min(duration, Math.Max(cluster[^1].Time + options.Tail, start + options.MinLength));
            if (zooms.Count > 0 && start < zooms[^1].End)
            {
                // Too wide to share a view but close in time: they meet halfway between their events, and the camera pans.
                var previous = zooms[^1];
                var meet = Times.Round((clusters[i - 1][^1].Time + cluster[0].Time) / 2);
                zooms[^1] = previous with { Length = Times.Round(Math.Max(TimelineEdits.ShortestItem, meet - previous.Start)) };
                start = zooms[^1].End;
            }

            if (end - start < TimelineEdits.ShortestItem)
            {
                continue;
            }

            var (left, top, right, bottom) = Bounds(cluster);
            zooms.Add(new ZoomItem
            {
                Id = Ids.New(),
                Start = Times.Round(start),
                Length = Times.Round(end - start),
                Scale = Math.Round(scale * 20) / 20,
                Focus = ZoomFocus.FollowPointer,
                CenterX = Math.Round((left + right) / 2 / canvas.Width, 4),
                CenterY = Math.Round((top + bottom) / 2 / canvas.Height, 4),
                EaseIn = options.Ease,
                EaseOut = options.Ease,
                Auto = true,
            });
        }

        return zooms;
    }

    /// <summary>
    /// Replaces the zooms Auto-Zoom made before (on unlocked tracks) with new ones, on a "Zoom" effect track,
    /// leaving out any that would overlap a zoom made by hand.
    /// </summary>
    public static (VideoProject Project, int Count) Apply(VideoProject project, MetadataOverlays overlays, AutoZoomOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        var tracks = new List<Track>();
        foreach (var track in project.Tracks)
        {
            var left = track.Kind == TrackKind.Effect && !track.Locked ? [.. track.Items.Where(i => i is not ZoomItem { Auto: true })] : track.Items;
            if (left.Count == 0 && track.Items.Count > 0)
            {
                continue; // it held only Auto-Zoom's zooms
            }

            tracks.Add(left.Count == track.Items.Count ? track : track.WithItems(left));
        }

        var cleared = project with { Tracks = tracks };
        var kept = cleared.Tracks.Where(t => t.Kind == TrackKind.Effect).SelectMany(t => t.Items.OfType<ZoomItem>()).ToList();
        var zooms = Plan(cleared, overlays, options).Where(z => !kept.Any(k => k.Overlaps(z.Start, z.End))).ToList();
        var result = cleared;
        foreach (var zoom in zooms)
        {
            result = AddZoom(result, zoom);
        }

        return (result, zooms.Count);
    }

    /// <summary>Puts <paramref name="zoom"/> on the first unlocked effect track holding only zooms with room for it, or a new "Zoom" track on top.</summary>
    public static VideoProject AddZoom(VideoProject project, ZoomItem zoom)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(zoom);
        var free = project.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Effect && !t.Locked && t.Items.Count > 0 && t.Items.All(i => i is ZoomItem) && !t.Items.Any(i => i.Overlaps(zoom.Start, zoom.End)));
        if (free is not null)
        {
            return project.WithTrack(free.WithItems([.. free.Items, zoom]));
        }

        var count = project.Tracks.Count(t => t.Kind == TrackKind.Effect && t.Items.Count > 0 && t.Items.All(i => i is ZoomItem));
        return project with { Tracks = [.. project.Tracks, new Track(Ids.New(), TrackKind.Effect, count == 0 ? VideoEditorStrings.TrackZoom : VideoEditorStrings.TrackZoomNumbered(count + 1), [zoom])] };
    }

    /// <summary>The largest zoom (up to the maximum) whose view holds the events with a margin around them.</summary>
    private static double ScaleFor(IReadOnlyList<InputEvent> events, ProjectCanvas canvas, AutoZoomOptions options)
    {
        var (left, top, right, bottom) = Bounds(events);
        var marginX = canvas.Width / options.MaxScale * Margin;
        var marginY = canvas.Height / options.MaxScale * Margin;
        var width = (right - left) + (2 * marginX);
        var height = (bottom - top) + (2 * marginY);
        return Math.Min(options.MaxScale, Math.Min(canvas.Width / width, canvas.Height / height));
    }

    private static (double Left, double Top, double Right, double Bottom) Bounds(IReadOnlyList<InputEvent> events) =>
        (events.Min(e => e.X), events.Min(e => e.Y), events.Max(e => e.X), events.Max(e => e.Y));
}
