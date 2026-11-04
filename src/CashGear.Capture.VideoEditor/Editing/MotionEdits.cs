using CashGear.Capture.VideoEditor.Model;

namespace CashGear.Capture.VideoEditor.Editing;

/// <summary>Changing an item's motion and its keyframes: functions from the old to the new, none of them touching anything else.</summary>
public static class MotionEdits
{
    /// <summary>The lanes a keyframe at the playhead is made in, unless a crop is in use.</summary>
    private static readonly MotionLane[] MainLanes = [MotionLane.X, MotionLane.Y, MotionLane.Scale, MotionLane.Rotation, MotionLane.Opacity];

    /// <summary>The motion of an item that can have one (a picture clip or an annotation), or null.</summary>
    public static ItemMotion? Of(TimelineItem item) => item switch
    {
        Clip clip => clip.Motion,
        AnnotationItem annotation => annotation.Motion,
        _ => null,
    };

    /// <summary>The item with another motion.</summary>
    public static TimelineItem With(TimelineItem item, ItemMotion? motion) => item switch
    {
        Clip clip => clip with { Motion = motion },
        AnnotationItem annotation => annotation with { Motion = motion },
        _ => item,
    };

    /// <summary>
    /// A value set <paramref name="into"/> seconds after the item's start: while animating, in a keyframe there (that one
    /// changed, or a new one going on as the key before it did); else for the whole item.
    /// </summary>
    public static ItemMotion SetValue(ItemMotion? motion, MotionLane lane, double into, double value, int fps)
    {
        var current = motion ?? new ItemMotion();
        if (!current.Animated)
        {
            return current.WithLane(lane, [new MotionKey(0, value)]);
        }

        into = Times.Round(Math.Max(0, into));
        var keys = current.Lane(lane)?.ToList() ?? [];
        var at = keys.FindIndex(k => Math.Abs(k.Time - into) <= 0.5 / fps);
        if (at >= 0)
        {
            keys[at] = keys[at] with { Value = value };
        }
        else
        {
            var before = keys.LastOrDefault(k => k.Time < into);
            keys.Add(new MotionKey(into, value, before?.Easing ?? MotionEasing.Linear, before?.Curve));
            keys.Sort((a, b) => a.Time.CompareTo(b.Time));
        }

        return current.WithLane(lane, keys);
    }

    /// <summary>A keyframe <paramref name="into"/> seconds after the start in the lanes that matter, each at what it is now, turning animating on.</summary>
    public static ItemMotion AddKeyframe(ItemMotion? motion, double into, int fps)
    {
        var current = (motion ?? new ItemMotion()) with { Animated = true };
        var state = current.At(into);
        if (motion is not { Animated: true })
        {
            // What held for the whole item is where its first keyframes start from.
            foreach (var (lane, keys) in current.Lanes.Where(l => l.Keys.Count == 1).ToList())
            {
                current = current.WithLane(lane, [keys[0] with { Time = Times.Round(Math.Max(0, into)) }]);
            }
        }

        var lanes = state.HasCrop || current.CropLeft is not null || current.CropTop is not null || current.CropRight is not null || current.CropBottom is not null
            ? Enum.GetValues<MotionLane>()
            : MainLanes;
        foreach (var lane in lanes)
        {
            current = SetValue(current, lane, into, state.Of(lane), fps);
        }

        return current;
    }

    /// <summary>
    /// Every lane without its keyframe <paramref name="into"/> seconds after the start. A lane left with one key keeps it
    /// as its constant; the last one of a lane goes only when no other lane has any left at that time.
    /// </summary>
    public static ItemMotion RemoveKeyframe(ItemMotion? motion, double into, int fps)
    {
        var current = motion ?? new ItemMotion();
        foreach (var (lane, keys) in current.Lanes)
        {
            if (keys.Count > 1)
            {
                current = current.WithLane(lane, [.. keys.Where(k => Math.Abs(k.Time - into) > 0.5 / fps)]);
            }
        }

        return current;
    }

    /// <summary>Whether there is a keyframe within half a frame of that time, in a lane that moves.</summary>
    public static bool HasKeyframe(ItemMotion? motion, double into, int fps) =>
        motion?.KeyTimes().Any(t => Math.Abs(t - into) <= 0.5 / fps) == true;

    /// <summary>The keyframes at <paramref name="from"/> moved to <paramref name="to"/>, within the item, in every lane; a keyframe of the lane already there is replaced.</summary>
    public static ItemMotion MoveKeyframes(ItemMotion motion, double from, double to, double length, int fps)
    {
        ArgumentNullException.ThrowIfNull(motion);
        to = Times.Round(Math.Clamp(to, 0, length));
        var result = motion;
        foreach (var (lane, keys) in motion.Lanes)
        {
            var moving = keys.FirstOrDefault(k => Math.Abs(k.Time - from) <= 0.5 / fps);
            if (moving is null)
            {
                continue;
            }

            var others = keys.Where(k => !ReferenceEquals(k, moving) && Math.Abs(k.Time - to) > 0.5 / fps);
            result = result.WithLane(lane, [.. others.Append(moving with { Time = to }).OrderBy(k => k.Time)]);
        }

        return result;
    }

    /// <summary>Every lane's keyframe at or before the time goes on with this easing, which is what a choice in the panel means at the playhead.</summary>
    public static ItemMotion SetEasing(ItemMotion motion, double into, MotionEasing easing, IReadOnlyList<double>? curve, int fps)
    {
        ArgumentNullException.ThrowIfNull(motion);
        var result = motion;
        foreach (var (lane, keys) in motion.Lanes)
        {
            var at = -1;
            for (var i = 0; i < keys.Count; i++)
            {
                if (keys[i].Time <= into + (0.5 / fps))
                {
                    at = i;
                }
            }

            if (at >= 0)
            {
                var changed = keys.ToList();
                changed[at] = changed[at] with { Easing = easing, Curve = easing == MotionEasing.Bezier ? curve : null };
                result = result.WithLane(lane, changed);
            }
        }

        return result;
    }

    /// <summary>The easing of the keyframe at or before the time (of the first lane that has one), for the panel to show.</summary>
    public static (MotionEasing Easing, IReadOnlyList<double>? Curve) EasingAt(ItemMotion? motion, double into, int fps)
    {
        foreach (var (_, keys) in motion?.Lanes ?? [])
        {
            if (keys.LastOrDefault(k => k.Time <= into + (0.5 / fps)) is { } key)
            {
                return (key.Easing, key.Curve);
            }
        }

        return (MotionEasing.Linear, null);
    }

    /// <summary>The time of the next keyframe after <paramref name="into"/> (the previous, when <paramref name="backward"/>), or null.</summary>
    public static double? NextKeyframe(TimelineItem item, double into, bool backward, int fps)
    {
        ArgumentNullException.ThrowIfNull(item);
        var times = KeyTimes(item);
        return backward
            ? times.Where(t => t < into - (0.5 / fps)).Cast<double?>().LastOrDefault()
            : times.Where(t => t > into + (0.5 / fps)).Cast<double?>().FirstOrDefault();
    }

    /// <summary>Where an item has keyframes, in seconds from its start: its motion's, and a moving annotation's own.</summary>
    public static IReadOnlyList<double> KeyTimes(TimelineItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var times = Of(item)?.KeyTimes() ?? [];
        return item is AnnotationItem { Keyframes.Count: > 1 } annotation ? [.. times.Concat(annotation.Keyframes.Select(k => k.Time)).Distinct().Order()] : times;
    }

    /// <summary>A keyframe's move on the timeline, for a diamond dragged along the lane: in the motion's lanes and an annotation's own keyframes.</summary>
    public static TimelineItem MoveKeyframesOf(TimelineItem item, double from, double to, int fps)
    {
        ArgumentNullException.ThrowIfNull(item);
        var moved = Of(item) is { } motion ? With(item, MoveKeyframes(motion, from, to, item.Duration, fps)) : item;
        if (moved is AnnotationItem { Keyframes.Count: > 1 } annotation)
        {
            var target = Times.Round(Math.Clamp(to, 0, item.Duration));
            var keyframes = annotation.Keyframes.Select(k => Math.Abs(k.Time - from) <= 0.5 / fps ? k with { Time = target } : k).OrderBy(k => k.Time).ToList();
            moved = annotation with { Keyframes = keyframes };
        }

        return moved;
    }
}
