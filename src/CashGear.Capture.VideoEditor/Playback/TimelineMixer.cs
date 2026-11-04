using CashGear.Capture.Core.Audio;
using CashGear.Capture.Media;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Model;

namespace CashGear.Capture.VideoEditor.Playback;

/// <summary>
/// Mixes a project's sound tracks, a block of samples at a time on the timeline (48 kHz stereo float): each clip read
/// from its file at its source time, through its effects (<see cref="ClipSoundReader"/>), at its speed (atempo keeps the
/// pitch; a ramp keeps it through <see cref="RampStretcher"/>, else it is resampled, as older projects' are),
/// with the track's and the clip's volume, its volume keyframes, its fades and crossfades where clips overlap. Muted
/// tracks are left out, and when any track is solo only the solo ones play. Clips are read in turn, so reading the
/// timeline forward never seeks; a jump repositions the clips it reaches. One thread at a time.
/// </summary>
public sealed class TimelineMixer : IDisposable
{
    public const int SampleRate = AudioStreamReader.SampleRate;
    public const int Channels = AudioStreamReader.Channels;

    /// <summary>Clips starting this soon are opened and positioned ahead, so the block that reaches them doesn't wait.</summary>
    private const double OpenAhead = 0.5;

    /// <summary>
    /// A clip with effects has seconds of sound to work through to get to its start: this far ahead, it does that beside the
    /// mixing, with room to spare on a busy PC (the worst seek takes under a second, the sound mixed ahead lasts a quarter of one).
    /// </summary>
    private const double OpenAheadWithEffects = 2;

    private readonly Dictionary<string, Voice> _voices = [];
    private float[] _scratch = [];
    private int _block;

    /// <summary>Whether the mix is kept within full scale at the end; an export's limiter takes care of that itself.</summary>
    public bool Clamps { get; init; } = true;

    /// <summary>Voices made (for tests: reading forward shouldn't make more than one per clip).</summary>
    public int Repositions { get; private set; }

    /// <summary>
    /// The project's sound for timeline samples <paramref name="start"/> to <paramref name="start"/> + the block's
    /// length, written to <paramref name="output"/> (interleaved L, R; overwritten).
    /// </summary>
    public void Mix(VideoProject project, long start, Span<float> output)
    {
        ArgumentNullException.ThrowIfNull(project);
        output.Clear();
        _block++;
        var frames = output.Length / Channels;
        var end = start + frames;
        var solo = project.Tracks.Any(t => t.HasSound && t.Solo);
        foreach (var track in project.Tracks)
        {
            if (!track.HasSound || track.Muted || (solo && !track.Solo) || track.Volume <= 0)
            {
                continue;
            }

            var clips = track.Clips.ToList();
            for (var i = 0; i < clips.Count; i++)
            {
                var clip = clips[i];
                if (clip.IsFreeze || clip.Volume <= 0 || project.Asset(clip.AssetId) is not { } asset)
                {
                    continue;
                }

                var clipStart = Sample(clip.Start);
                var clipEnd = Sample(clip.End);
                var from = Math.Max(start, clipStart);
                var to = Math.Min(end, clipEnd);
                if (to <= from)
                {
                    if (clipStart >= end && clipStart < end + (long)((clip.Sound is { HasChain: true } ? OpenAheadWithEffects : OpenAhead) * SampleRate))
                    {
                        Prepare(clip, asset, clipStart);
                    }

                    continue;
                }

                var count = (int)(to - from);
                if (_scratch.Length < count * Channels)
                {
                    _scratch = new float[count * Channels];
                }

                var block = _scratch.AsSpan(0, count * Channels);
                var voice = VoiceFor(clip, asset);
                voice.Render(clip, from, block);

                // Crossfades where the clip overlaps the one before or after on its track.
                var overlapIn = i > 0 && clips[i - 1].End > clip.Start + Times.Epsilon ? clips[i - 1].End - clip.Start : 0;
                var overlapOut = i < clips.Count - 1 && clips[i + 1].Start < clip.End - Times.Epsilon ? clip.End - clips[i + 1].Start : 0;

                // A cut's short join is the same sound going on, so it stays a straight line whatever its clip says.
                var equalIn = clip.EqualPowerIn && overlapIn > TranscriptEdits.LongestJoin;
                var equalOut = i < clips.Count - 1 && clips[i + 1].EqualPowerIn && overlapOut > TranscriptEdits.LongestJoin;
                var gain = track.Volume * clip.Volume * (clip.Sound is { HasChain: false } plain ? Decibels.ToGain(plain.GainDb) : 1);
                var keys = clip.VolumeKeys;
                var at = (int)(from - start) * Channels;
                for (var n = 0; n < count; n++)
                {
                    var time = (double)(from + n) / SampleRate;
                    var level = gain * Envelope(clip, time, overlapIn, overlapOut, equalIn, equalOut) * (keys is null ? 1 : VolumeCurve.At(keys, time - clip.Start));
                    output[at + (n * Channels)] += (float)(block[n * Channels] * level);
                    output[at + (n * Channels) + 1] += (float)(block[(n * Channels) + 1] * level);
                }
            }
        }

        foreach (var (id, voice) in _voices.Where(v => v.Value.LastUsed < _block - 1).ToList())
        {
            voice.Dispose();
            _voices.Remove(id);
        }

        if (Clamps)
        {
            for (var i = 0; i < output.Length; i++)
            {
                output[i] = Math.Clamp(output[i], -1f, 1f);
            }
        }
    }

    public void Dispose()
    {
        foreach (var voice in _voices.Values)
        {
            voice.Dispose();
        }

        _voices.Clear();
    }

    /// <summary>A timeline time as a sample position.</summary>
    public static long Sample(double seconds) => (long)Math.Round(seconds * SampleRate);

    /// <summary>A clip's level at <paramref name="time"/> from its fades and crossfades (0 to 1).</summary>
    /// <param name="equalIn">The overlap at its start is an equal-power crossfade, not a straight line.</param>
    /// <param name="equalOut">The same at its end.</param>
    public static double Envelope(Clip clip, double time, double overlapIn = 0, double overlapOut = 0, bool equalIn = false, bool equalOut = false)
    {
        ArgumentNullException.ThrowIfNull(clip);
        var level = 1.0;
        var fadeIn = Math.Max(clip.FadeIn, overlapIn);
        var fadeOut = Math.Max(clip.FadeOut, overlapOut);

        // A clip's own fade goes by its curve; an overlap's crossfade is a straight line or equal power.
        if (fadeIn > 0)
        {
            var part = (time - clip.Start) / fadeIn;
            level = Math.Min(level, clip.FadeIn >= overlapIn ? MotionCurves.Ease(clip.FadeInCurve, part) : equalIn ? EqualPower(part) : part);
        }

        if (fadeOut > 0)
        {
            var part = (clip.End - time) / fadeOut;
            level = Math.Min(level, clip.FadeOut >= overlapOut ? MotionCurves.Ease(clip.FadeOutCurve, part) : equalOut ? EqualPower(part) : part);
        }

        return Math.Clamp(level, 0, 1);
    }

    /// <summary>How much of a sound is left <paramref name="part"/> of the way through a crossfade that keeps the sum's power: the two parts' squares add up to 1.</summary>
    private static double EqualPower(double part) => Math.Sin(Math.Clamp(part, 0, 1) * Math.PI / 2);

    private Voice VoiceFor(Clip clip, MediaAsset asset)
    {
        if (!_voices.TryGetValue(clip.Id, out var voice) || voice.Path != asset.Path || voice.Stream != clip.Stream)
        {
            voice?.Dispose();
            voice = new Voice(asset.Path, clip.Stream);
            _voices[clip.Id] = voice;
            Repositions++;
        }

        voice.LastUsed = _block;
        return voice;
    }

    /// <summary>Opens a clip that starts soon and positions it at its start.</summary>
    private void Prepare(Clip clip, MediaAsset asset, long clipStart)
    {
        var voice = VoiceFor(clip, asset);
        if (voice.Next != clipStart || voice.Clip is not { } known || !Voice.SameReading(known, clip))
        {
            voice.Reposition(clip, clipStart, ahead: true);
        }
    }

    /// <summary>One clip being read: its file, where on the timeline it continues, and its speed's filter.</summary>
    private sealed class Voice(string path, int stream) : IDisposable
    {
        private readonly AudioStreamReader _reader = new(path, stream);
        private readonly SampleQueue _queue = new();
        private readonly float[] _chunk = new float[1024 * Channels];
        private TempoFilter? _tempo;
        private ClipSoundReader? _effects;
        private ClipSound? _effectsSound;
        private Task? _seeking;
        private RampStretcher? _stretcher;
        private bool _readerEnded;

        // A ramp's source samples: interleaved, the first at source sample _origin.
        private readonly SampleQueue _source = new();
        private long _origin;

        public string Path { get; } = path;

        public int Stream { get; } = stream;

        public int LastUsed { get; set; }

        /// <summary>The timeline sample the voice continues at (-1 before it is positioned).</summary>
        public long Next { get; private set; } = -1;

        public Clip? Clip { get; private set; }

        /// <summary>Whether what was being read for one clip goes on being right for the other (a change of level doesn't need a seek, and the sound doesn't break).</summary>
        public static bool SameReading(Clip one, Clip other) =>
            one.Id == other.Id && one.Start == other.Start && one.SourceIn == other.SourceIn && one.SourceOut == other.SourceOut && one.Speed == other.Speed && one.EndSpeed == other.EndSpeed
            && one.KeepPitch == other.KeepPitch && Equals(Effects(one), Effects(other));

        public void Render(Clip clip, long from, Span<float> block)
        {
            if (from != Next || Clip is not { } known || !SameReading(known, clip))
            {
                Reposition(clip, from);
            }

            Clip = clip;
            var frames = block.Length / Channels;
            if (_stretcher is { } stretcher)
            {
                stretcher.Render(clip, from, block);
            }
            else if (clip.IsRamp)
            {
                Ramp(clip, from, block);
            }
            else if (!clip.IsRamp && Math.Abs(clip.Speed - 1) < 1e-9)
            {
                var read = ReadSource(block);
                block[(read * Channels)..].Clear();
            }
            else
            {
                while (_queue.Frames < frames && !(_readerEnded && _tempo is null))
                {
                    var read = ReadSource(_chunk);
                    if (read > 0)
                    {
                        _tempo!.Push(_chunk.AsSpan(0, read * Channels), _queue.Append);
                    }

                    if (read < 1024)
                    {
                        _readerEnded = true;
                        _tempo?.Flush(_queue.Append);
                        _tempo?.Dispose();
                        _tempo = null;
                    }
                }

                var taken = _queue.Take(block);
                block[(taken * Channels)..].Clear();
            }

            Next = from + frames;
        }

        /// <summary>
        /// Starts reading the clip again at timeline sample <paramref name="from"/>. Getting there through a clip's effects works through
        /// seconds of sound: when <paramref name="ahead"/>, a clip that starts soon does that beside the mixing thread, so the sound
        /// already mixed doesn't run out meanwhile (the first read waits for it, and the samples are the same).
        /// </summary>
        public void Reposition(Clip clip, long from, bool ahead = false)
        {
            FinishSeeking();
            Clip = clip;
            var source = clip.SourceAt((double)from / SampleRate);
            var effects = Effects(clip);
            if (!Equals(effects, _effectsSound))
            {
                _effects?.Dispose();
                _effects = effects is null ? null : new ClipSoundReader(_reader, effects);
                _effectsSound = effects;
            }

            _origin = (long)Math.Round(source * SampleRate);
            _stretcher = clip.IsRamp && clip.KeepPitch ? new RampStretcher(ReadSource) : null;
            var seek = _stretcher?.Start(clip, from) ?? _origin;
            if (_effects is { } worked)
            {
                if (ahead)
                {
                    // A thread of its own: the pool can be slow to get to it just when the PC is busy.
                    _seeking = Task.Factory.StartNew(() => worked.Seek(seek), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                }
                else
                {
                    worked.Seek(seek);
                }
            }
            else
            {
                _reader.Seek((double)seek / SampleRate);
            }

            _queue.Clear();
            _source.Clear();
            _readerEnded = false;
            _tempo?.Dispose();
            _tempo = !clip.IsRamp && Math.Abs(clip.Speed - 1) >= 1e-9 ? new TempoFilter(clip.Speed) : null;
            Next = from;
        }

        /// <summary>A ramp: each output sample taken from the source position the clip shows then, between samples linearly.</summary>
        private void Ramp(Clip clip, long from, Span<float> block)
        {
            var frames = block.Length / Channels;
            for (var n = 0; n < frames; n++)
            {
                var position = (clip.SourceAt((double)(from + n) / SampleRate) * SampleRate) - _origin;
                var index = (long)Math.Floor(position);
                var fraction = (float)(position - index);
                while (_source.Frames < index + 2 && !_readerEnded)
                {
                    var read = ReadSource(_chunk);
                    _source.Append(_chunk.AsSpan(0, read * Channels));
                    _readerEnded = read < 1024;
                }

                if (index < 0 || index + 1 >= _source.Frames)
                {
                    block[n * Channels] = block[(n * Channels) + 1] = 0;
                    continue;
                }

                for (var c = 0; c < Channels; c++)
                {
                    var a = _source[((int)index * Channels) + c];
                    var b = _source[((int)(index + 1) * Channels) + c];
                    block[(n * Channels) + c] = a + ((b - a) * fraction);
                }
            }

            // Forget what is well behind.
            var keep = (long)Math.Floor((clip.SourceAt((double)(from + frames) / SampleRate) * SampleRate) - _origin) - 2;
            if (keep > 4096)
            {
                _source.Drop((int)keep);
                _origin += keep;
            }
        }

        public void Dispose()
        {
            try
            {
                FinishSeeking();
            }
            catch (FfmpegException)
            {
                // The file went wrong and the voice is going anyway.
            }

            _tempo?.Dispose();
            _effects?.Dispose();
            _reader.Dispose();
        }

        /// <summary>The effects to read through, when it has any that work on samples.</summary>
        private static ClipSound? Effects(Clip clip) => clip.Sound is { HasChain: true } sound ? sound : null;

        private int ReadSource(Span<float> interleaved)
        {
            FinishSeeking();
            return _effects is { } worked ? worked.Read(interleaved) : _reader.Read(interleaved);
        }

        /// <summary>Waits for a seek going on beside the mixing thread, with what went wrong in it, if anything.</summary>
        private void FinishSeeking()
        {
            var seeking = _seeking;
            _seeking = null;
            seeking?.GetAwaiter().GetResult();
        }
    }
}

/// <summary>A growable queue of interleaved stereo samples.</summary>
internal sealed class SampleQueue
{
    private float[] _buffer = new float[8192];
    private int _start;
    private int _count;

    /// <summary>Stereo frames waiting.</summary>
    public int Frames => _count / TimelineMixer.Channels;

    public float this[int index] => _buffer[_start + index];

    public void Append(ReadOnlySpan<float> samples)
    {
        if (_start + _count + samples.Length > _buffer.Length)
        {
            var target = _count + samples.Length > _buffer.Length ? new float[Math.Max(_buffer.Length * 2, _count + samples.Length)] : _buffer;
            Array.Copy(_buffer, _start, target, 0, _count);
            _buffer = target;
            _start = 0;
        }

        samples.CopyTo(_buffer.AsSpan(_start + _count));
        _count += samples.Length;
    }

    /// <summary>Moves up to the block's length of frames into it; returns how many.</summary>
    public int Take(Span<float> block)
    {
        var take = Math.Min(block.Length, _count) / TimelineMixer.Channels * TimelineMixer.Channels;
        _buffer.AsSpan(_start, take).CopyTo(block);
        _start += take;
        _count -= take;
        if (_count == 0)
        {
            _start = 0;
        }

        return take / TimelineMixer.Channels;
    }

    public void Drop(int frames)
    {
        var drop = Math.Min(frames * TimelineMixer.Channels, _count);
        _start += drop;
        _count -= drop;
    }

    public void Clear()
    {
        _start = 0;
        _count = 0;
    }
}
