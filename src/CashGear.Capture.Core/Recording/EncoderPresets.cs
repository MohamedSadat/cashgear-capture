using System.Globalization;
using CashGear.Capture.Core.Resources;

namespace CashGear.Capture.Core.Recording;

/// <summary>How much quality to trade for file size, the same idea for every encoder.</summary>
public enum QualityPreset
{
    Small,
    Balanced,
    High,

    /// <summary>Close to lossless (not mathematically lossless, which few players can play).</summary>
    Lossless,
}

public enum VideoCodec
{
    H264,
    Hevc,
    Av1,
    Vp9,
}

/// <summary>The video a recording encodes: its size in pixels (even) and frame rate.</summary>
public readonly record struct VideoFormat(int Width, int Height, int Fps);

/// <summary>A video encoder the app knows how to set up. Whether it works on this PC is found out by detection.</summary>
/// <param name="Id">Stable name used in settings and the detection cache, e.g. <c>h264_mf_hw</c>.</param>
/// <param name="FfmpegName">FFmpeg's encoder, e.g. <c>h264_mf</c> (Media Foundation, hardware and software are separate ids).</param>
/// <param name="Hardware">Encodes on the GPU (or a hardware block), leaving the CPU free.</param>
public sealed record EncoderCandidate(string Id, string FfmpegName, VideoCodec Codec, bool Hardware, string DisplayName)
{
    public override string ToString() => Id;
}

/// <summary>
/// The encoders recording can use and how each is configured for a <see cref="QualityPreset"/>. Automatic tries the
/// GPU vendors' own encoders first, then Windows' Media Foundation (the vendor's hardware encoder through Windows,
/// then Microsoft's software encoder, which Windows itself includes), then OpenH264 as the last resort. The values
/// were matched to the old x264 settings by SSIM (Media Foundation's are measured; see the Phase 10 changelog).
/// </summary>
public static class EncoderPresets
{
    /// <summary>Setting value: the best encoder that works here.</summary>
    public const string Auto = "auto";

    /// <summary>Setting value: a software encoder only.</summary>
    public const string Software = "software";

    public static EncoderCandidate NvencH264 { get; } = new("h264_nvenc", "h264_nvenc", VideoCodec.H264, true, CoreStrings.EncoderNvencH264);
    public static EncoderCandidate NvencHevc { get; } = new("hevc_nvenc", "hevc_nvenc", VideoCodec.Hevc, true, CoreStrings.EncoderNvencHevc);
    public static EncoderCandidate NvencAv1 { get; } = new("av1_nvenc", "av1_nvenc", VideoCodec.Av1, true, CoreStrings.EncoderNvencAv1);
    public static EncoderCandidate QuickSyncH264 { get; } = new("h264_qsv", "h264_qsv", VideoCodec.H264, true, CoreStrings.EncoderQuickSyncH264);
    public static EncoderCandidate AmfH264 { get; } = new("h264_amf", "h264_amf", VideoCodec.H264, true, CoreStrings.EncoderAmfH264);
    public static EncoderCandidate MediaFoundationH264 { get; } = new("h264_mf_hw", "h264_mf", VideoCodec.H264, true, CoreStrings.EncoderWindowsHardwareH264);
    public static EncoderCandidate MediaFoundationHevc { get; } = new("hevc_mf_hw", "hevc_mf", VideoCodec.Hevc, true, CoreStrings.EncoderWindowsHardwareHevc);
    public static EncoderCandidate WindowsSoftwareH264 { get; } = new("h264_mf", "h264_mf", VideoCodec.H264, false, CoreStrings.EncoderWindowsSoftwareH264);
    public static EncoderCandidate OpenH264 { get; } = new("libopenh264", "libopenh264", VideoCodec.H264, false, CoreStrings.EncoderOpenH264);

    /// <summary>Every candidate, in the order Automatic tries the H.264 ones.</summary>
    public static IReadOnlyList<EncoderCandidate> All { get; } =
    [
        NvencH264, QuickSyncH264, AmfH264, MediaFoundationH264, WindowsSoftwareH264, OpenH264,
        NvencHevc, MediaFoundationHevc, NvencAv1,
    ];

    public static IReadOnlyList<EncoderCandidate> SoftwareFallbacks { get; } = [WindowsSoftwareH264, OpenH264];

    /// <summary>VP9 in software (libvpx), for WebM exports.</summary>
    public static EncoderCandidate Vp9 { get; } = new("libvpx-vp9", "libvpx-vp9", VideoCodec.Vp9, false, CoreStrings.EncoderVp9);

    /// <summary>AV1 in software (SVT-AV1), for exports when no graphics card encodes AV1.</summary>
    public static EncoderCandidate SvtAv1 { get; } = new("libsvtav1", "libsvtav1", VideoCodec.Av1, false, CoreStrings.EncoderSvtAv1);

    /// <summary>HEVC in software (Kvazaar), for exports when no graphics card encodes HEVC.</summary>
    public static EncoderCandidate Kvazaar { get; } = new("libkvazaar", "libkvazaar", VideoCodec.Hevc, false, CoreStrings.EncoderKvazaar);

    /// <summary>
    /// Software encoders only exports use. They aren't in <see cref="All"/>, so recording's detection (and its cached
    /// results) doesn't change; an export simply tries to open them.
    /// </summary>
    public static IReadOnlyList<EncoderCandidate> ExportSoftware { get; } = [Vp9, SvtAv1, Kvazaar];

    public static EncoderCandidate? Find(string? id) =>
        All.Concat(ExportSoftware).FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The encoders to try for a setting, best first, keeping only those detection found working.</summary>
    public static IReadOnlyList<(EncoderCandidate Candidate, EncoderProbe Probe)> Usable(string? setting, IReadOnlyList<EncoderProbe> detected)
    {
        ArgumentNullException.ThrowIfNull(detected);
        return Preference(setting)
            .Select(c => (Candidate: c, Probe: detected.FirstOrDefault(p => p.Id == c.Id)))
            .Where(x => x.Probe is { Works: true })
            .Select(x => (x.Candidate, x.Probe!))
            .ToList();
    }

    /// <summary>
    /// The encoders an export of <paramref name="codec"/> tries, best first: the graphics card's (those detection
    /// found working) unless the setting is <see cref="Software"/>, then the software ones. Software encoders that
    /// detection doesn't cover are tried as they are.
    /// </summary>
    public static IReadOnlyList<(EncoderCandidate Candidate, EncoderProbe Probe)> ForExport(VideoCodec codec, string? setting, IReadOnlyList<EncoderProbe> detected)
    {
        ArgumentNullException.ThrowIfNull(detected);
        var software = string.Equals(setting, Software, StringComparison.OrdinalIgnoreCase);
        var result = new List<(EncoderCandidate, EncoderProbe)>();
        foreach (var candidate in All.Concat(ExportSoftware).Where(c => c.Codec == codec))
        {
            var probe = detected.FirstOrDefault(p => p.Id == candidate.Id);
            if (candidate.Hardware && (software || probe is not { Works: true }))
            {
                continue;
            }

            if (!candidate.Hardware && probe is { Works: false })
            {
                continue;
            }

            result.Add((candidate, probe ?? new EncoderProbe(candidate.Id, true)));
        }

        // The chosen encoder first, when it encodes this codec.
        if (Find(setting) is { } chosen && result.FindIndex(r => r.Item1 == chosen) is > 0 and var at)
        {
            var item = result[at];
            result.RemoveAt(at);
            result.Insert(0, item);
        }

        return result;
    }

    /// <summary>
    /// The encoders to try for a setting, best first. A chosen encoder is followed by the software ones, which are
    /// also where a recording continues when its encoder fails. Unknown values mean <see cref="Auto"/>.
    /// </summary>
    public static IReadOnlyList<EncoderCandidate> Preference(string? setting)
    {
        if (string.Equals(setting, Software, StringComparison.OrdinalIgnoreCase))
        {
            return SoftwareFallbacks;
        }

        if (Find(setting) is { } chosen)
        {
            return [chosen, .. SoftwareFallbacks.Where(c => c != chosen)];
        }

        return All.Where(c => c.Codec == VideoCodec.H264).ToList();
    }

    /// <summary>OpenH264 and the export-only software encoders take planar YUV 4:2:0; every other encoder gets NV12.</summary>
    public static bool UsesPlanarYuv(EncoderCandidate encoder) => encoder == OpenH264 || ExportSoftware.Contains(encoder);

    /// <summary>
    /// The encoder's FFmpeg options for a preset. <paramref name="qualityMode"/> false means detection found the
    /// Media Foundation encoder ignores its quality setting, so a bit rate is used instead.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Options(
        EncoderCandidate encoder, QualityPreset preset, int width, int height, int fps, bool qualityMode = true)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        var options = new List<KeyValuePair<string, string>>();
        void Add(string name, object value) => options.Add(new(name, Convert.ToString(value, CultureInfo.InvariantCulture)!));

        switch (encoder.FfmpegName)
        {
            case "h264_nvenc" or "hevc_nvenc" or "av1_nvenc":
                Add("preset", "p4");
                Add("tune", "hq");
                Add("rc", "vbr");
                Add("cq", Pick(preset, 30, 25, 21, 16));
                Add("b", 0);
                break;
            case "h264_qsv":
                Add("preset", "veryfast");
                Add("global_quality", Pick(preset, 30, 25, 21, 16));
                Add("look_ahead", 0);
                break;
            case "h264_amf":
                Add("usage", "transcoding");
                Add("quality", "balanced");
                Add("rc", "cqp");
                Add("qp_i", Pick(preset, 30, 24, 20, 15));
                Add("qp_p", Pick(preset, 30, 24, 20, 15));
                break;
            case "h264_mf" or "hevc_mf":
                Add("hw_encoding", encoder.Hardware ? 1 : 0);
                Add("scenario", "display_remoting");
                if (qualityMode)
                {
                    Add("rate_control", "quality");
                    Add("quality", encoder.Hardware ? Pick(preset, 35, 50, 65, 90) : Pick(preset, 55, 72, 88, 100));
                }
                else
                {
                    Add("rate_control", "u_vbr");
                    Add("b", BitRate(preset, width, height, fps));
                }

                break;
            case "libopenh264":
                Add("rc_mode", "bitrate");
                Add("b", BitRate(preset, width, height, fps));
                break;
            case "libvpx-vp9":
                // Constant quality ("crf" with no bit rate cap); "good" at speed 5 with row threads keeps exports quick.
                Add("deadline", "good");
                Add("cpu-used", 5);
                Add("row-mt", 1);
                Add("crf", Pick(preset, 40, 34, 28, 18));
                Add("b", 0);
                break;
            case "libsvtav1":
                Add("preset", 8);
                Add("crf", Pick(preset, 42, 35, 29, 20));
                Add("b", 0);
                break;
            case "libkvazaar":
                // FFmpeg's default bit rate (200 kbit/s) would override the quality: no bit rate.
                Add("kvazaar-params", string.Create(CultureInfo.InvariantCulture, $"preset=veryfast,qp={Pick(preset, 34, 28, 24, 18)}"));
                Add("b", 0);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(encoder), encoder, "No settings for this encoder.");
        }

        return options;
    }

    /// <summary>Bits per second for encoders without a constant-quality mode, from bits per pixel per frame.</summary>
    public static long BitRate(QualityPreset preset, int width, int height, int fps)
    {
        var bitsPerPixel = preset switch
        {
            QualityPreset.Small => 0.05,
            QualityPreset.High => 0.18,
            QualityPreset.Lossless => 0.35,
            _ => 0.1,
        };
        return (long)Math.Round(bitsPerPixel * width * height * fps / 1000.0) * 1000;
    }

    /// <summary>The preset closest to an old constant-quality setting (x264 CRF scale, 15 to 35).</summary>
    public static QualityPreset FromLegacyQuality(int quality) => quality switch
    {
        <= 17 => QualityPreset.Lossless,
        <= 21 => QualityPreset.High,
        <= 26 => QualityPreset.Balanced,
        _ => QualityPreset.Small,
    };

    /// <summary>4:2:0 video needs even dimensions: drop an odd last column or row.</summary>
    public static (int Width, int Height) EvenSize(int width, int height) => (width & ~1, height & ~1);

    private static int Pick(QualityPreset preset, int small, int balanced, int high, int lossless) => preset switch
    {
        QualityPreset.Small => small,
        QualityPreset.High => high,
        QualityPreset.Lossless => lossless,
        _ => balanced,
    };
}
