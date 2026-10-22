using CashGear.Capture.Core.Recording;
using CashGear.Capture.Media;
using CashGear.Capture.VideoEditor.Resources;

namespace CashGear.Capture.VideoEditor.Export;

/// <summary>
/// A kind of file an export makes: the container, the video codec (none for sound only and GIFs), the sound codec,
/// and the picture's height (0: the project's own size; a GIF's size comes from its <see cref="GifOptions"/>).
/// </summary>
public sealed record ExportPreset(string Id, string Name, ExportContainer Container, VideoCodec? Codec, string AudioCodec, int Height, string Extension)
{
    /// <summary>An animated GIF: pictures without sound, two passes for its palette.</summary>
    public bool IsGif => Container == ExportContainer.Gif;

    /// <summary>It has pictures (a video, or a GIF).</summary>
    public bool HasVideo => Codec is not null || IsGif;

    /// <summary>It has sound (not a GIF).</summary>
    public bool HasSound => !IsGif;

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

/// <summary>
/// The export presets. Made on each use, so their names are in the language the UI has then (records compare by value).
/// </summary>
public static class ExportPresets
{
    public static ExportPreset Mp4H264Hd => new("mp4-1080", VideoEditorStrings.PresetMp4Hd, ExportContainer.Mp4, VideoCodec.H264, AudioEncoder.Aac, 1080, ".mp4");

    public static ExportPreset Mp4H264Small => new("mp4-720", VideoEditorStrings.PresetMp4Small, ExportContainer.Mp4, VideoCodec.H264, AudioEncoder.Aac, 720, ".mp4");

    public static ExportPreset Mp4H264Project => new("mp4", VideoEditorStrings.PresetMp4Project, ExportContainer.Mp4, VideoCodec.H264, AudioEncoder.Aac, 0, ".mp4");

    public static ExportPreset Mp4Hevc => new("mp4-hevc", VideoEditorStrings.PresetMp4Hevc, ExportContainer.Mp4, VideoCodec.Hevc, AudioEncoder.Aac, 0, ".mp4");

    public static ExportPreset WebMVp9 => new("webm-vp9", VideoEditorStrings.PresetWebMVp9, ExportContainer.WebM, VideoCodec.Vp9, AudioEncoder.Opus, 0, ".webm");

    public static ExportPreset WebMAv1 => new("webm-av1", VideoEditorStrings.PresetWebMAv1, ExportContainer.WebM, VideoCodec.Av1, AudioEncoder.Opus, 0, ".webm");

    public static ExportPreset M4a => new("m4a", VideoEditorStrings.PresetM4a, ExportContainer.M4a, null, AudioEncoder.Aac, 0, ".m4a");

    public static ExportPreset Mp3 => new("mp3", VideoEditorStrings.PresetMp3, ExportContainer.Mp3, null, AudioEncoder.Mp3, 0, ".mp3");

    public static ExportPreset Gif => new("gif", VideoEditorStrings.PresetGif, ExportContainer.Gif, null, "", 0, ".gif");

    public static IReadOnlyList<ExportPreset> All => [Mp4H264Hd, Mp4H264Small, Mp4H264Project, Mp4Hevc, WebMVp9, WebMAv1, Gif, M4a, Mp3];

    public static ExportPreset? Find(string? id) => All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// How a GIF is made: frames a second, width (never larger than the project; the height in proportion), whether it
/// plays over and over, colours, dithering, and a size to stay under (0: none), which makes it smaller step by step.
/// </summary>
public sealed record GifOptions(int Fps = 15, int Width = 800, bool Loop = true, int Colors = 256, GifDither Dither = GifDither.Pattern, long MaxBytes = 0)
{
    public const int SlowestFps = 5;
    public const int FastestFps = 50;
    public const int NarrowestWidth = 64;

    /// <summary>The GIF's size for a project: the width (at most the project's), the height in proportion, both even.</summary>
    public (int Width, int Height) Size(int projectWidth, int projectHeight)
    {
        var width = Math.Clamp(Width, NarrowestWidth, Math.Max(NarrowestWidth, projectWidth));
        var height = (int)Math.Round((double)projectHeight * width / projectWidth);
        return (Math.Max(2, width / 2 * 2), Math.Max(2, height / 2 * 2));
    }

    /// <summary>The next try at a smaller file: fewer colours, then fewer frames, then narrower; null when there is none left.</summary>
    public GifOptions? Smaller() =>
        Colors > 128 ? this with { Colors = 128 }
        : Fps > 10 ? this with { Fps = 10 }
        : Width > 480 ? this with { Width = Width * 3 / 4 }
        : Colors > 64 ? this with { Colors = 64 }
        : Width > 240 ? this with { Width = Width * 3 / 4 }
        : null;
}
