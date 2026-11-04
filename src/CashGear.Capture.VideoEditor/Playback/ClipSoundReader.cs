using CashGear.Capture.Audio;
using CashGear.Capture.Core.Audio;
using CashGear.Capture.Media;
using CashGear.Capture.VideoEditor.Model;

namespace CashGear.Capture.VideoEditor.Playback;

/// <summary>
/// Reads a sound stream through a clip's effects, as what <see cref="TimelineMixer"/> plays. What comes out depends only on the
/// settings and the sample's place in the stream: not on where playing started, on how the clip was cut, or on the size of the
/// blocks asked for. So a preview after any seek is the export's samples, and a split clip plays on as if it weren't split.
/// </summary>
/// <remarks>
/// A gate, a compressor and RNNoise all remember what went before, so to be a function of the place alone their memory has to
/// start somewhere fixed. The stream is divided into <see cref="SegmentFrames"/> stretches from its own start, each with a
/// chain of its own that starts afresh <see cref="PreRollFrames"/> before the stretch (longer than any of them remembers
/// enough to hear) and goes over to the next stretch's chain in the last <see cref="BlendFrames"/> frames. A seek
/// starts the chain of its stretch, so it costs at most a stretch and its pre-roll; playing on costs the two chains running
/// side by side for the pre-roll of each stretch. The chains' own delay is taken out: the sound stays where it was.
/// </remarks>
internal sealed class ClipSoundReader : IDisposable
{
    public const int Block = MicrophoneChain.BlockFrames;

    /// <summary>Five seconds: long enough that the second chain runs only part of the time, short enough that a seek stays quick.</summary>
    public const int SegmentFrames = 500 * Block;

    /// <summary>Two seconds, of which RNNoise's recurrent state is the longest memory by far.</summary>
    public const int PreRollFrames = 200 * Block;

    public const int BlendFrames = Block;

    private const int Channels = TimelineMixer.Channels;

    private readonly AudioStreamReader _reader;
    private readonly ClipSound _sound;
    private readonly List<Run> _runs = [];
    private readonly float[] _input = new float[Block * Channels];
    private readonly SampleQueue _ready = new();
    private readonly int _latency;
    private long _inputAt;
    private long _next;
    private long? _end;

    public ClipSoundReader(AudioStreamReader reader, ClipSound sound)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(sound);
        _reader = reader;
        _sound = sound;
        using var measure = new Chain(sound);
        _latency = measure.Latency;
    }

    /// <summary>Whether RNNoise is there, for the panel to say what a clip's noise removal does: without it, nothing.</summary>
    public static bool CanRemoveNoise => RnnoiseLibrary.IsAvailable;

    /// <summary>The sample the next <see cref="Read"/> starts at.</summary>
    public long Position => _next;

    /// <summary>Goes to <paramref name="frame"/> of the stream; its stretch is worked through at once, so this is where a seek waits.</summary>
    public void Seek(long frame)
    {
        frame = Math.Max(0, frame);
        foreach (var run in _runs)
        {
            run.Chain.Dispose();
        }

        _runs.Clear();
        _ready.Clear();
        _end = null;
        _next = frame;
        _inputAt = StartOf(frame / SegmentFrames);
        _reader.Seek((double)_inputAt / TimelineMixer.SampleRate);
        while (!ReadyFor(1))
        {
            Pump();
        }
    }

    /// <summary>The next samples (pairs of L, R); returns how many pairs, fewer than asked only at the end of the stream.</summary>
    public int Read(Span<float> interleaved)
    {
        var wanted = interleaved.Length / Channels;
        while (!ReadyFor(wanted))
        {
            Pump();
        }

        var taken = _ready.Take(interleaved);
        _next += taken;
        return taken;
    }

    public void Dispose()
    {
        foreach (var run in _runs)
        {
            run.Chain.Dispose();
        }

        _runs.Clear();
    }

    /// <summary>Where the chain of a stretch starts: its pre-roll before it, on a block's edge, and never before the stream.</summary>
    private static long StartOf(long segment) => Math.Max(0, (segment * SegmentFrames) - PreRollFrames);

    private bool ReadyFor(int frames)
    {
        if (_ready.Frames >= frames)
        {
            return true;
        }

        // Nothing more will come once the output has reached the end of what was read.
        return _end is { } end && _inputAt - _latency >= end;
    }

    /// <summary>One block through every chain running, and what that makes of the output.</summary>
    private void Pump()
    {
        var segment = Math.Max(Math.Max(0, _inputAt - _latency), _next + _ready.Frames) / SegmentFrames;
        foreach (var run in _runs.Where(r => _inputAt >= ((r.Segment + 1) * SegmentFrames) + _latency).ToList())
        {
            run.Chain.Dispose();
            _runs.Remove(run);
        }

        // This stretch's chain, and the next one's when its pre-roll has begun.
        for (var s = segment; StartOf(s) <= _inputAt && s <= segment + 1; s++)
        {
            if (_runs.All(r => r.Segment != s))
            {
                _runs.Add(new Run(s, new Chain(_sound)));
            }
        }

        var read = _end is null ? _reader.Read(_input) : 0;
        if (_end is null && read < Block)
        {
            _end = _inputAt + read;
        }

        _input.AsSpan(read * Channels).Clear();
        foreach (var run in _runs)
        {
            _input.CopyTo(run.Output, 0);
            run.Chain.Process(run.Output);
        }

        var first = _inputAt - _latency;
        Span<float> frame = stackalloc float[Channels];
        for (var n = 0; n < Block; n++)
        {
            var at = first + n;
            if (at < _next + _ready.Frames || (_end is { } end && at >= end))
            {
                continue;
            }

            Mix(at, n, frame);
            _ready.Append(frame);
        }

        _inputAt += Block;
    }

    /// <summary>A frame of the output: its stretch's chain, going over to the next one's at the stretch's end.</summary>
    private void Mix(long at, int n, Span<float> frame)
    {
        var segment = at / SegmentFrames;
        var into = at - (segment * SegmentFrames);
        Run? here = null;
        Run? next = null;
        foreach (var run in _runs)
        {
            if (run.Segment == segment)
            {
                here = run;
            }
            else if (run.Segment == segment + 1)
            {
                next = run;
            }
        }

        var chain = here ?? next;
        if (chain is null)
        {
            frame.Clear();
            return;
        }

        var over = into >= SegmentFrames - BlendFrames && here is not null && next is not null ? (float)(into - (SegmentFrames - BlendFrames) + 1) / (BlendFrames + 1) : 0f;
        for (var c = 0; c < Channels; c++)
        {
            var value = chain.Output[(n * Channels) + c];
            frame[c] = over > 0 ? value + ((next!.Output[(n * Channels) + c] - value) * over) : value;
        }
    }

    private sealed class Run(long segment, Chain chain)
    {
        public long Segment { get; } = segment;

        public Chain Chain { get; } = chain;

        public float[] Output { get; } = new float[Block * Channels];
    }

    /// <summary>The effects, one run of them from a fresh start: the very stages of the microphone's chain, plus the equalizer.</summary>
    private sealed class Chain : IDisposable
    {
        private readonly RnnoiseSuppressor? _noise;
        private readonly float _strength;
        private readonly Equalizer? _eq;
        private readonly NoiseGate? _gate;
        private readonly float _gain;
        private readonly Compressor? _compressor;
        private readonly Limiter? _limiter;

        public Chain(ClipSound sound)
        {
            _noise = sound.NoiseRemoval && RnnoiseLibrary.IsAvailable ? new RnnoiseSuppressor() : null;
            _strength = (float)Math.Clamp(sound.NoiseStrength, 0, 1);
            _eq = sound.Eq is { IsFlat: false } eq ? new Equalizer(eq.HighPassHz, eq.LowDb, eq.MidDb, eq.HighDb) : null;
            _gate = sound.Gate ? new NoiseGate(sound.GateThresholdDb) : null;
            _gain = Decibels.ToGain(sound.GainDb);
            _compressor = sound.Compressor ? new Compressor(sound.CompressorThresholdDb, sound.CompressorRatio) : null;
            _limiter = sound.Limiter ? new Limiter(sound.LimiterCeilingDb) : null;
            Latency = (_noise?.Latency ?? 0) + (_limiter?.Latency ?? 0);
        }

        public int Latency { get; }

        public void Process(Span<float> block)
        {
            _noise?.Process(block, enabled: true, _strength);
            _eq?.Process(block);
            _gate?.Process(block);
            if (_gain != 1)
            {
                for (var i = 0; i < block.Length; i++)
                {
                    block[i] *= _gain;
                }
            }

            _compressor?.Process(block);
            _limiter?.Process(block);
        }

        public void Dispose() => _noise?.Dispose();
    }
}
