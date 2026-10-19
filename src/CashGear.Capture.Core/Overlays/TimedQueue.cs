namespace CashGear.Capture.Core.Overlays;

/// <summary>
/// The latest few pictures of a stream (the webcam's), each stamped with the recording time it was captured at,
/// for readers that each want "the picture shown at time t": the newest captured at or before t (or, before the
/// first, the first). Every picture is placed by its own capture time, so a camera whose rate wanders or whose
/// pictures arrive late never drifts against the clock. Thread-safe: one thread adds, others read.
/// </summary>
public sealed class TimedQueue<T>(int capacity)
    where T : class
{
    private readonly object _gate = new();
    private readonly List<(TimeSpan Time, T Item)> _items = [];

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    /// <summary>Adds a picture in its place by time; returns the oldest when there are more than the capacity (to reuse), else null.</summary>
    public T? Add(TimeSpan time, T item)
    {
        lock (_gate)
        {
            var at = _items.Count;
            while (at > 0 && _items[at - 1].Time > time)
            {
                at--;
            }

            _items.Insert(at, (time, item));
            if (_items.Count <= capacity)
            {
                return null;
            }

            var oldest = _items[0].Item;
            _items.RemoveAt(0);
            return oldest;
        }
    }

    /// <summary>
    /// The picture shown at <paramref name="time"/> and its time, or null when there is none yet;
    /// <paramref name="keep"/> runs on it before the lock is let go (e.g. to hold its buffer while it is used).
    /// </summary>
    public (TimeSpan Time, T Item)? At(TimeSpan time, Action<T>? keep = null)
    {
        lock (_gate)
        {
            if (_items.Count == 0)
            {
                return null;
            }

            var chosen = _items[0];
            foreach (var entry in _items)
            {
                if (entry.Time > time)
                {
                    break;
                }

                chosen = entry;
            }

            keep?.Invoke(chosen.Item);
            return chosen;
        }
    }

    /// <summary>Takes every picture out (e.g. to free their buffers).</summary>
    public IReadOnlyList<T> Clear()
    {
        lock (_gate)
        {
            var items = _items.Select(i => i.Item).ToList();
            _items.Clear();
            return items;
        }
    }
}
