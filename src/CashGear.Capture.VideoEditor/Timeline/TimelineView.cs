using CashGear.Capture.VideoEditor.Model;

namespace CashGear.Capture.VideoEditor.Timeline;

public enum TimelineHitKind
{
    None,

    /// <summary>The time ruler along the top.</summary>
    Ruler,

    /// <summary>A track's empty part.</summary>
    Track,

    Item,

    /// <summary>An item's start edge (to trim).</summary>
    ItemStart,

    /// <summary>An item's end edge.</summary>
    ItemEnd,
}

/// <summary>What is at a point of the timeline, and the time there.</summary>
public readonly record struct TimelineHit(TimelineHitKind Kind, double Time, string? TrackId = null, string? ItemId = null);

/// <summary>A track's row: its place from the top of the rows (top layer first, sound at the bottom).</summary>
public sealed record TrackRow(Track Track, double Top, double Height);

/// <summary>
/// The timeline's geometry in view pixels: the ruler on top, then a row per track, the top picture layer first and
/// the sound tracks at the bottom; time runs left to right at <see cref="PixelsPerSecond"/> from
/// <see cref="Scroll"/> seconds at the left edge. Hit-testing and snapping work here, without WPF, so they can be
/// tested.
/// </summary>
public sealed class TimelineView
{
    public const double RulerHeight = 26;
    public const double PictureRowHeight = 54;
    public const double SoundRowHeight = 44;
    public const double OtherRowHeight = 32;

    /// <summary>How close (pixels) an edge must be to grab it, and a time to snap to it.</summary>
    public const double EdgeGrab = 6;
    public const double SnapDistance = 8;

    public const double MinPixelsPerSecond = 0.5;
    public const double MaxPixelsPerSecond = 2000;

    private double _pixelsPerSecond = 40;
    private double _scroll;

    public event EventHandler? Changed;

    public double PixelsPerSecond
    {
        get => _pixelsPerSecond;
        set
        {
            var clamped = Math.Clamp(value, MinPixelsPerSecond, MaxPixelsPerSecond);
            if (clamped != _pixelsPerSecond)
            {
                _pixelsPerSecond = clamped;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>The time at the left edge.</summary>
    public double Scroll
    {
        get => _scroll;
        set
        {
            var clamped = Math.Max(0, value);
            if (clamped != _scroll)
            {
                _scroll = clamped;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>How far the rows are scrolled up, in pixels.</summary>
    public double ScrollRows { get; set; }

    public double Width { get; set; } = 800;

    public double Height { get; set; } = 240;

    public double X(double time) => (time - Scroll) * PixelsPerSecond;

    public double Time(double x) => Scroll + (x / PixelsPerSecond);

    /// <summary>The rows from the top: picture, annotation and effect tracks from the top layer down, then the sound tracks.</summary>
    public static IReadOnlyList<TrackRow> Rows(VideoProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var rows = new List<TrackRow>();
        var top = 0.0;
        foreach (var track in project.Tracks.Where(t => !t.HasSound).Reverse().Concat(project.Tracks.Where(t => t.HasSound)))
        {
            var height = track.HasPictures ? PictureRowHeight : track.HasSound ? SoundRowHeight : OtherRowHeight;
            rows.Add(new TrackRow(track, top, height));
            top += height;
        }

        return rows;
    }

    /// <summary>The rows' total height.</summary>
    public static double RowsHeight(VideoProject project) => Rows(project).Sum(r => r.Height);

    /// <summary>The row at <paramref name="y"/> (view pixels), or null.</summary>
    public TrackRow? RowAt(VideoProject project, double y)
    {
        var inRows = y - RulerHeight + ScrollRows;
        return y < RulerHeight ? null : Rows(project).FirstOrDefault(r => inRows >= r.Top && inRows < r.Top + r.Height);
    }

    /// <summary>A row's top in view pixels.</summary>
    public double RowTop(TrackRow row) => RulerHeight + row.Top - ScrollRows;

    public TimelineHit HitTest(VideoProject project, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(project);
        var time = Math.Max(0, Time(x));
        if (y < RulerHeight)
        {
            return new TimelineHit(TimelineHitKind.Ruler, time);
        }

        if (RowAt(project, y) is not { } row)
        {
            return new TimelineHit(TimelineHitKind.None, time);
        }

        // The item under the pointer first (later items are drawn on top: a crossfade's second clip), then an edge
        // just outside an item.
        TimelineHit? near = null;
        foreach (var item in row.Track.Items.Reverse())
        {
            var left = X(item.Start);
            var right = X(item.End);
            if (x < left - EdgeGrab || x > right + EdgeGrab)
            {
                continue;
            }

            var narrow = right - left < EdgeGrab * 3;
            var edge = narrow ? (TimelineHitKind?)null
                : Math.Abs(x - left) <= EdgeGrab ? TimelineHitKind.ItemStart
                : Math.Abs(x - right) <= EdgeGrab ? TimelineHitKind.ItemEnd
                : null;
            if (x >= left && x <= right)
            {
                return new TimelineHit(edge ?? TimelineHitKind.Item, time, row.Track.Id, item.Id);
            }

            if (edge is { } outside)
            {
                near ??= new TimelineHit(outside, time, row.Track.Id, item.Id);
            }
        }

        if (near is { } edgeHit)
        {
            return edgeHit;
        }

        return new TimelineHit(TimelineHitKind.Track, time, row.Track.Id);
    }

    /// <summary>
    /// <paramref name="time"/> pulled to the nearest item edge, the playhead or 0 within <see cref="SnapDistance"/>
    /// pixels (items in <paramref name="ignore"/>, the ones being dragged, don't count).
    /// </summary>
    public double Snap(VideoProject project, double time, double playhead, IReadOnlySet<string>? ignore = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        var best = time;
        var bestDistance = SnapDistance / PixelsPerSecond;
        void Consider(double candidate)
        {
            var distance = Math.Abs(candidate - time);
            if (distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        Consider(0);
        Consider(playhead);
        foreach (var item in project.Tracks.SelectMany(t => t.Items))
        {
            if (ignore?.Contains(item.Id) == true)
            {
                continue;
            }

            Consider(item.Start);
            Consider(item.End);
        }

        return best;
    }

    /// <summary>Zooms by <paramref name="factor"/> keeping the time under <paramref name="x"/> where it is.</summary>
    public void ZoomAt(double x, double factor)
    {
        var time = Time(x);
        PixelsPerSecond *= factor;
        Scroll = time - (x / PixelsPerSecond);
    }

    /// <summary>Shows the whole of <paramref name="duration"/> (with a little room after it).</summary>
    public void Fit(double duration)
    {
        PixelsPerSecond = Math.Max(1, Width - 20) / Math.Max(1, duration * 1.05);
        Scroll = 0;
    }

    /// <summary>Scrolls just enough to show <paramref name="time"/> (while playing, a page at a time).</summary>
    public void Reveal(double time)
    {
        var x = X(time);
        if (x < 0)
        {
            Scroll = time;
        }
        else if (x > Width - 10)
        {
            Scroll = time - ((Width * 0.1) / PixelsPerSecond);
        }
    }

    /// <summary>
    /// Scrolls just enough to show the time from <paramref name="start"/> to <paramref name="end"/>, with a little
    /// room round it (its start, when it is wider than the view).
    /// </summary>
    public void RevealSpan(double start, double end)
    {
        const double room = 10;
        if (X(start) < 0)
        {
            Scroll = start - (room / PixelsPerSecond);
        }
        else if (X(end) > Width - room)
        {
            Scroll = Math.Min(start - (room / PixelsPerSecond), end - ((Width - room) / PixelsPerSecond));
        }
    }

    /// <summary>Scrolls the rows just enough to show <paramref name="row"/> whole (its top, when they are shorter than it).</summary>
    public void RevealRow(TrackRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var shown = Math.Max(0, Height - RulerHeight);
        if (row.Top < ScrollRows)
        {
            ScrollRows = row.Top;
        }
        else if (row.Top + row.Height > ScrollRows + shown)
        {
            ScrollRows = Math.Min(row.Top, row.Top + row.Height - shown);
        }
    }

    /// <summary>A step between ruler ticks that leaves room for their labels: 1, 2, 5, 10 … frames or seconds.</summary>
    public double TickStep(int fps)
    {
        double[] steps = [1.0 / fps, 2.0 / fps, 5.0 / fps, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800, 3600];
        foreach (var step in steps)
        {
            if (step * PixelsPerSecond >= 70)
            {
                return step;
            }
        }

        return steps[^1];
    }
}
