using System.Collections.Concurrent;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Media;
using CashGear.Capture.VideoEditor.Model;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.VideoEditor.Playback;

/// <summary>Where the preview reads a stream from, when a smaller copy of it is to be used.</summary>
public interface IProxyResolver
{
    /// <summary>The smaller copy's file for a stream (whose only video stream is 0), or null to read the original.</summary>
    string? PathFor(MediaAsset asset, int stream);
}

/// <summary>
/// Smaller copies of large media for the preview, kept in <c>%LocalAppData%\CashGear\Capture\VideoEditor\proxies</c> by the
/// file's path, size and time: every picture at its own time, with a keyframe each second so scrubbing is quick. The
/// export never reads them. Nothing leaves the PC.
/// </summary>
public sealed partial class ProxyStore(string folder, IRecordingEncoders encoders, ILogger<ProxyStore> logger) : IProxyResolver
{
    /// <summary>The heights offered, and what a stream must be taller than to want one.</summary>
    public const int Off = 0;

    public const int Height1080 = 1080;

    public const int Height720 = 720;

    /// <summary>A file's copy, to be made: it is named with its source's identity, so a changed file makes a new one.</summary>
    [Localizable(false)]
    private const string ProxyExtension = ".mp4";

    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _working = new();

    /// <summary>The height copies are used at (<see cref="Off"/>: never).</summary>
    public int Height { get; set; }

    /// <summary>Whether a stream is taller than the copies are to be.</summary>
    public static bool Wants(AssetStream? stream, int height) => height > 0 && stream is { Kind: StreamKind.Video, Height: > 0 } && stream.Height > height;

    public string? PathFor(MediaAsset asset, int stream)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (Height <= 0 || asset.IsPicture || !Wants(asset.Stream(stream), Height))
        {
            return null;
        }

        var file = PathOf(asset, stream, Height);
        return file is not null && File.Exists(file) ? file : null;
    }

    /// <summary>Whether the copy is there.</summary>
    public bool IsReady(MediaAsset asset, int stream, int height)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return PathOf(asset, stream, height) is { } file && File.Exists(file);
    }

    /// <summary>Makes the copy if it isn't there yet; one at a time for each stream, whoever asks.</summary>
    /// <exception cref="FfmpegException">It can't be read or encoded.</exception>
    public Task<string> EnsureAsync(MediaAsset asset, int stream, int height, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var target = PathOf(asset, stream, height) ?? throw new FileNotFoundException(asset.Path);
        if (File.Exists(target))
        {
            return Task.FromResult(target);
        }

        var work = _working.GetOrAdd(target, _ => new Lazy<Task<string>>(() => Task.Factory.StartNew(() => MakeAsync(asset, stream, height, target, progress, ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap()));
        return Finish(target, work.Value);
    }

    private async Task<string> Finish(string target, Task<string> work)
    {
        try
        {
            return await work.ConfigureAwait(false);
        }
        finally
        {
            _working.TryRemove(target, out _);
        }
    }

    private async Task<string> MakeAsync(MediaAsset asset, int stream, int height, string target, IProgress<double>? progress, CancellationToken ct)
    {
        var detected = await encoders.DetectAsync(ct: ct).ConfigureAwait(false);
        Directory.CreateDirectory(folder);
        var partial = target + ".partial";
        LogMaking(asset.Name, stream, height);
        try
        {
            Write(asset, stream, height, partial, detected, progress, ct);
            File.Move(partial, target, overwrite: true);
            return target;
        }
        finally
        {
            if (File.Exists(partial))
            {
                File.Delete(partial);
            }
        }
    }

    private static unsafe void Write(MediaAsset asset, int stream, int height, string partial, IReadOnlyList<EncoderProbe> detected, IProgress<double>? progress, CancellationToken ct)
    {
        var info = asset.Stream(stream)!;
        var fps = info.FrameRate > 0 ? (int)Math.Clamp(Math.Round(info.FrameRate), 1, 240) : 30;
        var width = Math.Max(2, (int)Math.Round((double)info.Width * height / info.Height / 2) * 2);
        height = Math.Max(2, height / 2 * 2);
        var format = new VideoFormat(width, height, fps);
        var options = (IEnumerable<KeyValuePair<string, string>> o) => o.Where(p => p.Key != GopOption).Append(new KeyValuePair<string, string>(GopOption, fps.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToList();
        VideoEncoder? video = null;
        foreach (var (candidate, probe) in EncoderPresets.ForExport(VideoCodec.H264, null, detected))
        {
            try
            {
                video = VideoEncoder.Open(candidate, format, options(EncoderPresets.Options(candidate, QualityPreset.Balanced, width, height, fps, probe.QualityMode)));
                break;
            }
            catch (FfmpegException)
            {
                // the next one
            }
        }

        if (video is null)
        {
            throw new InvalidOperationException($"No H.264 encoder for a {width} x {height} proxy.");
        }

        using (video)
        {
            using var writer = new ExportWriter(partial, ExportContainer.Mp4);
            writer.Add(video);
            using var cursor = new FrameCursor(asset.Path, stream, VideoDecoding.Hardware, width, height);
            using var converter = new FrameConverter(width, height, video.PixelFormat);
            var packets = new List<EncodedPacket>();
            var total = (long)Math.Ceiling(Math.Max(info.Duration, asset.Duration) * fps);
            for (var n = 0L; n < total; n++)
            {
                ct.ThrowIfCancellationRequested();
                if (cursor.At((double)n / fps) is not { } frame)
                {
                    break;
                }

                converter.Convert(frame.Pixels, frame.RowBytes, video.NextFrame());
                video.Encode(n, packets);
                packets.ForEach(writer.Write);
                packets.Clear();
                if (n % fps == 0)
                {
                    progress?.Report((double)n / total);
                }
            }

            video.Flush(packets);
            packets.ForEach(writer.Write);
            writer.Finish();
        }

        progress?.Report(1);
    }

    /// <summary>The encoder's keyframe distance option (FFmpeg's name).</summary>
    [Localizable(false)]
    private const string GopOption = "g";

    private string? PathOf(MediaAsset asset, int stream, int height)
    {
        var info = new FileInfo(asset.Path);
        if (!info.Exists)
        {
            return null;
        }

        var text = FormattableString.Invariant($"{Path.GetFullPath(asset.Path).ToUpperInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{stream}|{height}");
        return Path.Combine(folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..20] + ProxyExtension);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Making a {Height}p proxy of {Name} stream {Stream}")]
    private partial void LogMaking(string name, int stream, int height);
}
