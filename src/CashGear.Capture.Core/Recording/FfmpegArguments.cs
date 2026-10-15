using System.Globalization;

namespace CashGear.Capture.Core.Recording;

/// <summary>
/// ffmpeg command lines for recording: raw BGRA frames on stdin at a constant frame rate, encoded to H.264 in an MP4
/// that starts playing before it has fully downloaded (<c>+faststart</c>). One quality number drives every encoder.
/// </summary>
public static class FfmpegArguments
{
    public const int MinQuality = 15;
    public const int MaxQuality = 35;

    public static string Name(VideoEncoder encoder) => encoder switch
    {
        VideoEncoder.X264 => "libx264",
        VideoEncoder.Nvenc => "h264_nvenc",
        VideoEncoder.QuickSync => "h264_qsv",
        VideoEncoder.Amf => "h264_amf",
        _ => throw new ArgumentOutOfRangeException(nameof(encoder), encoder, null),
    };

    /// <summary>The encoders to try, best first: hardware (fast, little CPU) before x264, unless software was asked for.</summary>
    public static IReadOnlyList<VideoEncoder> Preference(RecordingEncoder setting) => setting == RecordingEncoder.Software
        ? [VideoEncoder.X264]
        : [VideoEncoder.Nvenc, VideoEncoder.QuickSync, VideoEncoder.Amf, VideoEncoder.X264];

    /// <summary>H.264 in 4:2:0 needs even dimensions: drop an odd last column or row.</summary>
    public static (int Width, int Height) EvenSize(int width, int height) => (width & ~1, height & ~1);

    public static IReadOnlyList<string> Record(int width, int height, int fps, int quality, VideoEncoder encoder, string output)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 2);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fps);
        ArgumentException.ThrowIfNullOrWhiteSpace(output);
        if (width % 2 != 0 || height % 2 != 0)
        {
            throw new ArgumentException($"H.264 needs an even size, not {width}x{height}.", nameof(width));
        }

        var q = Math.Clamp(quality, MinQuality, MaxQuality).ToString(CultureInfo.InvariantCulture);
        var args = new List<string>
        {
            "-hide_banner", "-nostats", "-loglevel", "warning",
            "-f", "rawvideo", "-pixel_format", "bgra",
            "-video_size", $"{width}x{height}",
            "-framerate", fps.ToString(CultureInfo.InvariantCulture),
            "-i", "pipe:0",
            "-c:v", Name(encoder),
        };

        args.AddRange(encoder switch
        {
            VideoEncoder.X264 => ["-preset", "veryfast", "-crf", q],
            VideoEncoder.Nvenc => ["-preset", "p4", "-rc", "vbr", "-cq", q, "-b:v", "0"],
            VideoEncoder.QuickSync => ["-preset", "veryfast", "-global_quality", q],
            VideoEncoder.Amf => ["-quality", "balanced", "-rc", "cqp", "-qp_i", q, "-qp_p", q],
            _ => throw new ArgumentOutOfRangeException(nameof(encoder), encoder, null),
        });

        args.AddRange(
        [
            "-pix_fmt", encoder == VideoEncoder.QuickSync ? "nv12" : "yuv420p",
            "-g", (fps * 2).ToString(CultureInfo.InvariantCulture), // a keyframe every 2 s: quick seeking
            "-movflags", "+faststart",
            "-y", output,
        ]);
        return args;
    }

    /// <summary>A tiny encode to nowhere: succeeds only if the encoder really works on this PC (driver, hardware).</summary>
    public static IReadOnlyList<string> Probe(VideoEncoder encoder) =>
    [
        "-hide_banner", "-loglevel", "error",
        "-f", "lavfi", "-i", "color=c=black:s=256x256:r=30:d=0.2",
        "-c:v", Name(encoder),
        "-pix_fmt", encoder == VideoEncoder.QuickSync ? "nv12" : "yuv420p",
        "-f", "null", "-",
    ];
}
