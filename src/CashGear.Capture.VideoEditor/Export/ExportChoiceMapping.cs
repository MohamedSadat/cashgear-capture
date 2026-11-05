using System.ComponentModel;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Media;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Resources;

namespace CashGear.Capture.VideoEditor.Export;

/// <summary>What this licence lets an export do (everything, in this version).</summary>
public sealed record ExportAllowances(bool Hdr = true, bool Gif = true, bool Canvas = true, bool SoundPost = true)
{
    public static ExportAllowances All { get; } = new();
}

/// <summary>An export's request made of its choices.</summary>
/// <param name="Note">What couldn't be as asked (an HDR preset on a project without HDR media), for the user.</param>
public sealed record ChosenExport(ExportRequest Request, string? Note);

/// <summary>The one way from the export window's choices to an export, whether it comes from the window or a batch.</summary>
public static class ExportChoiceMapping
{
    /// <summary>The kind of file these choices ask for, in a form this project and this licence can have.</summary>
    public static (ExportPreset Preset, string? Note) PresetFor(this ExportChoices choices, VideoProject project, ExportAllowances? allowances = null)
    {
        ArgumentNullException.ThrowIfNull(choices);
        var allowed = allowances ?? ExportAllowances.All;
        var preset = ExportPresets.Find(choices.Preset) ?? ExportPresets.Mp4H264Hd;
        if (preset.Hdr && !(allowed.Hdr && ProjectExporter.HasHdrMedia(project)))
        {
            return (ExportPresets.Mp4Hevc, VideoEditorStrings.NoHdrMediaNote);
        }

        if ((preset.IsGif && !allowed.Gif) || (preset.HasCanvas && !allowed.Canvas))
        {
            return (ExportPresets.Mp4H264Hd, VideoEditorStrings.PresetNotInLicenceNote);
        }

        return (preset, null);
    }

    /// <summary>The GIF's options among the choices.</summary>
    public static GifOptions GifOptions(this ExportChoices choices)
    {
        ArgumentNullException.ThrowIfNull(choices);
        var dither = Enum.TryParse<GifDither>(choices.GifDither, out var parsed) ? parsed : GifDither.Pattern;
        return new GifOptions(choices.GifFps, choices.GifWidth, choices.GifLoop, Dither: dither, MaxBytes: (long)Math.Round(choices.GifMaxMegabytes * 1024 * 1024));
    }

    /// <param name="project">The project as it is now.</param>
    /// <param name="path">The file to write.</param>
    /// <param name="encoderSetting">The recording setting for the encoder.</param>
    /// <param name="range">Only this part of the timeline.</param>
    /// <param name="burnTrack">The caption track burned in, when there is a choice (null: the first shown).</param>
    public static ChosenExport ToRequest(this ExportChoices choices, VideoProject project, string path, string? encoderSetting, TimeRange? range = null, string? burnTrack = null, ExportAllowances? allowances = null)
    {
        ArgumentNullException.ThrowIfNull(choices);
        ArgumentNullException.ThrowIfNull(project);
        var allowed = allowances ?? ExportAllowances.All;
        var (preset, note) = choices.PresetFor(project, allowed);
        var soundPost = allowed.SoundPost && preset.HasSound;
        double? loudness = soundPost && choices.LoudnessLufs != 0 ? choices.LoudnessLufs : null;
        var tracks = soundPost && ProjectExporter.HeardTracks(project).Count > 0 && Enum.TryParse<SoundTracksOut>(choices.TracksOut, out var chosen)
            ? (chosen == SoundTracksOut.Streams && !preset.CanHoldTracks ? SoundTracksOut.Mix : chosen)
            : SoundTracksOut.Mix;
        var captionTracks = project.Tracks.Where(t => t.Kind == TrackKind.Caption && SubtitleFiles.Cues(project, t.Id).Count > 0).ToList();
        var files = captionTracks.Count == 0
            ? CaptionFiles.None
            : (choices.SaveSrt ? CaptionFiles.Srt : CaptionFiles.None) | (choices.SaveVtt ? CaptionFiles.Vtt : CaptionFiles.None);
        var burned = captionTracks.Count > 1 ? burnTrack ?? (captionTracks.FirstOrDefault(t => !t.Hidden) ?? captionTracks[0]).Id : null;
        var chapters = (choices.Chapters && preset.CanHoldChapters ? ExportChapterFiles.InFile : ExportChapterFiles.None)
            | (choices.ChaptersVtt ? ExportChapterFiles.Vtt : ExportChapterFiles.None);
        var request = new ExportRequest(project, preset, choices.Quality, path, encoderSetting, range, choices.BurnCaptions, files, preset.IsGif ? choices.GifOptions() : null, burned, loudness, tracks, chapters);
        return new ChosenExport(request, note);
    }
}

/// <summary>The presets that come with the app, in the user's language. Saved ones are in <see cref="Editor.Presets.PresetLibrary"/>.</summary>
public static class BuiltInExports
{
    [Localizable(false)]
    public static IReadOnlyList<NamedExport> All =>
    [
        new("youtube-1080", VideoEditorStrings.ExportYouTube1080, new ExportChoices(ExportPresets.Mp4H264Hd.Id, LoudnessLufs: ExportLoudness.YouTube)),
        new("shorts-9x16", VideoEditorStrings.ExportShorts, new ExportChoices(ExportPresets.Vertical.Id, LoudnessLufs: ExportLoudness.YouTube)),
        new("docs-gif", VideoEditorStrings.ExportDocsGif, new ExportChoices(ExportPresets.Gif.Id, GifFps: 12, GifWidth: 800, GifMaxMegabytes: 10)),
    ];
}
