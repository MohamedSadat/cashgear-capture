using CashGear.Capture.Core.Audio;
using CashGear.Capture.Media;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Playback;

namespace CashGear.Capture.VideoEditor.Export;

/// <summary>How an export keeps its sound tracks apart.</summary>
public enum SoundTracksOut
{
    /// <summary>One track, the mix.</summary>
    Mix,

    /// <summary>In the video file: the mix, then each track as a stream of its own, titled with its name.</summary>
    Streams,

    /// <summary>Beside the file: each track as a WAV file, named for it.</summary>
    WavFiles,
}

/// <summary>What the loudness pass found: the mix's loudness as it was, and the change that brings it to the target.</summary>
internal sealed record LoudnessFound(double MeasuredLufs, double GainDb);

/// <summary>The loudness targets exports offer, and finding the change that meets one.</summary>
public static class ExportLoudness
{
    public const double Online = Loudness.TargetLufs;

    public const double YouTube = -14;

    public const double Broadcast = -23;

    /// <summary>No sample of the result goes above this when a target is met: a bit below full scale, for what a lossy encoder adds.</summary>
    public const double CeilingDb = -1;

    /// <summary>
    /// The gain that brings the mix to <paramref name="target"/> LUFS: the whole mix turned up or down. Where that would pass the ceiling a
    /// limiter holds the peaks, which takes a little loudness away, so it is measured again through the limiter and made up for.
    /// Null for silence, which is left as it is.
    /// </summary>
    internal static LoudnessFound? Find(VideoProject project, double target, Action<double>? progress, CancellationToken ct)
    {
        var first = Measure(project, 0, limit: false, f => progress?.Invoke(f / 2), ct);
        var measured = first.IntegratedLufs;
        if (!double.IsFinite(measured) || measured <= Loudness.QuietestLufs)
        {
            return null;
        }

        var gain = target - measured;
        if (first.PeakDb + gain > CeilingDb)
        {
            var limited = Measure(project, gain, limit: true, f => progress?.Invoke(0.5 + (f / 2)), ct).IntegratedLufs;
            if (double.IsFinite(limited))
            {
                gain += Math.Clamp(target - limited, -6, 6);
            }
        }

        return new LoudnessFound(measured, gain);
    }

    private static LoudnessMeter Measure(VideoProject project, double gainDb, bool limit, Action<double> progress, CancellationToken ct)
    {
        var meter = new LoudnessMeter();
        var total = TimelineMixer.Sample(project.Duration);
        var block = new float[TimelineMixer.SampleRate * TimelineMixer.Channels / 5];
        using var sound = new ExportSoundSource(project, gainDb, limit);
        for (var at = 0L; at < total; at += block.Length / TimelineMixer.Channels)
        {
            ct.ThrowIfCancellationRequested();
            var samples = block.AsSpan(0, (int)Math.Min(block.Length / TimelineMixer.Channels, total - at) * TimelineMixer.Channels);
            sound.Read(samples);
            meter.Add(samples);
            progress((double)at / total);
        }

        return meter;
    }
}

/// <summary>
/// The sound of a project for an export, read forward: the mix itself, or the mix turned up or down for a loudness target, with
/// a limiter at <see cref="ExportLoudness.CeilingDb"/>, whose delay is taken out so the sound stays with the pictures.
/// </summary>
internal sealed class ExportSoundSource : IDisposable
{
    private const int Channels = TimelineMixer.Channels;

    private readonly VideoProject _project;
    private readonly TimelineMixer _mixer;
    private readonly bool _plain;
    private readonly float _gain;
    private readonly Limiter? _limiter;
    private readonly SampleQueue _ready = new();
    private readonly float[] _block = new float[1024 * Channels];
    private long _mixedTo;
    private int _delay;

    /// <param name="gainDb">Null: the mix as it is, kept within full scale.</param>
    /// <param name="limit">The limiter is on (else nothing holds the peaks: for measuring).</param>
    public ExportSoundSource(VideoProject project, double? gainDb = null, bool limit = true)
    {
        _project = project;
        _plain = gainDb is null;
        _mixer = new TimelineMixer { Clamps = _plain };
        _gain = Decibels.ToGain(gainDb ?? 0);
        _limiter = gainDb is not null && limit ? new Limiter(ExportLoudness.CeilingDb) : null;
        _delay = _limiter?.Latency ?? 0;
    }

    /// <summary>The next samples.</summary>
    public void Read(Span<float> output)
    {
        if (_plain)
        {
            _mixer.Mix(_project, _mixedTo, output);
            _mixedTo += output.Length / Channels;
            return;
        }

        while (_ready.Frames < output.Length / Channels)
        {
            _mixer.Mix(_project, _mixedTo, _block);
            _mixedTo += _block.Length / Channels;
            for (var i = 0; i < _block.Length; i++)
            {
                _block[i] *= _gain;
            }

            _limiter?.Process(_block);
            var skip = Math.Min(_delay, _block.Length / Channels);
            _delay -= skip;
            _ready.Append(_block.AsSpan(skip * Channels));
        }

        _ready.Take(output);
    }

    public void Dispose() => _mixer.Dispose();
}

/// <summary>One sound stream of an export being encoded: where its samples come from, its encoder and the file it goes to.</summary>
internal sealed class ExportSoundStream(AudioEncoder encoder, ExportSoundSource source, ExportWriter writer) : IDisposable
{
    private readonly float[] _block = new float[encoder.FrameSize * TimelineMixer.Channels];
    private readonly List<EncodedPacket> _packets = [];

    public AudioEncoder Encoder { get; } = encoder;

    /// <summary>The next sample to encode.</summary>
    public long Pts { get; private set; }

    /// <summary>Whole encoder frames up to a time; only the very last frame of the export may be shorter.</summary>
    public void Until(long until, long total)
    {
        while (Pts < until && (Pts + Encoder.FrameSize <= until || until == total))
        {
            var count = (int)Math.Min(Encoder.FrameSize, until - Pts);
            var samples = _block.AsSpan(0, count * TimelineMixer.Channels);
            source.Read(samples);
            Encoder.Encode(samples, Pts, _packets);
            Pts += count;
            Send();
        }
    }

    public void Flush()
    {
        Encoder.Flush(_packets);
        Send();
    }

    public void Dispose()
    {
        _packets.ForEach(p => p.Dispose());
        _packets.Clear();
        source.Dispose();
        Encoder.Dispose();
    }

    private void Send()
    {
        _packets.ForEach(writer.Write);
        _packets.Clear();
    }
}
