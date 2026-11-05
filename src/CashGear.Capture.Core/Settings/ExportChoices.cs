using CashGear.Capture.Core.Recording;

namespace CashGear.Capture.Core.Settings;

/// <summary>
/// What the export window has chosen, apart from the file: everything a saved preset keeps. An export with these choices is
/// the same whether it comes from the window or a batch.
/// </summary>
/// <param name="Preset">The kind of file (an export preset's id).</param>
/// <param name="LoudnessLufs">The loudness aimed at (0: as mixed).</param>
/// <param name="TracksOut">How the sound tracks go out (the name of a <c>SoundTracksOut</c>; null: only the mix).</param>
/// <param name="BurnCaptions">Captions that are shown are drawn into the pictures.</param>
/// <param name="SaveSrt">Captions are also saved as SRT.</param>
/// <param name="SaveVtt">Captions are also saved as WebVTT.</param>
/// <param name="Chapters">The markers become chapters in the file.</param>
/// <param name="ChaptersVtt">The markers are also saved as a WebVTT chapters file.</param>
/// <param name="GifDither">The name of a <c>GifDither</c>.</param>
/// <param name="GifMaxMegabytes">0: no limit.</param>
/// <param name="SendTo">A destination the file goes to when it is done (null: stays here).</param>
public sealed record ExportChoices(
    string Preset = "mp4-1080",
    QualityPreset Quality = QualityPreset.High,
    double LoudnessLufs = -16,
    string? TracksOut = null,
    bool BurnCaptions = true,
    bool SaveSrt = false,
    bool SaveVtt = false,
    bool Chapters = true,
    bool ChaptersVtt = false,
    int GifFps = 15,
    int GifWidth = 800,
    bool GifLoop = true,
    string? GifDither = null,
    double GifMaxMegabytes = 0,
    string? SendTo = null);

/// <summary>A preset of the export window's choices, by name. A saved one lives in presets.json.</summary>
public sealed record NamedExport(string Id, string Name, ExportChoices Choices);
