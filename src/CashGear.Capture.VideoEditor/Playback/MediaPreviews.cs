using System.Collections.Concurrent;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using CashGear.Capture.Media;
using CashGear.Capture.VideoEditor.Model;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace CashGear.Capture.VideoEditor.Playback;

/// <summary>Small pictures along a video stream, each with the source time it shows.</summary>
public sealed class ThumbnailStrip(IReadOnlyList<(double Time, SKImage Image)> thumbnails, bool complete) : IDisposable
{
    public IReadOnlyList<(double Time, SKImage Image)> Thumbnails { get; } = thumbnails;

    public bool Complete { get; } = complete;

    /// <summary>The thumbnail for source time <paramref name="time"/>: the last one at or before it (or the first).</summary>
    public SKImage? At(double time)
    {
        SKImage? found = null;
        foreach (var (at, image) in Thumbnails)
        {
            if (at > time + 1e-6 && found is not null)
            {
                break;
            }

            found = image;
        }

        return found;
    }

    public void Dispose()
    {
        foreach (var (_, image) in Thumbnails)
        {
            image.Dispose();
        }
    }
}

/// <summary>A sound stream's loudness outline: the peak of each 10 ms (0 to 255, on a square-root scale so quiet speech shows).</summary>
public sealed class Waveform(byte[] peaks, bool complete)
{
    public const int PerSecond = 100;

    public byte[] Peaks { get; } = peaks;

    public bool Complete { get; } = complete;

    /// <summary>The highest peak between two source times (0 to 1).</summary>
    public double Peak(double from, double to)
    {
        var first = Math.Max(0, (int)Math.Floor(from * PerSecond));
        var last = Math.Min(Peaks.Length - 1, (int)Math.Ceiling(to * PerSecond));
        var peak = 0;
        for (var i = first; i <= last; i++)
        {
            peak = Math.Max(peak, Peaks[i]);
        }

        return peak / 255.0;
    }
}

/// <summary>
/// The timeline's thumbnails and waveforms, made in the background (one stream at a time, at a low priority) and
/// kept on disk under <c>%LocalAppData%\CashGear\Capture\VideoEditor\cache</c> by the file's path, size and time, so a
/// recording opened again shows them at once. Thread-safe; <see cref="Changed"/> is raised (on a worker thread) as
/// they grow.
/// </summary>
public sealed partial class MediaPreviews : IDisposable
{
    public const int ThumbnailHeight = 72;
    private const int MaxThumbnails = 400;
    private const int FormatVersion = 1;

    /// <summary>The thread's name, for debuggers.</summary>
    [Localizable(false)]
    private const string ThreadName = "Video editor thumbnails";

    private readonly string _folder;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, ThumbnailStrip> _thumbnails = new();
    private readonly ConcurrentDictionary<string, Waveform> _waveforms = new();
    private readonly ConcurrentDictionary<string, bool> _started = new();
    private readonly BlockingCollection<Action> _work = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;

    public MediaPreviews(string folder, ILogger logger)
    {
        _folder = folder;
        _logger = logger;
        _thread = new Thread(Run) { IsBackground = true, Name = ThreadName, Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public event EventHandler? Changed;

    /// <summary>The thumbnails made so far (null before the first), starting them if they aren't yet.</summary>
    public ThumbnailStrip? ThumbnailsOf(MediaAsset asset, int stream)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var key = Key(asset.Path, stream, "thumbs");
        if (_started.TryAdd(key, true))
        {
            _work.Add(() => MakeThumbnails(asset, stream, key));
        }

        return _thumbnails.TryGetValue(key, out var strip) ? strip : null;
    }

    /// <summary>The waveform made so far (null before any), starting it if it isn't yet.</summary>
    public Waveform? WaveformOf(MediaAsset asset, int stream)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var key = Key(asset.Path, stream, "wave");
        if (_started.TryAdd(key, true))
        {
            _work.Add(() => MakeWaveform(asset, stream, key));
        }

        return _waveforms.TryGetValue(key, out var wave) ? wave : null;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _work.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(5));
        foreach (var strip in _thumbnails.Values)
        {
            strip.Dispose();
        }

        _stop.Dispose();
        _work.Dispose();
    }

    private void Run()
    {
        try
        {
            foreach (var work in _work.GetConsumingEnumerable(_stop.Token))
            {
                try
                {
                    work();
                }
                catch (Exception e) when (e is FfmpegException or IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    LogFailed(e);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // closing
        }
    }

    private void MakeThumbnails(MediaAsset asset, int stream, string key)
    {
        var file = Path.Combine(_folder, key + ".thumbs");
        if (LoadThumbnails(file) is { } cached)
        {
            Publish(key, cached);
            return;
        }

        using var reader = new VideoStreamReader(asset.Path, stream, VideoDecoding.Software, keyframesOnly: true);
        var height = ThumbnailHeight;
        var width = Math.Max(1, (int)Math.Round((double)reader.Width * height / Math.Max(1, reader.Height)));
        var count = Math.Clamp((int)Math.Ceiling(reader.Duration / 2), 1, MaxThumbnails);
        var step = reader.Duration / count;
        var pixels = new byte[width * height * 4];
        var made = new List<(double, SKImage)>();
        double? last = null;
        for (var i = 0; i < count && !_stop.IsCancellationRequested; i++)
        {
            reader.Seek(i * step);
            if (reader.Read(pixels, width, height) is not { } time || time <= last)
            {
                continue;
            }

            last = time;
            made.Add((time, SKImage.FromPixelCopy(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul), pixels)));
            if (made.Count % 20 == 0)
            {
                Publish(key, new ThumbnailStrip([.. made.Select(m => (m.Item1, Copy(m.Item2)))], false));
            }
        }

        if (_stop.IsCancellationRequested)
        {
            made.ForEach(m => m.Item2.Dispose());
            return;
        }

        SaveThumbnails(file, made);
        Publish(key, new ThumbnailStrip(made, true));
    }

    private void MakeWaveform(MediaAsset asset, int stream, string key)
    {
        var file = Path.Combine(_folder, key + ".wave");
        if (File.Exists(file))
        {
            var bytes = File.ReadAllBytes(file);
            if (bytes.Length > 8 && BitConverter.ToInt32(bytes, 0) == FormatVersion)
            {
                Publish(key, new Waveform(bytes[8..], true));
                return;
            }
        }

        using var reader = new AudioStreamReader(asset.Path, stream);
        reader.Seek(0);
        const int bucket = AudioStreamReader.SampleRate / Waveform.PerSecond;
        var block = new float[bucket * AudioStreamReader.Channels * 100];
        var peaks = new List<byte>();
        while (!_stop.IsCancellationRequested)
        {
            var read = reader.Read(block);
            if (read == 0)
            {
                break;
            }

            for (var start = 0; start < read; start += bucket)
            {
                var peak = 0f;
                for (var i = start; i < Math.Min(read, start + bucket); i++)
                {
                    peak = Math.Max(peak, Math.Max(Math.Abs(block[2 * i]), Math.Abs(block[(2 * i) + 1])));
                }

                peaks.Add((byte)Math.Round(Math.Sqrt(Math.Min(1, peak)) * 255));
            }

            if (peaks.Count % (Waveform.PerSecond * 60) < 100)
            {
                Publish(key, new Waveform([.. peaks], false));
            }
        }

        if (_stop.IsCancellationRequested)
        {
            return;
        }

        var result = peaks.ToArray();
        Directory.CreateDirectory(_folder);
        var header = new byte[8];
        BitConverter.TryWriteBytes(header, FormatVersion);
        File.WriteAllBytes(file, [.. header, .. result]);
        Publish(key, new Waveform(result, true));
    }

    private static SKImage Copy(SKImage image)
    {
        using var bitmap = SKBitmap.FromImage(image);
        return SKImage.FromBitmap(bitmap);
    }

    /// <summary>
    /// Makes <paramref name="strip"/> the stream's thumbnails. The one it replaces isn't disposed: the timeline may be
    /// drawing it right now, so its images are left to the garbage collector.
    /// </summary>
    private void Publish(string key, ThumbnailStrip strip)
    {
        _thumbnails[key] = strip;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Publish(string key, Waveform wave)
    {
        _waveforms[key] = wave;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A cache file name for a stream of a file as it is now (its size and time are part of it).</summary>
    private static string Key(string path, int stream, string kind)
    {
        var info = new FileInfo(path);
        var text = FormattableString.Invariant($"{Path.GetFullPath(path).ToUpperInvariant()}|{(info.Exists ? info.Length : 0)}|{(info.Exists ? info.LastWriteTimeUtc.Ticks : 0)}|{stream}|{kind}|{FormatVersion}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..24];
    }

    private ThumbnailStrip? LoadThumbnails(string file)
    {
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            using var reader = new BinaryReader(File.OpenRead(file));
            if (reader.ReadInt32() != FormatVersion)
            {
                return null;
            }

            var count = reader.ReadInt32();
            var thumbnails = new List<(double, SKImage)>(count);
            for (var i = 0; i < count; i++)
            {
                var time = reader.ReadDouble();
                var bytes = reader.ReadBytes(reader.ReadInt32());
                if (SKImage.FromEncodedData(bytes) is { } image)
                {
                    thumbnails.Add((time, image));
                }
            }

            return new ThumbnailStrip(thumbnails, true);
        }
        catch (EndOfStreamException)
        {
            return null;
        }
    }

    private static void SaveThumbnails(string file, List<(double Time, SKImage Image)> thumbnails)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var partial = file + ".partial";
        using (var writer = new BinaryWriter(File.Create(partial)))
        {
            writer.Write(FormatVersion);
            writer.Write(thumbnails.Count);
            foreach (var (time, image) in thumbnails)
            {
                using var data = image.Encode(SKEncodedImageFormat.Jpeg, 80);
                writer.Write(time);
                writer.Write((int)data.Size);
                writer.Write(data.AsSpan());
            }
        }

        File.Move(partial, file, overwrite: true);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Video editor: thumbnails or a waveform couldn't be made")]
    private partial void LogFailed(Exception e);
}
