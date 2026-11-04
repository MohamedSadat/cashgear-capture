namespace CashGear.Capture.VideoEditor.Model;

/// <summary>How a value goes from a keyframe to the next.</summary>
public enum MotionEasing
{
    /// <summary>The same speed all the way.</summary>
    Linear,

    /// <summary>Starting slowly.</summary>
    EaseIn,

    /// <summary>Stopping slowly.</summary>
    EaseOut,

    /// <summary>Starting and stopping slowly.</summary>
    EaseInOut,

    /// <summary>Going a little past the value and settling on it.</summary>
    Spring,

    /// <summary>A curve of its own: two control points, as in CSS's cubic-bezier.</summary>
    Bezier,
}

/// <summary>The curves of <see cref="MotionEasing"/>, each from 0 at 0 to 1 at 1.</summary>
public static class MotionCurves
{
    /// <summary>How far a spring goes past its value at most, as a part of the way there.</summary>
    public const double SpringOvershoot = 0.15;

    /// <summary>The curve's value <paramref name="t"/> of the way along (held at its ends outside 0 to 1).</summary>
    /// <param name="curve">A <see cref="MotionEasing.Bezier"/>'s x1, y1, x2, y2 (x clamped to 0 to 1); linear without them.</param>
    public static double Ease(MotionEasing easing, double t, IReadOnlyList<double>? curve = null)
    {
        if (t <= 0)
        {
            return 0;
        }

        if (t >= 1)
        {
            return 1;
        }

        return easing switch
        {
            MotionEasing.EaseIn => t * t * t,
            MotionEasing.EaseOut => 1 - Math.Pow(1 - t, 3),
            MotionEasing.EaseInOut => t * t * (3 - (2 * t)),
            MotionEasing.Spring => Spring(t),
            MotionEasing.Bezier when curve is { Count: 4 } => Bezier(t, curve[0], curve[1], curve[2], curve[3]),
            _ => t,
        };
    }

    /// <summary>A damped spring settling on 1, with a first overshoot of about <see cref="SpringOvershoot"/>, faded so it ends on 1 exactly.</summary>
    private static double Spring(double t)
    {
        const double decay = 6;
        const double turns = 1.6;
        var wave = Math.Exp(-decay * t) * Math.Cos(2 * Math.PI * turns * t);
        return 1 - (wave * (1 - (t * t * t * t)));
    }

    /// <summary>y of the cubic Bézier from (0,0) to (1,1) at x = <paramref name="x"/>: Newton's steps, then halving if they don't settle.</summary>
    private static double Bezier(double x, double x1, double y1, double x2, double y2)
    {
        x1 = Math.Clamp(x1, 0, 1);
        x2 = Math.Clamp(x2, 0, 1);
        static double At(double u, double a, double b) => ((((1 - (3 * b) + (3 * a)) * u) + ((3 * b) - (6 * a))) * u * u) + (3 * a * u);
        static double Slope(double u, double a, double b) => (3 * (1 - (3 * b) + (3 * a)) * u * u) + (2 * ((3 * b) - (6 * a)) * u) + (3 * a);

        var u = x;
        for (var i = 0; i < 8; i++)
        {
            var error = At(u, x1, x2) - x;
            if (Math.Abs(error) < 1e-7)
            {
                return At(u, y1, y2);
            }

            var slope = Slope(u, x1, x2);
            if (Math.Abs(slope) < 1e-6)
            {
                break;
            }

            u -= error / slope;
        }

        var (low, high) = (0.0, 1.0);
        u = x;
        for (var i = 0; i < 40; i++)
        {
            var value = At(u, x1, x2);
            if (Math.Abs(value - x) < 1e-7)
            {
                break;
            }

            if (value < x)
            {
                low = u;
            }
            else
            {
                high = u;
            }

            u = (low + high) / 2;
        }

        return At(u, y1, y2);
    }
}

/// <summary>A clip's volume keyframes (<see cref="Clip.VolumeKeys"/>): a lane of keys like the others, read for every sample, so found by halving.</summary>
public static class VolumeCurve
{
    /// <summary>The level <paramref name="into"/> seconds after the clip's start: held before the first key and after the last.</summary>
    public static double At(IReadOnlyList<MotionKey>? keys, double into)
    {
        if (keys is not { Count: > 0 })
        {
            return 1;
        }

        if (into <= keys[0].Time)
        {
            return keys[0].Value;
        }

        if (into >= keys[^1].Time)
        {
            return keys[^1].Value;
        }

        var (low, high) = (0, keys.Count - 1);
        while (high - low > 1)
        {
            var middle = (low + high) / 2;
            if (keys[middle].Time <= into)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        var from = keys[low];
        var span = keys[high].Time - from.Time;
        return from.Value + ((keys[high].Value - from.Value) * MotionCurves.Ease(from.Easing, span <= 0 ? 1 : (into - from.Time) / span, from.Curve));
    }

    /// <summary>The part from <paramref name="shift"/> seconds for <paramref name="length"/> seconds, starting at 0: the change in it is unchanged.</summary>
    public static IReadOnlyList<MotionKey>? Sliced(IReadOnlyList<MotionKey>? keys, double shift, double length) =>
        keys is { Count: > 0 } ? new ItemMotion { Opacity = keys }.Sliced(shift, length).Opacity : keys;

    /// <summary>The keys <paramref name="by"/> seconds later (a clip made longer at its start keeps its level where it was).</summary>
    public static IReadOnlyList<MotionKey>? Shifted(IReadOnlyList<MotionKey>? keys, double by) =>
        keys is { Count: > 0 } ? new ItemMotion { Opacity = keys }.Shifted(by).Opacity : keys;
}

/// <summary>A value at a time, seconds from the item's start, and how it goes on to the next keyframe.</summary>
/// <param name="Curve">A <see cref="MotionEasing.Bezier"/>'s x1, y1, x2, y2.</param>
public sealed record MotionKey(double Time, double Value, MotionEasing Easing = MotionEasing.Linear, IReadOnlyList<double>? Curve = null);

/// <summary>One of the things that can have keyframes of its own.</summary>
public enum MotionLane
{
    X,
    Y,
    Scale,
    Rotation,
    Opacity,
    CropLeft,
    CropTop,
    CropRight,
    CropBottom,
}

/// <summary>The values of an item's motion at a moment: the identity changes nothing.</summary>
/// <param name="X">How far it is moved across, as a part of the canvas's width.</param>
/// <param name="Y">How far it is moved down, as a part of the canvas's height.</param>
/// <param name="Scale">Its size, about its middle: 1 as placed.</param>
/// <param name="Rotation">Turned about its middle, in degrees clockwise.</param>
/// <param name="Opacity">0 to 1, on top of its other levels.</param>
/// <param name="CropLeft">The part of its width cut off on the left, 0 to 1.</param>
public readonly record struct MotionState(double X, double Y, double Scale, double Rotation, double Opacity, double CropLeft, double CropTop, double CropRight, double CropBottom)
{
    public static MotionState Identity { get; } = new(0, 0, 1, 0, 1, 0, 0, 0, 0);

    public bool HasCrop => CropLeft > 0 || CropTop > 0 || CropRight > 0 || CropBottom > 0;

    /// <summary>The value of one lane.</summary>
    public double Of(MotionLane lane) => lane switch
    {
        MotionLane.X => X,
        MotionLane.Y => Y,
        MotionLane.Scale => Scale,
        MotionLane.Rotation => Rotation,
        MotionLane.Opacity => Opacity,
        MotionLane.CropLeft => CropLeft,
        MotionLane.CropTop => CropTop,
        MotionLane.CropRight => CropRight,
        _ => CropBottom,
    };
}

/// <summary>
/// A picture clip's or an annotation's position, scale, rotation, opacity and crop over its time, on top of where it is
/// placed. Each has keyframes of its own (a single one is a constant; none is the identity); times are seconds from the
/// item's start, on the timeline, so a speed change doesn't stretch them.
/// </summary>
/// <param name="Animated">Changing a value makes a keyframe at the playhead, rather than setting it for the whole item.</param>
public sealed record ItemMotion
{
    public bool Animated { get; init; }

    public IReadOnlyList<MotionKey>? X { get; init; }

    public IReadOnlyList<MotionKey>? Y { get; init; }

    public IReadOnlyList<MotionKey>? Scale { get; init; }

    public IReadOnlyList<MotionKey>? Rotation { get; init; }

    public IReadOnlyList<MotionKey>? Opacity { get; init; }

    public IReadOnlyList<MotionKey>? CropLeft { get; init; }

    public IReadOnlyList<MotionKey>? CropTop { get; init; }

    public IReadOnlyList<MotionKey>? CropRight { get; init; }

    public IReadOnlyList<MotionKey>? CropBottom { get; init; }

    /// <summary>The value nothing changes.</summary>
    public static double Default(MotionLane lane) => lane is MotionLane.Scale or MotionLane.Opacity ? 1 : 0;

    public IReadOnlyList<MotionKey>? Lane(MotionLane lane) => lane switch
    {
        MotionLane.X => X,
        MotionLane.Y => Y,
        MotionLane.Scale => Scale,
        MotionLane.Rotation => Rotation,
        MotionLane.Opacity => Opacity,
        MotionLane.CropLeft => CropLeft,
        MotionLane.CropTop => CropTop,
        MotionLane.CropRight => CropRight,
        _ => CropBottom,
    };

    public ItemMotion WithLane(MotionLane lane, IReadOnlyList<MotionKey>? keys) => lane switch
    {
        MotionLane.X => this with { X = keys },
        MotionLane.Y => this with { Y = keys },
        MotionLane.Scale => this with { Scale = keys },
        MotionLane.Rotation => this with { Rotation = keys },
        MotionLane.Opacity => this with { Opacity = keys },
        MotionLane.CropLeft => this with { CropLeft = keys },
        MotionLane.CropTop => this with { CropTop = keys },
        MotionLane.CropRight => this with { CropRight = keys },
        _ => this with { CropBottom = keys },
    };

    /// <summary>Every lane with its keys.</summary>
    public IEnumerable<(MotionLane Lane, IReadOnlyList<MotionKey> Keys)> Lanes =>
        Enum.GetValues<MotionLane>().Select(l => (l, Lane(l))).Where(p => p.Item2 is { Count: > 0 }).Select(p => (p.l, p.Item2!));

    /// <summary>The values <paramref name="into"/> seconds after the item's start.</summary>
    public MotionState At(double into) => new(
        Value(X, into, 0),
        Value(Y, into, 0),
        Value(Scale, into, 1),
        Value(Rotation, into, 0),
        Math.Clamp(Value(Opacity, into, 1), 0, 1),
        Math.Clamp(Value(CropLeft, into, 0), 0, 1),
        Math.Clamp(Value(CropTop, into, 0), 0, 1),
        Math.Clamp(Value(CropRight, into, 0), 0, 1),
        Math.Clamp(Value(CropBottom, into, 0), 0, 1));

    /// <summary>A lane's value at a time: held before the first keyframe and after the last, eased by the keyframe before it in between.</summary>
    public static double Value(IReadOnlyList<MotionKey>? keys, double into, double fallback)
    {
        if (keys is not { Count: > 0 })
        {
            return fallback;
        }

        if (into <= keys[0].Time)
        {
            return keys[0].Value;
        }

        for (var i = 1; i < keys.Count; i++)
        {
            if (into < keys[i].Time)
            {
                var from = keys[i - 1];
                var span = keys[i].Time - from.Time;
                var t = span <= 0 ? 1 : (into - from.Time) / span;
                return from.Value + ((keys[i].Value - from.Value) * MotionCurves.Ease(from.Easing, t, from.Curve));
            }
        }

        return keys[^1].Value;
    }

    /// <summary>The times with a keyframe in any lane that moves, in order. A lane with one key is a constant, and has none to show.</summary>
    public IReadOnlyList<double> KeyTimes() =>
        [.. Lanes.Where(l => Animated || l.Keys.Count > 1).SelectMany(l => l.Keys).Select(k => k.Time).Distinct().Order()];

    /// <summary>The part from <paramref name="shift"/> seconds for <paramref name="length"/> seconds, starting at 0: the movement in it is unchanged.</summary>
    public ItemMotion Sliced(double shift, double length)
    {
        var result = this;
        foreach (var (lane, keys) in Lanes)
        {
            if (keys.Count == 1)
            {
                result = result.WithLane(lane, [keys[0] with { Time = 0 }]);
                continue;
            }

            var end = shift + length;
            var kept = new List<MotionKey>();
            void Add(MotionKey key)
            {
                if (kept.Count > 0 && Math.Abs(kept[^1].Time - key.Time) <= Times.Epsilon)
                {
                    kept[^1] = key;
                }
                else
                {
                    kept.Add(key);
                }
            }

            Add(new MotionKey(0, Value(keys, shift, 0)));
            for (var i = 0; i + 1 < keys.Count; i++)
            {
                var (a, b) = (keys[i], keys[i + 1]);
                var from = Math.Max(a.Time, shift);
                var to = Math.Min(b.Time, end);
                if (to <= from + Times.Epsilon)
                {
                    continue;
                }

                if ((from <= a.Time + Times.Epsilon && to >= b.Time - Times.Epsilon) || a.Easing == MotionEasing.Linear)
                {
                    // All of a curve keeps its shape, and any part of a straight line is a straight line.
                    Add(a with { Time = Times.Round(from - shift), Value = Value(keys, from, 0) });
                }
                else
                {
                    // Part of a curve has no curve of its own: its shape is kept in short straight lines.
                    var steps = Math.Clamp((int)Math.Ceiling((to - from) * SlicedSamples), 2, SlicedSamples * 2);
                    for (var n = 0; n < steps; n++)
                    {
                        var at = from + ((to - from) * n / steps);
                        Add(new MotionKey(Times.Round(at - shift), Value(keys, at, 0)));
                    }
                }

                // Where this stretch ends, which the next one starts from.
                Add(new MotionKey(Times.Round(to - shift), Value(keys, to, 0)));
            }

            Add(new MotionKey(Times.Round(length), Value(keys, end, 0)));
            result = result.WithLane(lane, kept);
        }

        return result;
    }

    /// <summary>The straight lines a second that stand for part of a curve cut by a split.</summary>
    private const int SlicedSamples = 30;

    /// <summary>The keys <paramref name="by"/> seconds later (an item made longer at its start keeps its movement where it was).</summary>
    public ItemMotion Shifted(double by)
    {
        var result = this;
        foreach (var (lane, keys) in Lanes)
        {
            result = result.WithLane(lane, [.. keys.Select(k => k with { Time = Times.Round(k.Time + by) })]);
        }

        return result;
    }
}
