using CashGear.Capture.Core.Geometry;

namespace CashGear.Capture.Core.Overlays;

public enum PointerButton
{
    Left,
    Right,
    Middle,
    X1,
    X2,
}

/// <summary>A mouse button press: where, when it went down and (once known) when it came up.</summary>
public sealed record Click(PointerButton Button, PixelPoint At, TimeSpan Down, TimeSpan? Up = null);

/// <summary>One version of a keystroke caption: from <paramref name="Time"/> on, caption <paramref name="Id"/> reads <paramref name="Text"/>.</summary>
public readonly record struct KeyCaption(int Id, TimeSpan Time, string Text);

/// <summary>From <paramref name="Time"/> on, the pointer has shape <paramref name="Shape"/> (0: unknown) and is shown or hidden by its app.</summary>
public readonly record struct PointerShapeChange(TimeSpan Time, int Shape, bool Visible);

/// <summary>The foreground app changed: its program file and window title (null when not kept).</summary>
public sealed record FocusChange(TimeSpan Time, string? App, string? Title);

/// <summary>Where the pointer was, and since when it hasn't moved.</summary>
public readonly record struct PointerState(PixelPoint At, TimeSpan StillSince, int Shape, bool Visible);

/// <summary>
/// The input of a recording on the recording clock: where the pointer was, the clicks, the keystroke captions and the
/// pointer's shape, as overlays and the metadata need them. Filled from one thread (live: the input thread; later:
/// the metadata file), read from another (the video thread), so every member is thread-safe. Each kind of event is
/// kept in time order; a late event is put in its place.
/// </summary>
public sealed class InputTimeline
{
    private readonly object _gate = new();
    private readonly List<(TimeSpan Time, PixelPoint At)> _moves = [];
    private readonly List<Click> _clicks = [];
    private readonly List<KeyCaption> _captions = [];
    private readonly List<PointerShapeChange> _shapes = [];
    private readonly List<(TimeSpan Time, bool On)> _drawing = [];
    private readonly List<FocusChange> _focus = [];

    /// <param name="start">Where the pointer is when the recording starts.</param>
    public InputTimeline(PixelPoint start = default) => _moves.Add((TimeSpan.Zero, start));

    /// <summary>The pointer moved to <paramref name="at"/> (screen pixels).</summary>
    public void Move(TimeSpan time, PixelPoint at)
    {
        lock (_gate)
        {
            if (_moves.Count > 0 && _moves[^1].At == at)
            {
                return;
            }

            Insert(_moves, (Time: time, At: at), m => m.Time);
        }
    }

    public void ButtonDown(TimeSpan time, PointerButton button, PixelPoint at)
    {
        lock (_gate)
        {
            Insert(_clicks, new Click(button, at, time), c => c.Down);
        }
    }

    /// <summary>Ends the latest press of <paramref name="button"/> that is still down (none: nothing happens).</summary>
    public void ButtonUp(TimeSpan time, PointerButton button)
    {
        lock (_gate)
        {
            for (var i = _clicks.Count - 1; i >= 0; i--)
            {
                if (_clicks[i].Button == button && _clicks[i].Up is null && _clicks[i].Down <= time)
                {
                    _clicks[i] = _clicks[i] with { Up = time };
                    return;
                }
            }
        }
    }

    public void Caption(KeyCaption caption)
    {
        lock (_gate)
        {
            Insert(_captions, caption, c => c.Time);
        }
    }

    public void Shape(PointerShapeChange change)
    {
        lock (_gate)
        {
            if (_shapes.Count > 0 && _shapes[^1].Shape == change.Shape && _shapes[^1].Visible == change.Visible)
            {
                return;
            }

            Insert(_shapes, change, s => s.Time);
        }
    }

    /// <summary>Drawing on the screen started or stopped: clicks and keys made meanwhile aren't overlays.</summary>
    public void Drawing(TimeSpan time, bool on)
    {
        lock (_gate)
        {
            Insert(_drawing, (Time: time, On: on), d => d.Time);
        }
    }

    public void Focus(FocusChange change)
    {
        lock (_gate)
        {
            Insert(_focus, change, f => f.Time);
        }
    }

    /// <summary>The pointer at <paramref name="time"/>: where, since when it is there, and its shape.</summary>
    public PointerState Pointer(TimeSpan time)
    {
        lock (_gate)
        {
            var i = LastAtOrBefore(_moves, time, m => m.Time);
            var (since, at) = i < 0 ? (TimeSpan.Zero, _moves.Count > 0 ? _moves[0].At : default) : _moves[i];
            var s = LastAtOrBefore(_shapes, time, c => c.Time);
            var shape = s < 0 ? new PointerShapeChange(TimeSpan.Zero, 0, Visible: true) : _shapes[s];
            return new PointerState(at, since, shape.Shape, shape.Visible);
        }
    }

    /// <summary>Presses that may still show at <paramref name="time"/>: down within <paramref name="window"/> before it, or held then.</summary>
    public IReadOnlyList<Click> Clicks(TimeSpan time, TimeSpan window)
    {
        var result = new List<Click>();
        Clicks(time, window, result);
        return result;
    }

    /// <summary>As <see cref="Clicks(TimeSpan, TimeSpan)"/>, into <paramref name="result"/> (cleared first): nothing is allocated.</summary>
    public void Clicks(TimeSpan time, TimeSpan window, List<Click> result)
    {
        ArgumentNullException.ThrowIfNull(result);
        result.Clear();
        lock (_gate)
        {
            foreach (var click in _clicks)
            {
                if (click.Down > time)
                {
                    break;
                }

                // Released by then: shows for a while after the press or the release, whichever is later.
                var held = click.Up is not { } up || up > time;
                if ((held || time - click.Down <= window || time - click.Up!.Value <= window) && !IsDrawing(click.Down))
                {
                    result.Add(held ? click with { Up = null } : click);
                }
            }
        }
    }

    /// <summary>Captions as they read at <paramref name="time"/>, oldest first, each with when it was last changed.</summary>
    public IReadOnlyList<(int Id, string Text, TimeSpan Started, TimeSpan Changed)> Captions(TimeSpan time, TimeSpan hold)
    {
        lock (_gate)
        {
            var byId = new Dictionary<int, (string Text, TimeSpan Started, TimeSpan Changed)>();
            for (var i = 0; i < _captions.Count && _captions[i].Time <= time; i++)
            {
                var caption = _captions[i];
                if (time - caption.Time > hold + TimeSpan.FromSeconds(30))
                {
                    continue;
                }

                byId[caption.Id] = byId.TryGetValue(caption.Id, out var known)
                    ? (caption.Text, known.Started, caption.Time)
                    : (caption.Text, caption.Time, caption.Time);
            }

            return [.. byId
                .Where(p => time - p.Value.Changed <= hold && !IsDrawing(p.Value.Started))
                .OrderBy(p => p.Value.Started)
                .Select(p => (p.Key, p.Value.Text, p.Value.Started, p.Value.Changed))];
        }
    }

    public bool IsDrawing(TimeSpan time)
    {
        lock (_gate)
        {
            var i = LastAtOrBefore(_drawing, time, d => d.Time);
            return i >= 0 && _drawing[i].On;
        }
    }

    /// <summary>The foreground app at <paramref name="time"/>, if known.</summary>
    public FocusChange? FocusAt(TimeSpan time)
    {
        lock (_gate)
        {
            var i = LastAtOrBefore(_focus, time, f => f.Time);
            return i < 0 ? null : _focus[i];
        }
    }

    /// <summary>Forgets what happened before <paramref name="before"/> except what is still needed to answer for later times.</summary>
    public void Prune(TimeSpan before)
    {
        lock (_gate)
        {
            Trim(_moves, before, m => m.Time);
            Trim(_shapes, before, s => s.Time);
            Trim(_drawing, before, d => d.Time);
            Trim(_focus, before, f => f.Time);
            _clicks.RemoveAll(c => c.Up is { } up && up < before && c.Down < before);
            var live = _captions.Where(c => c.Time >= before).Select(c => c.Id).ToHashSet();
            _captions.RemoveAll(c => c.Time < before && !live.Contains(c.Id));
        }
    }

    /// <summary>Removes everything before <paramref name="before"/> but the last entry before it (the state at that moment).</summary>
    private static void Trim<T>(List<T> list, TimeSpan before, Func<T, TimeSpan> time)
    {
        var keep = LastAtOrBefore(list, before, time);
        if (keep > 0)
        {
            list.RemoveRange(0, keep);
        }
    }

    private static void Insert<T>(List<T> list, T item, Func<T, TimeSpan> time)
    {
        var at = list.Count;
        while (at > 0 && time(list[at - 1]) > time(item))
        {
            at--;
        }

        list.Insert(at, item);
    }

    /// <summary>The index of the last entry at or before <paramref name="time"/>, or -1.</summary>
    private static int LastAtOrBefore<T>(List<T> list, TimeSpan time, Func<T, TimeSpan> of)
    {
        int lo = 0, hi = list.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            if (of(list[mid]) <= time)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return found;
    }
}
