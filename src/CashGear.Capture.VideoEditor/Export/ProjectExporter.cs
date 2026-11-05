using System.ComponentModel;
using System.Diagnostics;
using CashGear.Capture.Core.Diagnostics;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Media;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Playback;
using CashGear.Capture.VideoEditor.Rendering;
using CashGear.Capture.VideoEditor.Resources;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace CashGear.Capture.VideoEditor.Export;

/// <summary>An export that can't be made, in words for the user.</summary>
public sealed class ExportException : Exception
{
    public ExportException()
    {
    }

    public ExportException([Localizable(true)] string message)
        : base(message)
    {
    }

    public ExportException([Localizable(true)] string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <param name="Path">The file to write (replaced if it exists: the caller asked).</param>
/// <param name="EncoderSetting">The recording setting for the encoder (automatic, software only or one encoder).</param>
/// <param name="Range">Only this part of the timeline (null: all of it).</param>
/// <param name="BurnCaptions">Whether caption tracks that are shown are drawn into the pictures.</param>
/// <param name="CaptionFiles">Subtitle files written next to the file (same name): one for each caption track, named for it when there are several.</param>
/// <param name="Gif">How a GIF is made (null: the defaults).</param>
/// <param name="BurnTrack">The only caption track burned in, when there is one language to choose among several (null: all those shown).</param>
/// <param name="LoudnessTarget">The loudness the sound is brought to, in LUFS (null: as mixed).</param>
/// <param name="TracksOut">Whether the sound tracks are kept apart too.</param>
/// <param name="Chapters">What the project's markers become.</param>
public sealed record ExportRequest(
    VideoProject Project,
    ExportPreset Preset,
    QualityPreset Quality,
    string Path,
    string? EncoderSetting = null,
    TimeRange? Range = null,
    bool BurnCaptions = true,
    CaptionFiles CaptionFiles = CaptionFiles.None,
    GifOptions? Gif = null,
    string? BurnTrack = null,
    double? LoudnessTarget = null,
    SoundTracksOut TracksOut = SoundTracksOut.Mix,
    ExportChapterFiles Chapters = ExportChapterFiles.None)
{
    /// <summary>
    /// The project as the export makes it: the range only, on the preset's own canvas (the screen covering it and
    /// following the pointer, when the project is of another shape), caption tracks hidden when they aren't burned in.
    /// </summary>
    public VideoProject Prepared()
    {
        var project = Range is { } range ? ProjectRange.Extract(Project, range) : Project;
        if (Preset.HasCanvas && Math.Abs(((double)project.Canvas.Width / project.Canvas.Height) - ((double)Preset.CanvasWidth / Preset.CanvasHeight)) > 0.01)
        {
            project = CanvasEdits.Change(project, Preset.CanvasWidth, Preset.CanvasHeight, ScreenFraming.FollowPointer);
        }

        if (BurnCaptions && BurnTrack is { } burned)
        {
            return project with { Tracks = [.. project.Tracks.Select(t => t.Kind == TrackKind.Caption ? t with { Hidden = t.Id != burned } : t)] };
        }

        return BurnCaptions ? project : project with { Tracks = [.. project.Tracks.Select(t => t.Kind == TrackKind.Caption ? t with { Hidden = true } : t)] };
    }
}

/// <param name="Remaining">The estimated time left, once there is enough to tell.</param>
public sealed record ExportProgress(double Fraction, TimeSpan Elapsed, TimeSpan? Remaining);

/// <param name="Encoder">The video encoder used (null for sound only).</param>
/// <param name="CaptionPaths">The subtitle files written next to it.</param>
/// <param name="Gif">For a GIF, how it was made in the end (smaller than asked, to fit its size).</param>
/// <param name="PointerOutOfView">When the screen followed the pointer: the times it moved too fast to stay in the picture.</param>
/// <param name="TrackPaths">The WAV files of the sound tracks written beside it.</param>
/// <param name="MeasuredLufs">How loud the mix was before a loudness target was met.</param>
/// <param name="LoudnessGainDb">What was done to the mix to meet it.</param>
/// <param name="Chapters">The chapters, as the file, its chapters file and a YouTube description have them.</param>
/// <param name="ChaptersPath">The WebVTT chapters file written beside it.</param>
public sealed record ExportResult(string Path, TimeSpan Duration, long Frames, long Bytes, string? Encoder, TimeSpan Elapsed, IReadOnlyList<string>? CaptionPaths = null, GifOptions? Gif = null, IReadOnlyList<TimeRange>? PointerOutOfView = null, IReadOnlyList<string>? TrackPaths = null, double? MeasuredLufs = null, double? LoudnessGainDb = null, IReadOnlyList<ExportChapter>? Chapters = null, string? ChaptersPath = null);

/// <summary>
/// Makes a file from a project on a background thread: each frame drawn by the same <see cref="FrameCompositor"/>
/// as the preview (in memory, at the preset's size), converted and encoded with the encoders recording detected
/// (the graphics card's first, then software), with the sound mixed by <see cref="TimelineMixer"/>. Progress and
/// the time left are reported; cancelling leaves no file. The file is written as <c>…​.partial</c> and renamed at
/// the end.
/// </summary>
public sealed partial class ProjectExporter(IRecordingEncoders encoders, ILogger<ProjectExporter> logger, WebcamMasks? masks = null)
{
    private static readonly TimeSpan ReportEvery = TimeSpan.FromMilliseconds(200);

    /// <summary>Motion blur's samples in an export: more than the preview takes, since nobody is waiting on each frame.</summary>
    public const int MotionSamples = 32;

    /// <summary>The sound stream's title in the file, kept in English like a recording's ("Microphone").</summary>
    [Localizable(false)]
    private const string SoundStreamTitle = "Sound";

    /// <summary>The mix's title when the tracks are streams beside it.</summary>
    [Localizable(false)]
    private const string MixStreamTitle = "Mix";

    public Task<ExportResult> ExportAsync(ExportRequest request, IProgress<ExportProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.Factory.StartNew(() => Run(request, progress, ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    private async Task<ExportResult> Run(ExportRequest request, IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        using var scope = LogScopes.Begin(logger, LogScopes.Export, LogScopes.NewId());
        if (encoders.Problem is { } problem)
        {
            throw new ExportException(problem);
        }

        if (request.Project.Duration <= 0)
        {
            throw new ExportException(VideoEditorStrings.TimelineEmpty);
        }

        var project = request.Prepared();
        await MakeMasksAsync(project, ct).ConfigureAwait(false);

        var detected = request.Preset.Codec is not null ? await encoders.DetectAsync(ct: ct).ConfigureAwait(false) : [];
        var partial = request.Path + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(request.Path))!);
        LogStarted(request.Path, request.Preset.Id, request.Quality);
        var trackFiles = new List<(string Partial, string Final)>();
        try
        {
            var prepared = request with { Project = project, Range = null };
            var result = request.Preset.IsGif ? WriteGif(prepared, partial, progress, ct) : Write(prepared, partial, detected, trackFiles, progress, ct);
            File.Move(partial, request.Path, overwrite: true);
            foreach (var (written, final) in trackFiles)
            {
                File.Move(written, final, overwrite: true);
            }

            result = trackFiles.Count > 0 ? result with { TrackPaths = [.. trackFiles.Select(t => t.Final)] } : result;
            LogFinished(request.Path, result.Frames, result.Elapsed.TotalSeconds);
            var outOfView = project.Canvas.Framing == ScreenFraming.FollowPointer && request.Preset.HasVideo
                ? new ZoomCamera(new MetadataOverlays()).PointerOutOfView(project)
                : null;
            var chapters = request.Chapters != ExportChapterFiles.None ? ProjectChapters.From(project) : [];
            string? chaptersPath = null;
            if (request.Chapters.HasFlag(ExportChapterFiles.Vtt) && chapters.Count > 0)
            {
                chaptersPath = ProjectChapters.VttPath(request.Path);
                ProjectChapters.SaveVtt(chaptersPath, chapters);
            }

            return result with { Path = request.Path, CaptionPaths = WriteCaptions(project, request.Path, request.CaptionFiles), PointerOutOfView = outOfView, Chapters = chapters, ChaptersPath = chaptersPath };
        }
        catch (FfmpegException e)
        {
            throw new ExportException(VideoEditorStrings.ExportFailed(e.Message), e);
        }
        finally
        {
            if (File.Exists(partial))
            {
                File.Delete(partial);
            }

            foreach (var (written, _) in trackFiles)
            {
                if (File.Exists(written))
                {
                    File.Delete(written);
                }
            }
        }
    }

    /// <summary>Whether any video of the project is HDR, so an HDR export has light to keep. Media that can't be read counts as none.</summary>
    public static bool HasHdrMedia(VideoProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        foreach (var track in project.Tracks.Where(t => t.HasPictures))
        {
            foreach (var clip in track.Clips)
            {
                if (project.Assets.FirstOrDefault(a => a.Id == clip.AssetId) is not { IsPicture: false } asset || !File.Exists(asset.Path))
                {
                    continue;
                }

                try
                {
                    using var reader = new VideoStreamReader(asset.Path, clip.Stream, VideoDecoding.Software);
                    if (reader.IsHdr)
                    {
                        return true;
                    }
                }
                catch (FfmpegException)
                {
                    // not one that can be read here
                }
            }
        }

        return false;
    }

    /// <summary>The sound tracks that are heard in the mix.</summary>
    internal static IReadOnlyList<Track> HeardTracks(VideoProject project)
    {
        var solo = project.Tracks.Any(t => t.HasSound && t.Solo);
        return [.. project.Tracks.Where(t => t.HasSound && !t.Muted && (!solo || t.Solo) && t.Volume > 0 && t.Clips.Any())];
    }

    /// <summary>The project with only this sound track heard: what a solo export of it plays.</summary>
    internal static VideoProject Solo(VideoProject project, string trackId) =>
        project with { Tracks = [.. project.Tracks.Select(t => t.HasSound ? t with { Solo = t.Id == trackId } : t)] };

    private ExportResult Write(ExportRequest request, string partial, IReadOnlyList<EncoderProbe> detected, List<(string Partial, string Final)> trackFiles, IProgress<ExportProgress>? progress, CancellationToken ct)
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

        // Measuring the whole mix first is about what a file of sound alone takes again, and little beside a video's pictures.
        var share = request.LoudnessTarget is null ? 0 : preset.HasVideo ? 0.05 : 0.5;
        LoudnessFound? loudness = null;
        if (request.LoudnessTarget is { } target)
        {
            loudness = ExportLoudness.Find(project, target, f => Report(share * f), ct);
        }

        var gain = loudness?.GainDb;
        var totalSamples = TimelineMixer.Sample(duration);
        using var writer = new ExportWriter(partial, preset.Container);
        if (request.Chapters.HasFlag(ExportChapterFiles.InFile) && preset.CanHoldChapters)
        {
            writer.Chapters = ProjectChapters.From(project);
        }

        var writers = new List<ExportWriter>();
        var streams = new List<ExportSoundStream>();
        var mainStreams = new List<ExportSoundStream>();
        try
        {
            var separate = request.TracksOut == SoundTracksOut.Streams && preset.CanHoldTracks;
            var main = new ExportSoundStream(AudioEncoder.Open(stream: 1, preset.AudioBitRate, preset.AudioCodec), new ExportSoundSource(project, gain), writer);
            streams.Add(main);
            mainStreams.Add(main);
            var titled = new List<(ExportSoundStream Stream, string Title)> { (main, separate ? MixStreamTitle : SoundStreamTitle) };
            if (separate)
            {
                var number = 2;
                foreach (var track in HeardTracks(project))
                {
                    var stream = new ExportSoundStream(AudioEncoder.Open(number++, preset.AudioBitRate, preset.AudioCodec), new ExportSoundSource(Solo(project, track.Id), gain), writer);
                    streams.Add(stream);
                    mainStreams.Add(stream);
                    titled.Add((stream, track.Name));
                }
            }

            if (request.TracksOut == SoundTracksOut.WavFiles)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var track in HeardTracks(project))
                {
                    var final = Path.ChangeExtension(request.Path, null) + "." + SubtitleFiles.SafeName(track.Name, names) + ".wav";
                    var trackPartial = final + ".partial";
                    trackFiles.Add((trackPartial, final));
                    var trackWriter = new ExportWriter(trackPartial, ExportContainer.Wav);
                    writers.Add(trackWriter);
                    var stream = new ExportSoundStream(AudioEncoder.Open(0, 0, AudioEncoder.Wav24), new ExportSoundSource(Solo(project, track.Id), gain), trackWriter);
                    trackWriter.Add(stream.Encoder, track.Name);
                    streams.Add(stream);
                }
            }

            void Sound(long until)
            {
                foreach (var stream in streams)
                {
                    stream.Until(until, totalSamples);
                }
            }

            void FinishTrackFiles()
            {
                foreach (var stream in streams.Except(mainStreams))
                {
                    stream.Flush();
                }

                foreach (var trackWriter in writers)
                {
                    trackWriter.Finish();
                }
            }

            if (!preset.HasVideo)
            {
                writer.Add(main.Encoder, SoundStreamTitle);
                var step = (long)TimelineMixer.SampleRate;
                for (var at = 0L; at < totalSamples; at += step)
                {
                    ct.ThrowIfCancellationRequested();
                    Sound(Math.Min(totalSamples, at + step));
                    Report(share + ((1 - share) * main.Pts / totalSamples));
                }

                main.Flush();
                FinishTrackFiles();
                var soundBytes = writer.Finish();
                Report(1);
                return new ExportResult(partial, TimeSpan.FromSeconds(duration), 0, soundBytes, null, watch.Elapsed, MeasuredLufs: loudness?.MeasuredLufs, LoudnessGainDb: loudness?.GainDb);
            }

            var (width, height) = preset.Size(canvas.Width, canvas.Height);
            var format = new VideoFormat(width, height, fps);
            using var video = OpenEncoder(preset.Codec!.Value, format, request, detected);
            writer.Add(video);
            foreach (var (stream, title) in titled)
            {
                writer.Add(stream.Encoder, title);
            }

            var packets = new List<EncodedPacket>();
            void Send()
            {
                packets.ForEach(writer.Write);
                packets.Clear();
            }

            using var frames = new ExportFrameProvider(VideoDecoding.Hardware, preset.Hdr);
            using var converter = new FrameConverter(width, height, video.PixelFormat);

            // Drawn as ever, on float pixels that can hold light past white: everything else blends as in an SDR export.
            using var surface = SKSurface.Create(new SKImageInfo(width, height, preset.Hdr ? SKColorType.RgbaF16 : SKColorType.Bgra8888, SKAlphaType.Premul))
                ?? throw new ExportException(VideoEditorStrings.NotEnoughMemory(width, height));
            var compositor = NewCompositor(frames);
            var count = (long)Math.Ceiling((duration * fps) - 1e-6);
            for (var n = 0L; n < count; n++)
            {
                ct.ThrowIfCancellationRequested();
                var time = (double)n / fps;
                var target2 = surface.Canvas;
                target2.Save();
                target2.Scale((float)width / canvas.Width, (float)height / canvas.Height);
                compositor.Render(target2, project, time);
                target2.Restore();
                surface.Flush();
                using (var pixmap = surface.PeekPixels())
                {
                    unsafe
                    {
                        if (preset.Hdr)
                        {
                            PqPictures.FromExtended((byte*)pixmap.GetPixels(), pixmap.RowBytes, video.NextFrame(), width, height);
                        }
                        else
                        {
                            converter.Convert(new ReadOnlySpan<byte>((void*)pixmap.GetPixels(), pixmap.RowBytes * height), pixmap.RowBytes, video.NextFrame());
                        }
                    }
                }

                video.Encode(n, packets);
                Send();
                Sound(Math.Min(totalSamples, (n + 1) * TimelineMixer.SampleRate / fps));
                frames.Retire();
                Report(share + ((1 - share) * (n + 1) / count));
            }

            Sound(totalSamples);
            video.Flush(packets);
            Send();
            foreach (var stream in mainStreams)
            {
                stream.Flush();
            }

            FinishTrackFiles();
            var bytes = writer.Finish();
            Report(1);
            return new ExportResult(partial, TimeSpan.FromSeconds((double)count / fps), count, bytes, video.Candidate.DisplayName, watch.Elapsed, MeasuredLufs: loudness?.MeasuredLufs, LoudnessGainDb: loudness?.GainDb);
        }
        finally
        {
            streams.ForEach(s => s.Dispose());
            writers.ForEach(w => w.Dispose());
        }
    }

    /// <summary>
    /// A GIF: each try draws the timeline twice at the GIF's rate and size (first for its colours, then to write
    /// it); when it comes out larger than allowed, it is made again with fewer colours, frames or pixels (four tries
    /// at most, the last kept whatever its size).
    /// </summary>
    private ExportResult WriteGif(ExportRequest request, string partial, IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        var project = request.Project;
        var options = request.Gif ?? new GifOptions();
        options = options with { Fps = Math.Clamp(options.Fps, GifOptions.SlowestFps, GifOptions.FastestFps) };
        var watch = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        for (var attempt = 1; ; attempt++)
        {
            var (width, height) = options.Size(project.Canvas.Width, project.Canvas.Height);
            var count = (long)Math.Ceiling((project.Duration * options.Fps) - 1e-6);
            long bytes;
            using (var gif = new GifWriter(partial, width, height, options.Fps, new GifSettings(options.Colors, options.Dither, options.Loop ? 0 : -1)))
            using (var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul))
                ?? throw new ExportException(VideoEditorStrings.NotEnoughMemory(width, height)))
            {
                for (var pass = 0; pass < 2; pass++)
                {
                    using var frames = new ExportFrameProvider(VideoDecoding.Hardware);
                    var compositor = NewCompositor(frames);
                    for (var n = 0L; n < count; n++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var target = surface.Canvas;
                        target.Save();
                        target.Scale((float)width / project.Canvas.Width, (float)height / project.Canvas.Height);
                        compositor.Render(target, project, (double)n / options.Fps);
                        target.Restore();
                        surface.Flush();
                        using (var pixmap = surface.PeekPixels())
                        {
                            unsafe
                            {
                                var pixels = new ReadOnlySpan<byte>((void*)pixmap.GetPixels(), pixmap.RowBytes * height);
                                if (pass == 0)
                                {
                                    gif.Learn(pixels, pixmap.RowBytes);
                                }
                                else
                                {
                                    gif.Write(pixels, pixmap.RowBytes);
                                }
                            }
                        }

                        frames.Retire();
                        if (watch.Elapsed - lastReport >= ReportEvery)
                        {
                            lastReport = watch.Elapsed;
                            var fraction = ((pass * count) + n + 1) / (2.0 * count);
                            TimeSpan? remaining = attempt == 1 && fraction > 0.02 && watch.Elapsed > TimeSpan.FromSeconds(1) ? watch.Elapsed * ((1 - fraction) / fraction) : null;
                            progress?.Report(new ExportProgress(fraction, watch.Elapsed, remaining));
                        }
                    }

                    if (pass == 0)
                    {
                        gif.EndLearning();
                    }
                }

                bytes = gif.Finish();
            }

            if (options.MaxBytes <= 0 || bytes <= options.MaxBytes || attempt >= 4 || options.Smaller() is not { } smaller)
            {
                progress?.Report(new ExportProgress(1, watch.Elapsed, TimeSpan.Zero));
                return new ExportResult(partial, TimeSpan.FromSeconds((double)count / options.Fps), count, bytes, VideoEditorStrings.GifEncoder, watch.Elapsed, Gif: options);
            }

            LogGifTooLarge(bytes, options.MaxBytes, attempt);
            options = smaller;
        }
    }

    private FrameCompositor NewCompositor(IFrameProvider frames) => new(frames) { Masks = masks, MotionSamples = MotionSamples };

    /// <summary>The masks of the webcam clips that need them, made now if they aren't there yet (the export says what stopped it).</summary>
    private async Task MakeMasksAsync(VideoProject project, CancellationToken ct)
    {
        if (masks is null)
        {
            return;
        }

        var needed = project.Tracks.Where(t => t.HasPictures && !t.Hidden).SelectMany(t => t.Clips)
            .Where(c => c.Background is { Effect: not BackgroundEffect.None })
            .Select(c => (c.AssetId, c.Stream))
            .Distinct();
        foreach (var (assetId, stream) in needed)
        {
            if (project.Asset(assetId) is not { } asset)
            {
                continue;
            }

            try
            {
                for (var made = false; !made;)
                {
                    try
                    {
                        await masks.EnsureAsync(asset, stream, null, ct).ConfigureAwait(false);
                        made = true;
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // Work a window that has closed had started, and this export joined: it does it itself now.
                    }
                }
            }
            catch (Exception e) when (e is HttpRequestException or InvalidDataException or InvalidOperationException or FfmpegException or IOException)
            {
                throw new ExportException(VideoEditorStrings.MasksFailed(e.Message), e);
            }
        }
    }

    /// <summary>The subtitle files asked for, next to <paramref name="path"/> (none when no caption has text).</summary>
    private static IReadOnlyList<string> WriteCaptions(VideoProject project, string path, CaptionFiles files)
    {
        var written = new List<string>();
        var tracks = project.Tracks.Where(t => t.Kind == TrackKind.Caption && SubtitleFiles.Cues(project, t.Id).Count > 0).ToList();
        foreach (var (flag, format) in new[] { (CaptionFiles.Srt, SubtitleFormat.Srt), (CaptionFiles.Vtt, SubtitleFormat.Vtt) })
        {
            if (!files.HasFlag(flag))
            {
                continue;
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var track in tracks)
            {
                // One track: the file's own name. Several, each for a language: named for its track.
                var file = tracks.Count == 1
                    ? Path.ChangeExtension(path, SubtitleFiles.Extension(format))
                    : Path.ChangeExtension(path, null) + "." + SubtitleFiles.SafeName(track.Name, names) + SubtitleFiles.Extension(format);
                SubtitleFiles.Save(file, SubtitleFiles.Cues(project, track.Id), format);
                written.Add(file);
            }
        }

        return written;
    }

    /// <summary>The first encoder of the codec that opens at the export's size, in the order the settings prefer.</summary>
    private VideoEncoder OpenEncoder(VideoCodec codec, VideoFormat format, ExportRequest request, IReadOnlyList<EncoderProbe> detected)
    {
        var problems = new List<string>();
        if (request.Preset.Hdr)
        {
            foreach (var candidate in EncoderPresets.ForHdrExport(codec))
            {
                try
                {
                    return VideoEncoder.Open(candidate, format, EncoderPresets.HdrOptions(candidate, request.Quality, format.Width, format.Height, format.Fps), hdr: true);
                }
                catch (FfmpegException e)
                {
                    LogOpenFailed(candidate.Id, e.Message);
                }
            }

            throw new ExportException(VideoEditorStrings.NoHdrEncoder);
        }

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
            ? VideoEditorStrings.NoEncoder(codec)
            : VideoEditorStrings.NoEncoderForSize(codec, format.Width, format.Height, string.Join(" ", problems)));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Exporting {Path} ({Preset}, {Quality})")]
    private partial void LogStarted(string path, string preset, QualityPreset quality);

    [LoggerMessage(Level = LogLevel.Information, Message = "Exported {Path}: {Frames} frames in {Seconds:0.0} s")]
    private partial void LogFinished(string path, long frames, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Export: encoder {Id} could not open: {Reason}")]
    private partial void LogOpenFailed(string id, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "The GIF came out at {Bytes} bytes, over {Max} (try {Attempt}): making it smaller")]
    private partial void LogGifTooLarge(long bytes, long max, int attempt);
}

/// <summary>The export's pictures: a <see cref="FrameCursor"/> per clip, read forward as the export goes, closed once the clip is behind.</summary>
internal sealed class ExportFrameProvider(VideoDecoding decoding, bool keepHdr = false) : IFrameProvider, IDisposable
{
    private readonly Dictionary<string, FrameCursor> _cursors = [];
    private readonly HashSet<string> _used = [];

    /// <summary>Cursors opened so far (one per clip drawn).</summary>
    public int Opened { get; private set; }

    public VideoFrame? Frame(Clip clip, MediaAsset asset, double sourceTime)
    {
        if (!_cursors.TryGetValue(clip.Id, out var cursor))
        {
            cursor = new FrameCursor(asset.Path, clip.Stream, decoding, keepHdr: keepHdr);
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
