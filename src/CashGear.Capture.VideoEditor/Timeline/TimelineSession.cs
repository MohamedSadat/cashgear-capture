using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Resources;

namespace CashGear.Capture.VideoEditor.Timeline;

public enum TimelineCursor
{
    Arrow,
    Move,
    Trim,
    Scrub,
}

/// <summary>
/// What the mouse does on the timeline, without WPF: a click on the ruler (or a drag along it) moves the playhead; a
/// click selects an item and a drag moves it (with the clips linked to it, unless Alt is held) or trims it by an edge;
/// a drag over an empty part of a track, or Shift with a drag on the ruler, marks a range. Moves and trims snap to
/// item edges, the playhead and 0 (Alt: no snapping), and one drag is one undo step.
/// </summary>
public sealed class TimelineSession(ProjectHistory history, TimelineView view)
{
    private enum Drag
    {
        None,
        Scrub,
        Move,
        TrimStart,
        TrimEnd,
        Range,
    }

    private Drag _drag;
    private VideoProject? _base;
    private TimelineItem? _item;
    private double _grabOffset;
    private double _anchor;
    private int _drags;
    private double _playhead;

    /// <summary>The selection, the range or the playhead changed.</summary>
    public event EventHandler? Changed;

    /// <summary>The user moved the playhead (scrubbing): the preview should show this time.</summary>
    public event EventHandler<double>? SeekRequested;

    public ProjectHistory History { get; } = history;

    public TimelineView View { get; } = view;

    public string? SelectedId { get; private set; }

    /// <summary>The range marked (from, to), or null.</summary>
    public (double From, double To)? Range { get; private set; }

    public double Playhead
    {
        get => _playhead;
        set
        {
            if (value != _playhead)
            {
                _playhead = value;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool IsDragging => _drag != Drag.None;

    /// <summary>The selected item, if it is still in the project.</summary>
    public TimelineItem? Selected => SelectedId is { } id ? History.Project.Find(id)?.Item : null;

    public void Select(string? id)
    {
        if (id != SelectedId)
        {
            SelectedId = id;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SetRange((double From, double To)? range)
    {
        Range = range is { } r && r.To - r.From > Times.Epsilon ? (Times.Round(r.From), Times.Round(r.To)) : null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Every item in the order Tab goes through them: the rows from the top (<see cref="TimelineView.Rows"/>), each by start.</summary>
    public static IReadOnlyList<TimelineItem> TabOrder(VideoProject project) =>
        [.. TimelineView.Rows(project).SelectMany(r => r.Track.Items.OrderBy(i => i.Start))];

    /// <summary>
    /// Tab: selects the item after the selected one in <see cref="TabOrder"/> (before it when
    /// <paramref name="backward"/>; the first or last when nothing is selected), round from the end to the start,
    /// and scrolls to it. False when the timeline is empty.
    /// </summary>
    public bool SelectNext(bool backward = false)
    {
        var items = TabOrder(History.Project);
        if (items.Count == 0)
        {
            return false;
        }

        var index = -1;
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Id == SelectedId)
            {
                index = i;
                break;
            }
        }

        var next = index < 0
            ? (backward ? items.Count - 1 : 0)
            : (index + (backward ? items.Count - 1 : 1)) % items.Count;
        Reveal(items[next].Id);
        Select(items[next].Id);
        return true;
    }

    /// <summary>Scrolls the view (in time and down the rows) to show the item <paramref name="id"/>.</summary>
    public void Reveal(string id)
    {
        var project = History.Project;
        if (project.Find(id) is not { } found)
        {
            return;
        }

        View.RevealSpan(found.Item.Start, found.Item.End);
        if (TimelineView.Rows(project).FirstOrDefault(r => r.Track.Id == found.Track.Id) is { } row)
        {
            View.RevealRow(row);
        }
    }

    /// <summary>
    /// ↑/↓: moves the selected item to the nearest track above (<paramref name="direction"/> below 0) or below that
    /// takes it and has room for it at its time (<see cref="TrackEdits.MoveItem"/>), as one undo step. False when
    /// there is none, or its own track is locked.
    /// </summary>
    public bool MoveSelectedToTrack(int direction)
    {
        var project = History.Project;
        if (direction == 0 || SelectedId is not { } id || project.Find(id) is not { } found)
        {
            return false;
        }

        var rows = TimelineView.Rows(project);
        var from = -1;
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Track.Id == found.Track.Id)
            {
                from = i;
                break;
            }
        }

        var step = Math.Sign(direction);
        for (var i = from + step; from >= 0 && i >= 0 && i < rows.Count; i += step)
        {
            var after = TrackEdits.MoveItem(project, id, rows[i].Track.Id);
            if (!ReferenceEquals(after, project))
            {
                History.Apply(VideoEditorStrings.UndoMoveToTrack, after);
                Reveal(id);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Alt+←/→: moves the selected item (with its linked clips) <paramref name="frames"/> frames later, or earlier
    /// when negative, from frame start to frame start, as far as the items next to it allow. Moves of the same item
    /// one after another are one undo step. False when it can't move.
    /// </summary>
    public bool MoveSelectedByFrames(int frames)
    {
        var project = History.Project;
        if (frames == 0 || Selected is not { } item)
        {
            return false;
        }

        var fps = project.Canvas.Fps;
        var at = item.Start * fps;
        var tolerance = Times.Epsilon * fps;
        var start = (frames > 0 ? Math.Floor(at + tolerance) + frames : Math.Ceiling(at - tolerance) + frames) / fps;
        var after = TimelineEdits.Move(project, item.Id, Math.Max(0, start));
        if (ReferenceEquals(after, project))
        {
            return false;
        }

        History.Apply(VideoEditorStrings.UndoMove, after, $"nudge {item.Id}");
        Reveal(item.Id);
        return true;
    }

    /// <summary>Ctrl+[ / Ctrl+]: trims the selected item's start or end (and its linked clips') to the playhead, as one undo step.</summary>
    public bool TrimSelectedToPlayhead(ItemEdge edge)
    {
        var project = History.Project;
        if (Selected is not { } item)
        {
            return false;
        }

        var after = TimelineEdits.Trim(project, item.Id, edge, Playhead);
        if (ReferenceEquals(after, project))
        {
            return false;
        }

        History.Apply(edge == ItemEdge.Start ? VideoEditorStrings.UndoTrimStart : VideoEditorStrings.UndoTrimEnd, after);
        return true;
    }

    public TimelineCursor CursorAt(double x, double y)
    {
        if (_drag is Drag.Move)
        {
            return TimelineCursor.Move;
        }

        return View.HitTest(History.Project, x, y).Kind switch
        {
            TimelineHitKind.ItemStart or TimelineHitKind.ItemEnd => TimelineCursor.Trim,
            TimelineHitKind.Item => TimelineCursor.Move,
            TimelineHitKind.Ruler => TimelineCursor.Scrub,
            _ => TimelineCursor.Arrow,
        };
    }

    public void PointerDown(double x, double y, bool shift)
    {
        var project = History.Project;
        var hit = View.HitTest(project, x, y);
        _base = project;
        _drags++;
        switch (hit.Kind)
        {
            case TimelineHitKind.Ruler when shift:
            case TimelineHitKind.Track:
            case TimelineHitKind.None:
                _drag = Drag.Range;
                _anchor = shift && Range is null ? Playhead : View.Snap(project, hit.Time, Playhead);
                Select(null);
                SetRange(null);
                break;
            case TimelineHitKind.Ruler:
                _drag = Drag.Scrub;
                Scrub(hit.Time);
                break;
            default:
                _item = project.Find(hit.ItemId!)?.Item;
                Select(hit.ItemId);
                _drag = hit.Kind switch
                {
                    TimelineHitKind.ItemStart => Drag.TrimStart,
                    TimelineHitKind.ItemEnd => Drag.TrimEnd,
                    _ => Drag.Move,
                };
                _grabOffset = hit.Time - (_item?.Start ?? 0);
                break;
        }
    }

    /// <param name="alt">Alt held: no snapping, and a move leaves the linked clips where they are.</param>
    public void PointerMove(double x, double y, bool alt)
    {
        if (_drag == Drag.None || _base is null)
        {
            return;
        }

        var time = Math.Max(0, View.Time(x));
        switch (_drag)
        {
            case Drag.Scrub:
                Scrub(time);
                break;
            case Drag.Range:
                var to = alt ? time : View.Snap(_base, time, Playhead);
                SetRange(to >= _anchor ? (_anchor, to) : (to, _anchor));
                break;
            case Drag.Move when _item is not null:
                Move(time - _grabOffset, alt);
                break;
            case Drag.TrimStart or Drag.TrimEnd when _item is not null:
                var edge = _drag == Drag.TrimStart ? ItemEdge.Start : ItemEdge.End;
                var linked = TimelineEdits.Linked(_base, _item.Id).ToHashSet();
                var target = alt ? time : View.Snap(_base, time, Playhead, linked);
                History.Apply(_drag == Drag.TrimStart ? VideoEditorStrings.UndoTrimStart : VideoEditorStrings.UndoTrimEnd, TimelineEdits.Trim(_base, _item.Id, edge, target), $"drag {_drags}");
                break;
        }
    }

    public void PointerUp(double x, double y)
    {
        _ = (x, y);
        _drag = Drag.None;
        _base = null;
        _item = null;
    }

    /// <summary>Moves the dragged item so it starts at <paramref name="start"/>, snapping its start or end, whichever is nearer.</summary>
    private void Move(double start, bool alt)
    {
        var item = _item!;
        var moving = alt ? new HashSet<string> { item.Id } : TimelineEdits.Linked(_base!, item.Id).ToHashSet();
        if (!alt)
        {
            var byStart = View.Snap(_base!, start, Playhead, moving);
            var byEnd = View.Snap(_base!, start + item.Duration, Playhead, moving) - item.Duration;
            start = Math.Abs(byStart - start) <= Math.Abs(byEnd - start) ? byStart : byEnd;
        }

        History.Apply(VideoEditorStrings.UndoMove, TimelineEdits.Move(_base!, item.Id, Math.Max(0, start), withLinked: !alt), $"drag {_drags}");
    }

    private void Scrub(double time)
    {
        Playhead = Times.Round(time);
        SeekRequested?.Invoke(this, Playhead);
    }
}
