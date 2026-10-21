using System.Diagnostics;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Media;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Playback;
using CashGear.Capture.VideoEditor.Rendering;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace CashGear.Capture.VideoEditor.Export;

/// <summary>An export that can't be made, in words for the user.</summary>
public sealed class ExportException : Exception
{
    public ExportException()
    {
    }

    public ExportException(string message)
        : base(message)
    {
    }

    public ExportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <param name="Path">The file to write (replaced if it exists: the caller asked).</param>
/// <param name="EncoderSetting">The recording setting for the encoder (automatic, software only or one encoder).</param>
public sealed record ExportRequest(VideoProject Project, ExportPreset Preset, QualityPreset Quality, string Path, string? EncoderSetting = null);

/// <param name="Remaining">The estimated time left, once there is enough to tell.</param>
public sealed record ExportProgress(double Fraction, TimeSpan Elapsed, TimeSpan? Remaining);

/// <param name="Encoder">The video encoder used (null for sound only).</param>
public sealed record ExportResult(string Path, TimeSpan Duration, long Frames, long Bytes, string? Encoder, TimeSpan Elapsed);

/// <summary>
/// Makes a file from a project on a background thread: each frame drawn by the same <see cref="FrameCompositor"/>
/// as the preview (in memory, at the preset's size), converted and encoded with the encoders recording detected
/// (the graphics card's first, then software), with the sound mixed by <see cref="TimelineMixer"/>. Progress and
/// the time left are reported; cancelling leaves no file. The file is written as <c>…​.partial</c> and renamed at
/// the end.
/// </summary>
public sealed partial class ProjectExporter(IRecordingEncoders encoders, ILogger<ProjectExporter> logger)
{
    private static readonly TimeSpan ReportEvery = TimeSpan.FromMilliseconds(200);

    public Task<ExportResult> ExportAsync(ExportRequest request, IProgress<ExportProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.Factory.StartNew(() => Run(request, progress, ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    private async Task<ExportResult> Run(ExportRequest request, IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        if (encoders.Problem is { } problem)
        {
            throw new ExportException(problem);
        }

        var project = request.Project;
        if (project.Duration <= 0)
        {
            throw new ExportException("The timeline is empty: there is nothing to export.");
        }

        var detected = request.Preset.HasVideo ? await encoders.DetectAsync(ct: ct).ConfigureAwait(false) : [];
        var partial = request.Path + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(request.Path))!);
        LogStarted(request.Path, request.Preset.Id, request.Quality);
        try
        {
            var result = Write(request, partial, detected, progress, ct);
            File.Move(partial, request.Path, overwrite: true);
            LogFinished(request.Path, result.Frames, result.Elapsed.TotalSeconds);
            return result with { Path = request.Path };
        }
        catch (FfmpegException e)
        {
            throw new ExportException($"The export failed: {e.Message}", e);
        }
        finally
        {
            if (File.Exists(partial))
            {
                File.Delete(partial);
            }
        }
    }

    private ExportResult Write(ExportRequest request, string partial, IReadOnlyList<EncoderProbe> detected, IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        var project = request.Project;
        var preset = request.Preset;
        var canvas = project.Canvas;
        var fps = canvas.Fps;
        var duration = project.Duration;
        var watch = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        void Report(double fraction)
        {
            if (watch.Elapsed - lastReport < ReportEvery && fraction < 1)
            {
                return;
            }

            lastReport = watch.Elapsed;
            TimeSpan? remaining = fraction > 0.02 && watch.Elapsed > TimeSpan.FromSeconds(1) ? watch.Elapsed * ((1 - fraction) / fraction) : null;
            progress?.Report(new ExportProgress(fraction, watch.Elapsed, remaining));
        }

        var packets = new List<EncodedPacket>();
        using var audio = AudioEncoder.Open(stream: 1, preset.AudioBitRate, preset.AudioCodec);
        using var writer = new ExportWriter(partial, preset.Container);
        using var mixer = new TimelineMixer();
        var block = new float[audio.FrameSize * TimelineMixer.Channels];
        var totalSamples = TimelineMixer.Sample(duration);
        long audioPts = 0;
        void Send()
        {
            packets.ForEach(writer.Write);
            packets.Clear();
        }

        // Whole encoder frames up to a time; only the very last frame of the export may be shorter.
        void Sound(long until)
        {
            while (audioPts < until && (audioPts + audio.FrameSize <= until || until == totalSamples))
            {
                var count = (int)Math.Min(audio.FrameSize, until - audioPts);
                var samples = block.AsSpan(0, count * TimelineMixer.Channels);
                mixer.Mix(project, audioPts, samples);
                audio.Encode(samples, audioPts, packets);
                audioPts += count;
                Send();
            }
        }

        if (!preset.HasVideo)
        {
            writer.Add(audio, "Sound");
            var step = (long)TimelineMixer.SampleRate;
            for (var at = 0L; at < totalSamples; at += step)
            {
                ct.ThrowIfCancellationRequested();
                Sound(Math.Min(totalSamples, at + step));
                Report((double)audioPts / totalSamples);
            }

            audio.Flush(packets);
            Send();
            var soundBytes = writer.Finish();
            Report(1);
            return new ExportResult(partial, TimeSpan.FromSeconds(duration), 0, soundBytes, null, watch.Elapsed);
        }

        var (width, height) = preset.Size(canvas.Width, canvas.Height);
        var format = new VideoFormat(width, height, fps);
        using var video = OpenEncoder(preset.Codec!.Value, format, request, detected);
        writer.Add(video);
        writer.Add(audio, "Sound");
        using var frames = new ExportFrameProvider(VideoDecoding.Hardware);
        using var converter = new FrameConverter(width, height, video.PixelFormat);
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul))
            ?? throw new ExportException($"Not enough memory for {width} × {height} pictures.");
        var compositor = new FrameCompositor(frames);
        var count = (long)Math.Ceiling((duration * fps) - 1e-6);
        for (var n = 0L; n < count; n++)
        {
            ct.ThrowIfCancellationRequested();
            var time = (double)n / fps;
            var target = surface.Canvas;
            target.Save();
            target.Scale((float)width / canvas.Width, (float)height / canvas.Height);
            compositor.Render(target, project, time);
            target.Restore();
            surface.Flush();
            using (var pixmap = surface.PeekPixels())
            {
                unsafe
                {
                    converter.Convert(new ReadOnlySpan<byte>((void*)pixmap.GetPixels(), pixmap.RowBytes * height), pixmap.RowBytes, video.NextFrame());
                }
            }

            video.Encode(n, packets);
            Send();
            Sound(Math.Min(totalSamples, (n + 1) * TimelineMixer.SampleRate / fps));
            frames.Retire();
            Report((double)(n + 1) / count);
        }

        Sound(totalSamples);
        video.Flush(packets);
        audio.Flush(packets);
        Send();
        var bytes = writer.Finish();
        Report(1);
        return new ExportResult(partial, TimeSpan.FromSeconds((double)count / fps), count, bytes, video.Candidate.DisplayName, watch.Elapsed);
    }

    /// <summary>The first encoder of the codec that opens at the export's size, in the order the settings prefer.</summary>
    private VideoEncoder OpenEncoder(VideoCodec codec, VideoFormat format, ExportRequest request, IReadOnlyList<EncoderProbe> detected)
    {
        var problems = new List<string>();
        foreach (var (candidate, probe) in EncoderPresets.ForExport(codec, request.EncoderSetting, detected))
        {
            try
            {
                return VideoEncoder.Open(candidate, format, EncoderPresets.Options(candidate, request.Quality, format.Width, format.Height, format.Fps, probe.QualityMode));
            }
            catch (FfmpegException e)
            {
                LogOpenFailed(candidate.Id, e.Message);
                problems.Add($"{candidate.DisplayName}: {e.Message}");
            }
        }

        throw new ExportException(problems.Count == 0
            ? $"No {codec} encoder works on this PC."
            : $"No {codec} encoder could encode {format.Width} × {format.Height}. {string.Join(" ", problems)}");
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Exporting {Path} ({Preset}, {Quality})")]
    private partial void LogStarted(string path, string preset, QualityPreset quality);

    [LoggerMessage(Level = LogLevel.Information, Message = "Exported {Path}: {Frames} frames in {Seconds:0.0} s")]
    private partial void LogFinished(string path, long frames, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Export: encoder {Id} could not open: {Reason}")]
    private partial void LogOpenFailed(string id, string reason);
}

/// <summary>The export's pictures: a <see cref="FrameCursor"/> per clip, read forward as the export goes, closed once the clip is behind.</summary>
internal sealed class ExportFrameProvider(VideoDecoding decoding) : IFrameProvider, IDisposable
{
    private readonly Dictionary<string, FrameCursor> _cursors = [];
    private readonly HashSet<string> _used = [];

    /// <summary>Cursors opened so far (one per clip drawn).</summary>
    public int Opened { get; private set; }

    public VideoFrame? Frame(Clip clip, MediaAsset asset, double sourceTime)
    {
        if (!_cursors.TryGetValue(clip.Id, out var cursor))
        {
            cursor = new FrameCursor(asset.Path, clip.Stream, decoding);
            _cursors[clip.Id] = cursor;
            Opened++;
        }

        _used.Add(clip.Id);
        return cursor.At(sourceTime)?.AddRef();
    }

    /// <summary>After a frame: closes the cursors of clips it didn't draw (the export only goes forward).</summary>
    public void Retire()
    {
        foreach (var id in _cursors.Keys.Where(id => !_used.Contains(id)).ToList())
        {
            _cursors[id].Dispose();
            _cursors.Remove(id);
        }

        _used.Clear();
    }

    public void Dispose()
    {
        foreach (var cursor in _cursors.Values)
        {
            cursor.Dispose();
        }

        _cursors.Clear();
    }
}
