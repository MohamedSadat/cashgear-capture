using CashGear.Capture.Core.Audio;
using CashGear.Capture.Media;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Playback;

namespace CashGear.Capture.VideoEditor.Editing;

/// <summary>Changing a clip's volume keyframes and evening out its loudness: functions from the old to the new.</summary>
public static class SoundEdits
{
    /// <summary>The highest a volume keyframe goes: as far as a clip's own volume does.</summary>
    public const double LoudestKey = 2;

    /// <summary>The loudness Even Out aims at: the usual level for speech online.</summary>
    public const double EvenOutLufs = Loudness.TargetLufs;

    /// <summary>A keyframe <paramref name="into"/> seconds after the start, at the level there, so nothing changes until it is moved.</summary>
    public static Clip AddVolumeKey(Clip clip, double into, int fps)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return SetVolumeAt(clip, into, VolumeCurve.At(clip.VolumeKeys, into), fps);
    }

    /// <summary>The keyframe at that time set to <paramref name="value"/>, or a new one going on as the key before it did.</summary>
    public static Clip SetVolumeAt(Clip clip, double into, double value, int fps)
    {
        ArgumentNullException.ThrowIfNull(clip);
        into = Times.Round(Math.Clamp(into, 0, clip.Duration));
        value = Math.Clamp(value, 0, LoudestKey);
        var keys = clip.VolumeKeys?.ToList() ?? [];
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

        return clip with { VolumeKeys = keys };
    }

    /// <summary>Without the keyframe at that time. Without any left, the clip has no change along it.</summary>
    public static Clip RemoveVolumeKey(Clip clip, double into, int fps)
    {
        ArgumentNullException.ThrowIfNull(clip);
        var keys = clip.VolumeKeys?.Where(k => Math.Abs(k.Time - into) > 0.5 / fps).ToList();
        return clip with { VolumeKeys = keys is { Count: > 0 } ? keys : null };
    }

    /// <summary>The keyframe at <paramref name="from"/> moved to <paramref name="to"/> within the clip, and to <paramref name="value"/> if there is one; one already there is replaced.</summary>
    public static Clip MoveVolumeKey(Clip clip, double from, double to, double? value, int fps)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (clip.VolumeKeys is not { } keys || keys.FirstOrDefault(k => Math.Abs(k.Time - from) <= 0.5 / fps) is not { } moving)
        {
            return clip;
        }

        to = Times.Round(Math.Clamp(to, 0, clip.Duration));
        var others = keys.Where(k => !ReferenceEquals(k, moving) && Math.Abs(k.Time - to) > 0.5 / fps);
        var moved = moving with { Time = to, Value = value is { } level ? Math.Clamp(level, 0, LoudestKey) : moving.Value };
        return clip with { VolumeKeys = [.. others.Append(moved).OrderBy(k => k.Time)] };
    }

    /// <summary>The keyframe at or before the time goes on with this easing.</summary>
    public static Clip SetVolumeEasing(Clip clip, double into, MotionEasing easing, int fps)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (clip.VolumeKeys is not { } keys)
        {
            return clip;
        }

        var at = -1;
        for (var i = 0; i < keys.Count; i++)
        {
            if (keys[i].Time <= into + (0.5 / fps))
            {
                at = i;
            }
        }

        if (at < 0)
        {
            return clip;
        }

        var changed = keys.ToList();
        changed[at] = changed[at] with { Easing = easing, Curve = null };
        return clip with { VolumeKeys = changed };
    }

    /// <summary>The clip with its effects changed, null again when they change nothing.</summary>
    public static Clip WithSound(Clip clip, Func<ClipSound, ClipSound> change)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(change);
        var sound = change(clip.Sound ?? new ClipSound());
        return clip with { Sound = sound.IsActive || sound != new ClipSound() ? sound : null };
    }

    /// <summary>
    /// The clip's sound as it is through its effects, at its own level and with no fades or volume keyframes: what Even Out measures.
    /// </summary>
    public static LoudnessMeter Measure(MediaAsset asset, Clip clip, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(clip);
        var bare = clip with
        {
            Start = 0,
            Volume = 1,
            FadeIn = 0,
            FadeOut = 0,
            VolumeKeys = null,
            EqualPowerIn = false,
            Sound = clip.Sound is { } sound ? sound with { GainDb = 0 } : null,
        };
        var project = new VideoProject(VideoProject.CurrentVersion, new ProjectCanvas(16, 16, 30), [asset], [new Track(Ids.New(), TrackKind.Audio, "", [bare])]);
        var meter = new LoudnessMeter();
        var total = TimelineMixer.Sample(bare.Duration);
        var block = new float[TimelineMixer.SampleRate * TimelineMixer.Channels / 10];
        using var mixer = new TimelineMixer { Clamps = false };
        for (var at = 0L; at < total; at += block.Length / TimelineMixer.Channels)
        {
            ct.ThrowIfCancellationRequested();
            var count = (int)Math.Min(block.Length / TimelineMixer.Channels, total - at);
            var samples = block.AsSpan(0, count * TimelineMixer.Channels);
            mixer.Mix(project, at, samples);
            meter.Add(samples);
            progress?.Report((double)at / total);
        }

        return meter;
    }

    /// <summary>
    /// Even Out: the gain that brings a clip measured so to <see cref="EvenOutLufs"/>. Silence stays as it is. When the gain would push
    /// its peaks past full scale, the clip's limiter goes on, so they are held rather than clipped.
    /// </summary>
    public static Clip EvenOut(Clip clip, double measuredLufs, double peakDb)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (!double.IsFinite(measuredLufs) || measuredLufs <= Loudness.QuietestLufs)
        {
            return clip;
        }

        var gain = Math.Round(Math.Clamp(EvenOutLufs - measuredLufs, -ClipSound.MostGainDb, ClipSound.MostGainDb), 1);
        return WithSound(clip, s => s with { GainDb = gain, Limiter = s.Limiter || peakDb + gain > -1 });
    }
}

/// <param name="AmountDb">How far the music goes down under the voice (negative dB).</param>
/// <param name="Attack">Seconds it takes to go down, ending as the voice starts.</param>
/// <param name="Release">Seconds it takes to come back, starting as the voice stops.</param>
/// <param name="ShortestPause">A pause in the voice shorter than this doesn't count as one.</param>
public sealed record DuckOptions(double AmountDb = -12, double Attack = 0.3, double Release = 0.5, double ShortestPause = 0.4)
{
    /// <summary>The level under the voice.</summary>
    public double Level => Math.Pow(10, Math.Min(0, AmountDb) / 20);
}

/// <summary>
/// Ducking: music, and any track marked as background, goes down while the voice speaks. The result is ordinary volume
/// keyframes on the clips, in place of any they had, so any of it can be changed by hand or made again with other settings.
/// </summary>
public static class Ducking
{
    /// <summary>A sound shorter than this isn't speech: a click, a cough.</summary>
    private const double ShortestSpeech = 0.1;

    /// <summary>The tracks marked as background, or those with this name (music added before tracks were marked), but not the voice's.</summary>
    public static IReadOnlyList<Track> BackgroundTracks(VideoProject project, Track? voice, string? musicName = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        return [.. project.Tracks.Where(t => t.HasSound && !t.Locked && t.Id != voice?.Id && t.Clips.Any() && (t.Background || (musicName is not null && string.Equals(t.Name, musicName, StringComparison.CurrentCultureIgnoreCase))))];
    }

    /// <summary>The track the voice is on, among those that aren't background or left out (the track being ducked); null when there is none.</summary>
    public static Track? VoiceTrack(VideoProject project, string? musicName = null, string? notThis = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        var background = BackgroundTracks(project, null, musicName).Select(t => t.Id).ToHashSet();
        return SilenceFinder.VoiceTrack(project with { Tracks = [.. project.Tracks.Where(t => t.Id != notThis && !background.Contains(t.Id))] });
    }

    /// <summary>When the voice is heard on the timeline, in time order: the voice track's clips without their pauses.</summary>
    public static IReadOnlyList<(double From, double To)> SpeechSpans(VideoProject project, Track voice, Func<MediaAsset, int, SoundLevels?>? levels, DuckOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(voice);
        var settings = options ?? new DuckOptions();
        var pauses = SilenceFinder.Find(project, voice, levels, settings.ShortestPause);
        var spans = new List<(double From, double To)>();
        foreach (var clip in voice.Clips.Where(c => !c.IsFreeze).OrderBy(c => c.Start))
        {
            var from = clip.Start;
            foreach (var pause in pauses.Where(p => p.End > clip.Start && p.Start < clip.End))
            {
                if (pause.Start > from)
                {
                    spans.Add((from, pause.Start));
                }

                from = Math.Max(from, pause.End);
            }

            if (clip.End > from)
            {
                spans.Add((from, clip.End));
            }
        }

        // What touches is one, and a sound too short to be speech is none.
        var merged = new List<(double From, double To)>();
        foreach (var span in spans.OrderBy(s => s.From))
        {
            if (merged.Count > 0 && span.From <= merged[^1].To + TranscriptEdits.LongestJoin)
            {
                merged[^1] = (merged[^1].From, Math.Max(merged[^1].To, span.To));
            }
            else
            {
                merged.Add(span);
            }
        }

        return [.. merged.Where(s => s.To - s.From >= ShortestSpeech)];
    }

    /// <summary>The volume keyframes that duck a clip under these spans: none when the voice isn't heard during it.</summary>
    public static IReadOnlyList<MotionKey>? Keys(Clip clip, IReadOnlyList<(double From, double To)> speech, DuckOptions options)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(speech);
        ArgumentNullException.ThrowIfNull(options);

        // Spans the music couldn't come back between are one.
        var low = new List<(double From, double To)>();
        foreach (var (from, to) in speech)
        {
            if (low.Count > 0 && from - low[^1].To < options.Attack + options.Release)
            {
                low[^1] = (low[^1].From, to);
            }
            else
            {
                low.Add((from, to));
            }
        }

        var level = options.Level;
        double At(double time)
        {
            foreach (var (from, to) in low)
            {
                if (time < from - options.Attack)
                {
                    return 1;
                }

                if (time < from)
                {
                    return 1 + ((level - 1) * (time - (from - options.Attack)) / options.Attack);
                }

                if (time <= to)
                {
                    return level;
                }

                if (time < to + options.Release)
                {
                    return level + ((1 - level) * (time - to) / options.Release);
                }
            }

            return 1;
        }

        var times = new SortedSet<double> { clip.Start, clip.End };
        foreach (var (from, to) in low)
        {
            foreach (var time in new[] { from - options.Attack, from, to, to + options.Release })
            {
                if (time > clip.Start && time < clip.End)
                {
                    times.Add(time);
                }
            }
        }

        if (!low.Any(s => s.From - options.Attack < clip.End && s.To + options.Release > clip.Start))
        {
            return null;
        }

        var keys = new List<MotionKey>();
        foreach (var time in times)
        {
            var key = new MotionKey(Times.Round(time - clip.Start), Math.Round(At(time), 4));
            if (keys.Count >= 2 && keys[^1].Value == key.Value && keys[^2].Value == key.Value)
            {
                keys[^1] = key; // the middle of a flat stretch says nothing
            }
            else
            {
                keys.Add(key);
            }
        }

        return keys;
    }

    /// <summary>The project with these tracks ducked, one undo step's worth. A clip the voice doesn't touch loses any keyframes it had.</summary>
    public static VideoProject Duck(VideoProject project, IReadOnlyCollection<string> trackIds, IReadOnlyList<(double From, double To)> speech, DuckOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(trackIds);
        var settings = options ?? new DuckOptions();
        return project with
        {
            Tracks = [.. project.Tracks.Select(track => trackIds.Contains(track.Id) && track.HasSound && !track.Locked
                ? track.WithItems(track.Items.Select(item => item is Clip clip && !clip.IsFreeze ? clip with { VolumeKeys = Keys(clip, speech, settings) } : item))
                : track)],
        };
    }
}
