using CashGear.Capture.VideoEditor.Model;

namespace CashGear.Capture.VideoEditor.Editing;

/// <summary>A part of the timeline, in seconds.</summary>
public readonly record struct TimeRange(double From, double To)
{
    public double Length => To - From;
}

/// <summary>Parts of a project as projects of their own (for exporting or transcribing a marked range).</summary>
public static class ProjectRange
{
    /// <summary>
    /// The project from <paramref name="range"/>'s start to its end, starting at 0: every item on every track (locked
    /// ones too) cut to the range and moved back by its start.
    /// </summary>
    public static VideoProject Extract(VideoProject project, TimeRange range)
    {
        ArgumentNullException.ThrowIfNull(project);
        var from = Math.Max(0, range.From);
        var to = Math.Min(project.Duration, range.To);
        if (to - from < TimelineEdits.ShortestItem)
        {
            throw new ArgumentException("The range is empty.", nameof(range));
        }

        return project with
        {
            Tracks = [.. project.Tracks.Select(t => t.WithItems(t.Items
                .Where(i => i.Overlaps(from, to))
                .Select(i => i.Slice(from, to))
                .Select(i => i with { Start = Times.Round(i.Start - from) })))],
        };
    }
}
