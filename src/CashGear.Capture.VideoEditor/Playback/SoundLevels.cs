using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using CashGear.Capture.VideoEditor.Model;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.VideoEditor.Playback;

/// <summary>
/// How loud a sound stream is, and where it is voiced, every 10 ms on its own clock: what word times are refined with,
/// and what silences and unworded sounds are found in. Frame <c>f</c> is centred on <c>f / 100</c> s.
/// </summary>
public sealed class SoundLevels
{
    public const int PerSecond = 100;

    private const int Hop = TranscriptAudio.SampleRate / PerSecond;

    /// <summary>A frame's level in decibels below full scale, no lower than this.</summary>
    private const double Lowest = -100;

    /// <summary>Sound below this level, in decibels, is silence in any recording.</summary>
    private const double Inaudible = -70;

    private const int FileMagic = 0x4C564743; // "CGVL"
    private const int FileVersion = 2;

    /// <summary>The levels, in decibels.</summary>
    private readonly float[] _levels;

    /// <summary>Whether the sound has a pitch there, as a voice does (a click or a fan doesn't).</summary>
    private readonly bool[] _voiced;

    /// <summary>The levels with the high frequencies made more of, in decibels: an "s" is as loud as a vowel in them, so the gap between words shows.</summary>
    private readonly float[] _sharp;

    /// <param name="sharp">The levels with the high frequencies made more of (null: the levels themselves).</param>
    public SoundLevels(float[] levels, bool[] voiced, float[]? sharp = null)
    {
        ArgumentNullException.ThrowIfNull(levels);
        ArgumentNullException.ThrowIfNull(voiced);
        _levels = levels;
        _voiced = voiced;
        _sharp = sharp ?? levels;
        var sorted = levels.Order().ToArray();
        Floor = sorted.Length == 0 ? Lowest : sorted[sorted.Length / 20];
        Loud = sorted.Length == 0 ? Lowest : sorted[(int)(sorted.Length * 0.95)];

        // Just above the noise, but well below the speech; a level too low to hear is always quiet.
        QuietBelow = Math.Max(Math.Max(Math.Min(Floor + 6, Loud - 10), Loud - 35), Inaudible);
    }

    public int Count => _levels.Length;

    /// <summary>The noise under the speech: the level the quietest twentieth of the sound stays under.</summary>
    public double Floor { get; }

    /// <summary>The level of loud speech: what all but the loudest twentieth of the sound stays under.</summary>
    public double Loud { get; }

    /// <summary>Below this level is silence for this stream.</summary>
    public double QuietBelow { get; }

    public double Duration => (double)_levels.Length / PerSecond;

    /// <summary>The frame at or nearest source time <paramref name="seconds"/>.</summary>
    public int FrameAt(double seconds) => _levels.Length == 0 ? 0 : Math.Clamp((int)Math.Round(seconds * PerSecond), 0, _levels.Length - 1);

    public double Level(int frame) => _levels.Length == 0 ? Lowest : _levels[Math.Clamp(frame, 0, _levels.Length - 1)];

    /// <summary>The frame's level with the high frequencies made more of: for finding the gap between words.</summary>
    public double SharpLevel(int frame) => _sharp.Length == 0 ? Lowest : _sharp[Math.Clamp(frame, 0, _sharp.Length - 1)];

    public bool IsVoiced(int frame) => _voiced.Length != 0 && _voiced[Math.Clamp(frame, 0, _voiced.Length - 1)];

    public bool IsQuiet(int frame) => Level(frame) < QuietBelow;

    /// <summary>The levels of <paramref name="samples"/> (mono, <see cref="TranscriptAudio.SampleRate"/> Hz).</summary>
    public static SoundLevels FromSamples(float[] samples, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var count = samples.Length / Hop;
        var levels = new float[count];
        var pitched = new bool[count];
        var sharp = new float[count];
        const int half = Hop; // 20 ms around each frame's time
        Parallel.For(0, count, new ParallelOptions { CancellationToken = ct }, frame =>
        {
            var from = Math.Max(0, (frame * Hop) - half);
            var to = Math.Min(samples.Length, (frame * Hop) + half);
            double sum = 0;
            double sharpSum = 0;
            for (var i = from; i < to; i++)
            {
                sum += samples[i] * samples[i];
                var change = i > 0 ? samples[i] - (0.95f * samples[i - 1]) : samples[i];
                sharpSum += change * change;
            }

            levels[frame] = (float)Math.Max(Lowest, 10 * Math.Log10((sum / Math.Max(1, to - from)) + 1e-12));
            sharp[frame] = (float)Math.Max(Lowest, 10 * Math.Log10((sharpSum / Math.Max(1, to - from)) + 1e-12));
            pitched[frame] = HasPitch(samples, frame * Hop);
        });

        // Voiced means a pitch in sound that isn't near the floor: a pitch in the noise is the mains' hum.
        var result = new SoundLevels(levels, pitched, sharp);
        var voiced = new bool[count];
        for (var frame = 0; frame < count; frame++)
        {
            voiced[frame] = pitched[frame] && levels[frame] >= result.QuietBelow;
        }

        return new SoundLevels(levels, voiced, sharp);
    }

    /// <summary>
    /// Whether the 40 ms around <paramref name="center"/> repeats itself at a voice's pitch (70 to 400 Hz): the highest
    /// normalized autocorrelation of the sound taken down to 8 kHz.
    /// </summary>
    private static bool HasPitch(float[] samples, int center)
    {
        const int factor = 2;
        const int length = 40 * (TranscriptAudio.SampleRate / 1000) / factor;
        const int shortestLag = TranscriptAudio.SampleRate / factor / 400;
        const int longestLag = TranscriptAudio.SampleRate / factor / 70;
        var from = center - (length * factor / 2);
        if (from < 0 || from + ((length + longestLag) * factor) > samples.Length)
        {
            return false;
        }

        Span<float> low = stackalloc float[length + longestLag];
        for (var i = 0; i < low.Length; i++)
        {
            low[i] = (samples[from + (i * factor)] + samples[from + (i * factor) + 1]) / 2;
        }

        double own = 0;
        for (var i = 0; i < length; i++)
        {
            own += low[i] * low[i];
        }

        if (own < 1e-9)
        {
            return false;
        }

        for (var lag = shortestLag; lag <= longestLag; lag++)
        {
            double together = 0;
            double other = 0;
            for (var i = 0; i < length; i++)
            {
                together += low[i] * low[i + lag];
                other += low[i + lag] * low[i + lag];
            }

            if (together > 0 && together * together > 0.8 * 0.8 * own * other)
            {
                return true; // correlation over 0.8
            }
        }

        return false;
    }

    internal void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var partial = path + ".partial";
        using (var writer = new BinaryWriter(File.Create(partial)))
        {
            writer.Write(FileMagic);
            writer.Write(FileVersion);
            writer.Write(_levels.Length);
            foreach (var level in _levels)
            {
                writer.Write((short)Math.Round(level * 100));
            }

            foreach (var voiced in _voiced)
            {
                writer.Write(voiced);
            }

            foreach (var level in _sharp)
            {
                writer.Write((short)Math.Round(level * 100));
            }
        }

        File.Move(partial, path, overwrite: true);
    }

    internal static SoundLevels? Load(string path)
    {
        try
        {
            using var reader = new BinaryReader(File.OpenRead(path));
            if (reader.ReadInt32() != FileMagic || reader.ReadInt32() != FileVersion)
            {
                return null;
            }

            var count = reader.ReadInt32();
            var levels = new float[count];
            for (var i = 0; i < count; i++)
            {
                levels[i] = reader.ReadInt16() / 100f;
            }

            var voiced = new bool[count];
            for (var i = 0; i < count; i++)
            {
                voiced[i] = reader.ReadBoolean();
            }

            var sharp = new float[count];
            for (var i = 0; i < count; i++)
            {
                sharp[i] = reader.ReadInt16() / 100f;
            }

            return new SoundLevels(levels, voiced, sharp);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// The sound levels of streams, worked out once and kept on disk by the file's path, size and time, beside the
/// thumbnails and waveforms; thread-safe.
/// </summary>
public sealed partial class SoundLevelStore(string folder, ILogger<SoundLevelStore> logger)
{
    private readonly ConcurrentDictionary<string, SoundLevels> _known = new();

    /// <summary>What is known of a stream already, without working anything out.</summary>
    public SoundLevels? Known(MediaAsset asset, int stream)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return Key(asset, stream) is { } key && (_known.TryGetValue(key, out var levels) || TryLoad(key, out levels)) ? levels : null;
    }

    /// <summary>Keeps the levels of sound already heard.</summary>
    public SoundLevels Keep(MediaAsset asset, int stream, float[] samples, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var levels = SoundLevels.FromSamples(samples, ct);
        if (Key(asset, stream) is { } key)
        {
            _known[key] = levels;
            try
            {
                levels.Save(PathOf(key));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                LogNotSaved(e, asset.Name);
            }
        }

        return levels;
    }

    /// <summary>The stream's levels, worked out from its file when they aren't known.</summary>
    /// <exception cref="Media.FfmpegException">The file can't be read.</exception>
    public async Task<SoundLevels> GetAsync(MediaAsset asset, int stream, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (Known(asset, stream) is { } known)
        {
            return known;
        }

        return await Task.Run(() => Keep(asset, stream, TranscriptAudio.RenderStream(asset, stream, progress, ct), ct), ct).ConfigureAwait(false);
    }

    private bool TryLoad(string key, out SoundLevels levels)
    {
        var loaded = File.Exists(PathOf(key)) ? SoundLevels.Load(PathOf(key)) : null;
        levels = loaded!;
        if (loaded is null)
        {
            return false;
        }

        _known[key] = loaded;
        return true;
    }

    private string PathOf(string key) => Path.Combine(folder, key + ".levels");

    /// <summary>A name for a stream's levels: its file's path, size and time, so a changed file is measured again. Null when the file is gone.</summary>
    private static string? Key(MediaAsset asset, int stream)
    {
        var info = new FileInfo(asset.Path);
        if (!info.Exists)
        {
            return null;
        }

        var text = FormattableString.Invariant($"{asset.Path.ToUpperInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{stream}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..24];
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The sound levels of {Name} couldn't be saved")]
    private partial void LogNotSaved(Exception e, string name);
}
