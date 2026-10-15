namespace CashGear.Capture.Core.Recording;

/// <summary>
/// One source of sound (system audio or a microphone) on the recording's timeline: a ring of 48 kHz stereo float
/// frames indexed by recording frame. A packet is placed where its timestamp says, except that a packet within
/// <see cref="JumpFrames"/> of where the previous one ended follows it directly, so timestamp jitter never makes gaps
/// or overlaps. A real gap (loopback sends nothing while nothing plays) or a device glitch puts the packet at its
/// timestamp, with silence before it. If the sound card's clock runs a little fast or slow against the PC's, the
/// smoothed difference is taken back one frame per packet, which can't be heard.
/// </summary>
public sealed class AudioTrack
{
    /// <summary>Further than this (30 ms) from the end of the previous packet, a packet goes where its timestamp says.</summary>
    public const int JumpFrames = AudioMixer.SampleRate * 30 / 1000;

    /// <summary>A smoothed drift beyond this (10 ms) is corrected one frame per packet.</summary>
    public const int SlipFrames = AudioMixer.SampleRate / 100;

    /// <summary>The drift is smoothed over about a second of sound.</summary>
    private const double SmoothingFrames = AudioMixer.SampleRate;

    private readonly float[] _ring;
    private readonly int _capacity;
    private long _next = -1;
    private long _end;
    private double _drift;
    private volatile bool _muted;

    internal AudioTrack(string name, int capacityFrames)
    {
        Name = name;
        _capacity = capacityFrames;
        _ring = new float[capacityFrames * AudioMixer.Channels];
    }

    public string Name { get; }

    /// <summary>While true the track is left out of the mix (it keeps its place, so unmuting is immediate).</summary>
    public bool Muted
    {
        get => _muted;
        set => _muted = value;
    }

    /// <summary>Frames that arrived after their place had been mixed.</summary>
    public long LateFrames { get; private set; }

    /// <summary>Frames that didn't fit because the mix wasn't being taken (ffmpeg not reading).</summary>
    public long OverflowFrames { get; private set; }

    /// <summary>Where the next packet would go if it follows on (for tests).</summary>
    internal long NextPosition => _next;

    /// <summary>Places a packet whose first frame belongs at recording frame <paramref name="position"/>. Called under the mixer's lock.</summary>
    internal void Place(ReadOnlySpan<float> samples, long position, bool discontinuity, long mixPosition)
    {
        var frames = samples.Length / AudioMixer.Channels;
        if (frames == 0)
        {
            return;
        }

        long start;
        if (_next < 0 || discontinuity || Math.Abs(position - _next) > JumpFrames)
        {
            start = position;
            _drift = 0;
        }
        else
        {
            _drift += (position - _next - _drift) * Math.Min(1, frames / SmoothingFrames);
            start = _next;
            if (_drift > SlipFrames)
            {
                // The sound is landing early: hold it back a frame by playing its first frame twice.
                Write(samples[..AudioMixer.Channels], start, mixPosition);
                start++;
                _drift--;
            }
            else if (_drift < -SlipFrames)
            {
                // Landing late: let it overlap the previous packet's last frame.
                start--;
                _drift++;
            }
        }

        Write(samples, start, mixPosition);
        var end = start + frames;
        if (end < _end)
        {
            Clear(end, _end, mixPosition); // jumped back: what was placed beyond this packet is stale
        }

        _end = end;
        _next = end;
    }

    /// <summary>Adds (or, muted, just consumes) the frames from <paramref name="from"/> into <paramref name="output"/>, clearing them.</summary>
    internal void MixInto(Span<float> output, long from)
    {
        var muted = _muted;
        var frames = output.Length / AudioMixer.Channels;
        for (var i = 0; i < frames; i++)
        {
            var slot = Slot(from + i);
            if (!muted)
            {
                output[i * 2] += _ring[slot];
                output[(i * 2) + 1] += _ring[slot + 1];
            }

            _ring[slot] = 0;
            _ring[slot + 1] = 0;
        }
    }

    /// <summary>Throws away frames that won't be mixed.</summary>
    internal void Discard(long from, long frames) => Clear(from, from + frames, from);

    private void Write(ReadOnlySpan<float> samples, long start, long mixPosition)
    {
        var frames = samples.Length / AudioMixer.Channels;
        var skip = start < mixPosition ? (int)Math.Min(frames, mixPosition - start) : 0;
        LateFrames += skip;
        var count = frames - skip;
        var position = start + skip;
        var limit = mixPosition + _capacity;
        if (position + count > limit)
        {
            var over = (int)Math.Min(count, position + count - limit);
            OverflowFrames += over;
            count -= over;
        }

        for (var i = 0; i < count; i++)
        {
            var slot = Slot(position + i);
            _ring[slot] = samples[(skip + i) * 2];
            _ring[slot + 1] = samples[((skip + i) * 2) + 1];
        }
    }

    private void Clear(long from, long to, long mixPosition)
    {
        from = Math.Max(from, mixPosition);
        to = Math.Min(to, mixPosition + _capacity);
        for (var frame = from; frame < to; frame++)
        {
            var slot = Slot(frame);
            _ring[slot] = 0;
            _ring[slot + 1] = 0;
        }
    }

    private int Slot(long frame) => (int)(frame % _capacity) * AudioMixer.Channels;
}

/// <summary>
/// The recording's sound: its tracks mixed in order at 48 kHz stereo float, clamped to ±1. Whatever no track filled is
/// silence, so the sound keeps pace with the clock even when loopback sends nothing. Thread-safe: capture threads
/// place packets while the writer mixes.
/// </summary>
public sealed class AudioMixer(int capacityFrames = AudioMixer.DefaultCapacityFrames)
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;

    /// <summary>8 s per track: enough for ffmpeg to hold sound back while x264's look-ahead fills.</summary>
    public const int DefaultCapacityFrames = SampleRate * 8;

    private readonly object _gate = new();
    private readonly List<AudioTrack> _tracks = [];
    private long _position;
    private float _peak;

    public IReadOnlyList<AudioTrack> Tracks
    {
        get
        {
            lock (_gate)
            {
                return [.. _tracks];
            }
        }
    }

    /// <summary>The next recording frame to mix; everything before it has been mixed or skipped.</summary>
    public long Position
    {
        get
        {
            lock (_gate)
            {
                return _position;
            }
        }
    }

    /// <summary>Frames of sound produced so far.</summary>
    public long Mixed { get; private set; }

    /// <summary>Frames left out because the video lost that time (dropped frames).</summary>
    public long Skipped { get; private set; }

    public AudioTrack AddTrack(string name)
    {
        var track = new AudioTrack(name, capacityFrames);
        lock (_gate)
        {
            _tracks.Add(track);
        }

        return track;
    }

    /// <summary>Puts a packet of interleaved stereo frames on <paramref name="track"/>, its first frame at recording frame <paramref name="position"/>.</summary>
    public void Place(AudioTrack track, ReadOnlySpan<float> samples, long position, bool discontinuity = false)
    {
        lock (_gate)
        {
            track.Place(samples, position, discontinuity, _position);
        }
    }

    /// <summary>Mixes the next <c>output.Length / 2</c> frames into <paramref name="output"/>.</summary>
    public void Mix(Span<float> output)
    {
        var frames = output.Length / Channels;
        lock (_gate)
        {
            output.Clear();
            foreach (var track in _tracks)
            {
                track.MixInto(output[..(frames * Channels)], _position);
            }

            var peak = _peak;
            for (var i = 0; i < frames * Channels; i++)
            {
                var sample = Math.Clamp(output[i], -1f, 1f);
                output[i] = sample;
                peak = Math.Max(peak, Math.Abs(sample));
            }

            _peak = peak;
            _position += frames;
            Mixed += frames;
        }
    }

    /// <summary>Leaves the next <paramref name="frames"/> frames out of the sound.</summary>
    public void Skip(long frames)
    {
        if (frames <= 0)
        {
            return;
        }

        lock (_gate)
        {
            foreach (var track in _tracks)
            {
                track.Discard(_position, frames);
            }

            _position += frames;
            Skipped += frames;
        }
    }

    /// <summary>The loudest mixed sample since the last call, 0 to 1.</summary>
    public float TakePeak()
    {
        lock (_gate)
        {
            var peak = _peak;
            _peak = 0;
            return peak;
        }
    }
}

/// <summary>When the recording's sound is mixed, relative to the clock and the video.</summary>
public static class AudioSchedule
{
    /// <summary>Sound is mixed this long after it is recorded, so packets that arrive a little late still make it.</summary>
    public static readonly TimeSpan Latency = TimeSpan.FromMilliseconds(80);

    /// <summary>Sound frames per video frame: 3200, 1600 or 800 at 15, 30 or 60 fps.</summary>
    public static int FramesPerVideoFrame(int fps)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fps);
        if (AudioMixer.SampleRate % fps != 0)
        {
            throw new ArgumentException($"{fps} fps doesn't divide {AudioMixer.SampleRate} Hz.", nameof(fps));
        }

        return AudioMixer.SampleRate / fps;
    }

    /// <summary>The recording frame at recording time <paramref name="elapsed"/>.</summary>
    public static long FrameAt(TimeSpan elapsed) =>
        elapsed <= TimeSpan.Zero ? 0 : elapsed.Ticks * AudioMixer.SampleRate / TimeSpan.TicksPerSecond;

    /// <summary>
    /// How far the sound may be mixed at recording time <paramref name="elapsed"/>: up to <see cref="Latency"/> ago
    /// while the clock runs. When it stands still (paused or stopped), the hold is let go as
    /// <paramref name="stillFor"/> grows, so after <see cref="Latency"/> everything up to the clock is mixed.
    /// </summary>
    public static long Target(TimeSpan elapsed, TimeSpan? stillFor)
    {
        var hold = stillFor is { } still ? Latency - still : Latency;
        return FrameAt(hold > TimeSpan.Zero ? elapsed - hold : elapsed);
    }
}
