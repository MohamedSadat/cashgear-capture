using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Overlays;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Engine.Capture.Wgc;
using CashGear.Capture.Media;
using CashGear.Capture.Overlays.Input;
using CashGear.Capture.Overlays.Rendering;
using CashGear.Capture.Recording.Resources;
using Microsoft.Extensions.Logging;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace CashGear.Capture.Recording;

/// <summary>Adds the overlays to a saved recording afterwards, from its metadata file.</summary>
public interface IOverlayRenderer
{
    /// <summary>The metadata file that goes with <paramref name="video"/> (also for a later part of a recording), or null.</summary>
    string? FindMetadata(string video);

    /// <summary>
    /// Makes <c>name (overlays).mp4</c> next to <paramref name="video"/>: the same recording with the overlays drawn
    /// in as the overlay settings say now. Returns its path.
    /// </summary>
    /// <exception cref="CaptureException">No metadata, overlays already drawn in, or no encoder works.</exception>
    Task<string> RenderAsync(string video, IProgress<double>? progress = null, CancellationToken ct = default);
}

/// <summary>
/// Draws the overlays onto a saved recording: the video is decoded, each picture goes to the GPU, gets the same
/// <see cref="OverlayPlanner"/> scene and <see cref="OverlayCompositor"/> drawing a live recording would have had at
/// its time (from the metadata's timeline), and is encoded again; the sound and the webcam's track are copied. A
/// picture with nothing to draw goes straight on. The webcam is drawn from the recording's own webcam track. When the
/// pointer was left out of the video (the overlays were to draw it), it is drawn here, with the shapes the metadata
/// kept; click sounds can't be added afterwards.
/// </summary>
internal sealed partial class OverlayRenderer(IRecordingEncoders encoders, ISettingsService settings, ILoggerFactory loggers) : IOverlayRenderer
{
    private readonly ILogger _logger = loggers.CreateLogger<OverlayRenderer>();

    public string? FindMetadata(string video)
    {
        ArgumentException.ThrowIfNullOrEmpty(video);
        var own = RecordingMetadata.PathFor(video);
        if (File.Exists(own))
        {
            return own;
        }

        // A later part ("Rec (part 2).mp4") is listed in the first part's metadata.
        var folder = Path.GetDirectoryName(Path.GetFullPath(video)) ?? ".";
        var name = Path.GetFileName(video);
        foreach (var candidate in Directory.EnumerateFiles(folder, "*" + RecordingMetadata.Extension))
        {
            try
            {
                if (RecordingMetadata.Load(candidate).Parts.Any(p => string.Equals(p.File, name, StringComparison.OrdinalIgnoreCase)))
                {
                    return candidate;
                }
            }
            catch (InvalidDataException)
            {
                // not one of ours
            }
        }

        return null;
    }

    public async Task<string> RenderAsync(string video, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(video);
        if (encoders.Problem is { } problem)
        {
            throw new CaptureException(RecordingStrings.OverlaysUnavailable(problem));
        }

        var metadataPath = FindMetadata(video)
            ?? throw new CaptureException(RecordingStrings.NoMetadataFile(Path.GetFileName(video), Path.GetFileNameWithoutExtension(video) + RecordingMetadata.Extension));
        RecordingMetadata metadata;
        try
        {
            metadata = RecordingMetadata.Load(metadataPath);
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            throw new CaptureException(e.Message, e);
        }

        if (metadata.OverlaysInVideo)
        {
            throw new CaptureException(RecordingStrings.OverlaysAlreadyDrawn(Path.GetFileName(video)));
        }

        var current = settings.Current;
        var detected = await encoders.DetectAsync(ct: ct).ConfigureAwait(false);
        var usable = EncoderService.Usable(current.Recording.Encoder, detected);
        if (usable.Count == 0)
        {
            throw new CaptureException(RecordingStrings.NoVideoEncoder);
        }

        var output = PartNames.Unique(Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(video)) ?? ".", Path.GetFileNameWithoutExtension(video) + " (overlays).mp4"));
        var start = metadata.Parts.FirstOrDefault(p => string.Equals(p.File, Path.GetFileName(video), StringComparison.OrdinalIgnoreCase))?.Start ?? 0;
        LogRendering(video, metadataPath, output);
        try
        {
            await Task.Run(() => Render(video, output, metadata, start, current, usable, progress, ct), ct).ConfigureAwait(false);
        }
        catch (FfmpegException e)
        {
            throw new CaptureException(RecordingStrings.OverlaysFailed(Path.GetFileName(video), e.Message), e);
        }

        LogRendered(output);
        return output;
    }

    private void Render(
        string video,
        string output,
        RecordingMetadata metadata,
        double start,
        AppSettings current,
        IReadOnlyList<(EncoderCandidate Candidate, EncoderProbe Probe)> usable,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var timeline = metadata.ToTimeline();
        var cursors = new CursorLibrary();
        foreach (var cursor in metadata.Cursors)
        {
            cursors.Add(CursorImage.FromMetadata(cursor));
        }

        using var devices = new D3DDeviceManager(loggers.CreateLogger<D3DDeviceManager>());
        var resources = devices.Acquire();
        OverlayPlanner? planner = null;
        OverlayCompositor? compositor = null;
        ID3D11Texture2D? picture = null;
        ID3D11Texture2D? staging = null;
        try
        {
            VideoTranscoder.Transcode(
                video,
                output,
                format => OpenEncoder(format, usable, current.Recording.Quality),
                (time, bgra, width, height, webcam) =>
                {
                    planner ??= new OverlayPlanner(
                        current.Overlays,
                        new PixelPoint(metadata.Area.X, metadata.Area.Y),
                        metadata.Scale,
                        width,
                        height,
                        drawPointer: !metadata.CursorInVideo && current.Recording.IncludeCursor,
                        webcam: metadata.Webcam is not null);
                    var scene = planner.Plan(timeline, TimeSpan.FromSeconds(time + start));
                    if (scene.IsEmpty)
                    {
                        return;
                    }

                    compositor ??= new OverlayCompositor(resources.Device, width, height, cursors);
                    picture ??= resources.Device.CreateTexture2D(Description(width, height, ResourceUsage.Default, CpuAccessFlags.None));
                    staging ??= resources.Device.CreateTexture2D(Description(width, height, ResourceUsage.Staging, CpuAccessFlags.Read));
                    Draw(resources.Context, picture, staging, compositor, scene, bgra, width, height, webcam);
                },
                progress,
                ct);
        }
        finally
        {
            compositor?.Dispose();
            picture?.Dispose();
            staging?.Dispose();
        }
    }

    /// <summary>The picture up to the GPU, the overlays drawn over it, and back.</summary>
    private static unsafe void Draw(
        ID3D11DeviceContext context,
        ID3D11Texture2D picture,
        ID3D11Texture2D staging,
        OverlayCompositor compositor,
        OverlayScene scene,
        Span<byte> bgra,
        int width,
        int height,
        DecodedPicture? webcam)
    {
        fixed (byte* pixels = bgra)
        {
            context.UpdateSubresource(picture, 0, null, (nint)pixels, (uint)(width * 4), 0);
        }

        compositor.Compose(context, picture, scene, webcam is null ? null : new WebcamPicture(webcam.Width, webcam.Height, webcam.Pixels, webcam.Version));
        context.CopyResource(staging, compositor.Target);
        var mapped = context.Map(staging, 0, MapMode.Read);
        try
        {
            var source = new ReadOnlySpan<byte>((byte*)mapped.DataPointer, ((int)mapped.RowPitch * (height - 1)) + (width * 4));
            Core.Imaging.PixelBuffer.CopyRows(source, (int)mapped.RowPitch, bgra, width * 4, width * 4, height);
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }

    private static Texture2DDescription Description(int width, int height, ResourceUsage usage, CpuAccessFlags cpu) => new()
    {
        Width = (uint)width,
        Height = (uint)height,
        MipLevels = 1,
        ArraySize = 1,
        Format = Format.B8G8R8A8_UNorm,
        SampleDescription = new SampleDescription(1, 0),
        Usage = usage,
        CPUAccessFlags = cpu,
    };

    /// <summary>The first encoder that opens at the video's size, in the order the recording settings prefer.</summary>
    private VideoEncoder OpenEncoder(VideoFormat format, IReadOnlyList<(EncoderCandidate Candidate, EncoderProbe Probe)> usable, QualityPreset quality)
    {
        var problems = new List<string>();
        foreach (var (candidate, probe) in usable)
        {
            try
            {
                return VideoEncoder.Open(candidate, format, EncoderPresets.Options(candidate, quality, format.Width, format.Height, format.Fps, probe.QualityMode));
            }
            catch (FfmpegException e)
            {
                LogOpenFailed(candidate.Id, e.Message);
                problems.Add($"{candidate.DisplayName}: {e.Message}");
            }
        }

        throw new FfmpegException(RecordingStrings.NoEncoderCouldEncode(format.Width, format.Height, string.Join(" ", problems)));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Adding overlays to {Video} from {Metadata} into {Output}")]
    private partial void LogRendering(string video, string metadata, string output);

    [LoggerMessage(Level = LogLevel.Information, Message = "Overlays added: {Output}")]
    private partial void LogRendered(string output);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Encoder {Id} could not open: {Reason}")]
    private partial void LogOpenFailed(string id, string reason);
}
