using System.Diagnostics;
using CashGear.Capture.Core.Audio;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.Audio;

/// <summary>Removes background noise from 480-frame blocks of 48 kHz stereo (RNNoise).</summary>
public interface INoiseSuppressor : IDisposable
{
    /// <summary>How many frames the output lags the input; the same whether it is enabled or not.</summary>
    int Latency { get; }

    /// <summary>
    /// Processes one block of <see cref="MicrophoneChain.BlockFrames"/> frames in place. Disabled, the sound passes
    /// unchanged (but still delayed by <see cref="Latency"/>); <paramref name="strength"/> 0 to 1 mixes in the original.
    /// </summary>
    void Process(Span<float> block, bool enabled, float strength);
}

/// <summary>
/// The microphone's processing on its way to the recording: noise suppression, noise gate, compressor and limiter
/// (see <see cref="MicrophoneProcessing"/>), in blocks of 10 ms. The packets that go in keep their timestamps: each
/// block that comes out is stamped with the time its sound was captured (from a <see cref="StampLedger"/>), its
/// delay through the chain taken off. The delay is fixed when the chain is made, so turning a stage on or off while
/// recording never moves the sound in time. Settings can change at any time (from any thread); they apply from the
/// next block. Not thread-safe otherwise: one capture thread feeds it.
/// </summary>
public sealed class MicrophoneChain : IDisposable
{
    /// <summary>RNNoise's frame: 10 ms at 48 kHz.</summary>
    public const int BlockFrames = 480;

    private readonly INoiseSuppressor? _noise;
    private readonly NoiseGate _gate = new();
    private readonly Compressor _compressor = new();
    private readonly Limiter _limiter = new();
    private readonly StampLedger _ledger;
    private readonly long _frequency;
    private readonly float[] _block = new float[BlockFrames * AudioMixer.Channels];
    private readonly StampedRun[] _runs = new StampedRun[8];
    private int _filled;
    private long _processed;
    private MicrophoneProcessing _settings;

    public MicrophoneChain(MicrophoneProcessing settings, INoiseSuppressor? noise = null, long? frequency = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _noise = noise;
        _frequency = frequency ?? Stopwatch.Frequency;
        _ledger = new StampLedger(_frequency);
        Latency = (noise?.Latency ?? 0) + _limiter.Latency;
    }

    /// <summary>How many frames the sound is held back in total (removed again from the timestamps).</summary>
    public int Latency { get; }

    /// <summary>Whether noise suppression is available in this chain (RNNoise was loaded).</summary>
    public bool CanSuppressNoise => _noise is not null;

    public MicrophoneProcessing Settings
    {
        get => Volatile.Read(ref _settings);
        set => Volatile.Write(ref _settings, value ?? throw new ArgumentNullException(nameof(value)));
    }

    /// <summary>Takes a packet (interleaved stereo, first frame captured at <paramref name="start"/>) and hands on every block it completes.</summary>
    public void Process(ReadOnlySpan<float> samples, long start, bool discontinuity, AudioDataHandler output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _ledger.Add(samples.Length / AudioMixer.Channels, start, discontinuity);
        while (!samples.IsEmpty)
        {
            var take = Math.Min(samples.Length, _block.Length - _filled);
            samples[..take].CopyTo(_block.AsSpan(_filled));
            _filled += take;
            samples = samples[take..];
            if (_filled == _block.Length)
            {
                ProcessBlock(output);
                _filled = 0;
            }
        }
    }

    /// <summary>
    /// Processes a whole recording at once (the microphone test): the result has the same length, with the chain's
    /// delay taken out. Needs a new chain made with a <c>frequency</c> of 48000, so a timestamp is a frame number.
    /// </summary>
    public float[] ProcessAll(ReadOnlySpan<float> samples)
    {
        if (_ledger.Frames != 0 || _frequency != AudioMixer.SampleRate)
        {
            throw new InvalidOperationException("ProcessAll needs a new chain made with a frequency of 48000.");
        }

        var frames = samples.Length / AudioMixer.Channels;
        var result = new float[frames * AudioMixer.Channels];
        void Collect(ReadOnlySpan<float> block, long stamp, bool discontinuity)
        {
            var at = (int)stamp * AudioMixer.Channels;
            var count = Math.Min(block.Length, result.Length - at);
            if (count > 0)
            {
                block[..count].CopyTo(result.AsSpan(at));
            }
        }

        Process(samples, 0, discontinuity: false, Collect);
        var flush = new float[(Latency + BlockFrames) * AudioMixer.Channels];
        Process(flush, frames, discontinuity: false, Collect);
        return result;
    }

    public void Dispose() => _noise?.Dispose();

    private void ProcessBlock(AudioDataHandler output)
    {
        var settings = Settings;
        var block = _block.AsSpan();
        _noise?.Process(block, settings.NoiseSuppression, settings.NoiseSuppressionStrength / 100f);
        if (settings.NoiseGate)
        {
            _gate.ThresholdDb = settings.GateThresholdDb;
            _gate.Process(block);
        }

        if (settings.Compressor)
        {
            _compressor.Process(block);
        }

        _limiter.Enabled = settings.Limiter;
        _limiter.Process(block);

        // The block holds the sound that went in Latency frames before it; the first Latency frames ever are the
        // chain filling up, not sound.
        var content = _processed - Latency;
        _processed += BlockFrames;
        var skip = (int)Math.Clamp(-content, 0, BlockFrames);
        if (skip == BlockFrames)
        {
            return;
        }

        var from = content + skip;
        var count = _ledger.Runs(from, BlockFrames - skip, _runs);
        for (var i = 0; i < count; i++)
        {
            var run = _runs[i];
            output(block.Slice((skip + run.Offset) * AudioMixer.Channels, run.Count * AudioMixer.Channels), run.Stamp, run.Discontinuity);
        }

        _ledger.Forget(from);
    }
}
