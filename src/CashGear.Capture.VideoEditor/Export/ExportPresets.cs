using CashGear.Capture.Core.Recording;
using CashGear.Capture.Media;

namespace CashGear.Capture.VideoEditor.Export;

/// <summary>
/// A kind of file an export makes: the container, the video codec (none for sound only), the sound codec, and the
/// picture's height (0: the project's own size).
/// </summary>
public sealed record ExportPreset(string Id, string Name, ExportContainer Container, VideoCodec? Codec, string AudioCodec, int Height, string Extension)
{
    public bool HasVideo => Codec is not null;

    /// <summary>The picture's size for a project: the preset's height (the width in proportion), even, and in multiples of 8 for HEVC.</summary>
    public (int Width, int Height) Size(int projectWidth, int projectHeight)
    {
        var height = Height > 0 ? Height : projectHeight;
        var width = (int)Math.Round((double)projectWidth * height / projectHeight);
        var step = Codec == VideoCodec.Hevc ? 8 : 2;
        return (Math.Max(step, width / step * step), Math.Max(step, height / step * step));
    }

    /// <summary>Sound bits per second for the preset's codec.</summary>
    public int AudioBitRate => AudioCodec switch
    {
        AudioEncoder.Mp3 => 192_000,
        AudioEncoder.Opus => 128_000,
        _ => AudioEncoder.DefaultBitRate,
    };

    public override string ToString() => Name;
}

public static class ExportPresets
{
    public static ExportPreset Mp4H264Hd { get; } = new("mp4-1080", "MP4, H.264, 1080p", ExportContainer.Mp4, VideoCodec.H264, AudioEncoder.Aac, 1080, ".mp4");

    public static ExportPreset Mp4H264Small { get; } = new("mp4-720", "MP4, H.264, 720p", ExportContainer.Mp4, VideoCodec.H264, AudioEncoder.Aac, 720, ".mp4");

    public static ExportPreset Mp4H264Project { get; } = new("mp4", "MP4, H.264, the project's size", ExportContainer.Mp4, VideoCodec.H264, AudioEncoder.Aac, 0, ".mp4");

    public static ExportPreset Mp4Hevc { get; } = new("mp4-hevc", "MP4, HEVC, the project's size", ExportContainer.Mp4, VideoCodec.Hevc, AudioEncoder.Aac, 0, ".mp4");

    public static ExportPreset WebMVp9 { get; } = new("webm-vp9", "WebM, VP9", ExportContainer.WebM, VideoCodec.Vp9, AudioEncoder.Opus, 0, ".webm");

    public static ExportPreset WebMAv1 { get; } = new("webm-av1", "WebM, AV1", ExportContainer.WebM, VideoCodec.Av1, AudioEncoder.Opus, 0, ".webm");

    public static ExportPreset M4a { get; } = new("m4a", "M4A (AAC), sound only", ExportContainer.M4a, null, AudioEncoder.Aac, 0, ".m4a");

    public static ExportPreset Mp3 { get; } = new("mp3", "MP3, sound only", ExportContainer.Mp3, null, AudioEncoder.Mp3, 0, ".mp3");

    public static IReadOnlyList<ExportPreset> All { get; } = [Mp4H264Hd, Mp4H264Small, Mp4H264Project, Mp4Hevc, WebMVp9, WebMAv1, M4a, Mp3];

    public static ExportPreset? Find(string? id) => All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}
