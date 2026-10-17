using CashGear.Capture.Core.Recording;
using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.Media;

/// <summary>
/// Finds out whether an encoder really works on this PC by encoding a few frames with it: being in the FFmpeg build
/// doesn't mean the driver, GPU or Windows component it needs is there. The test is 256×256 (some hardware encoders
/// refuse much smaller pictures). Media Foundation encoders are also checked for honouring their quality setting;
/// one that doesn't is used with a bit rate instead. Call it on a background (MTA) thread, never the UI thread:
/// Media Foundation and hardware encoders can take a while to open.
/// </summary>
public sealed partial class EncoderCatalog(FfmpegRuntime runtime, ILogger<EncoderCatalog> logger)
{
    private const int ProbeFrames = 8;
    private static readonly VideoFormat ProbeFormat = new(256, 256, 30);

    public EncoderProbe Probe(EncoderCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        runtime.EnsureAvailable();
        EncoderProbe probe;
        try
        {
            if (!IsInBuild(candidate.FfmpegName))
            {
                throw new FfmpegException($"{candidate.FfmpegName} isn't in this FFmpeg build.");
            }

            var (_, detail) = TestEncode(candidate, QualityPreset.Balanced, qualityMode: true);
            var qualityMode = true;
            if (candidate.FfmpegName is "h264_mf" or "hevc_mf")
            {
                var small = TestEncode(candidate, QualityPreset.Small, qualityMode: true).Bytes;
                var high = TestEncode(candidate, QualityPreset.High, qualityMode: true).Bytes;
                qualityMode = high > small * 1.1;
            }

            probe = new EncoderProbe(candidate.Id, true, detail, qualityMode);
        }
        catch (FfmpegException e)
        {
            probe = new EncoderProbe(candidate.Id, false, e.Message);
        }

        LogProbe(candidate.Id, probe.Works, probe.Detail ?? "", probe.QualityMode);
        return probe;
    }

    private static unsafe bool IsInBuild(string name) => ffmpeg.avcodec_find_encoder_by_name(name) is not null;

    private static unsafe (long Bytes, string? Detail) TestEncode(EncoderCandidate candidate, QualityPreset preset, bool qualityMode)
    {
        var options = EncoderPresets.Options(candidate, preset, ProbeFormat.Width, ProbeFormat.Height, ProbeFormat.Fps, qualityMode);
        using var encoder = VideoEncoder.Open(candidate, ProbeFormat, options);
        var packets = new List<EncodedPacket>();
        try
        {
            for (var i = 0; i < ProbeFrames; i++)
            {
                TestPattern.Fill(encoder.NextFrame(), i);
                encoder.Encode(i, packets);
            }

            encoder.Flush(packets);
            if (packets.Count == 0)
            {
                throw new FfmpegException($"{candidate.FfmpegName} accepted frames but produced no video.");
            }

            return (packets.Sum(p => (long)p.Size), encoder.Detail);
        }
        finally
        {
            packets.ForEach(p => p.Dispose());
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Encoder {Id}: works {Works} ({Detail}), quality mode {QualityMode}")]
    private partial void LogProbe(string id, bool works, string detail, bool qualityMode);
}

/// <summary>A deterministic busy picture (fine noise over moving bars), so quality settings make a difference.</summary>
internal static unsafe class TestPattern
{
    public static void Fill(AVFrame* frame, int index)
    {
        var width = frame->width;
        var height = frame->height;
        var luma = frame->data[0];
        for (var y = 0; y < height; y++)
        {
            var row = luma + (y * frame->linesize[0]);
            for (var x = 0; x < width; x++)
            {
                var noise = (uint)((x * 73856093) ^ (y * 19349663) ^ (index * 83492791)) >> 27;
                row[x] = (byte)(((x + (index * 8)) / 16 % 2 == 0 ? 60 : 180) + noise);
            }
        }

        var nv12 = frame->format == (int)AVPixelFormat.AV_PIX_FMT_NV12;
        for (var plane = 1u; plane <= (nv12 ? 1u : 2u); plane++)
        {
            for (var y = 0; y < height / 2; y++)
            {
                var row = frame->data[plane] + (y * frame->linesize[plane]);
                var bytes = nv12 ? width : width / 2;
                for (var x = 0; x < bytes; x++)
                {
                    row[x] = (byte)(128 + (((x + y + index) % 32) - 16));
                }
            }
        }
    }
}
