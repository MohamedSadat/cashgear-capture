using System.ComponentModel;
using CashGear.Capture.VideoEditor.Resources;

namespace CashGear.Capture.VideoEditor.Model;

/// <summary>
/// How a track's name is shown. Tracks made from a recording are named after its sound and picture streams, whose
/// titles the file keeps in English (and code matches); those are shown in the app's language.
/// </summary>
public static class TrackNames
{
    /// <summary><paramref name="name"/> as the UI shows it.</summary>
    public static string Display(string name) => Stored(name) switch
    {
        "microphone" => VideoEditorStrings.TrackMicrophone,
        "system audio" => VideoEditorStrings.TrackSystemAudio,
        "click sounds" => VideoEditorStrings.TrackClickSounds,
        "mixed" => VideoEditorStrings.TrackMixed,
        "webcam" => VideoEditorStrings.TrackWebcam,
        _ => name,
    };

    [Localizable(false)] // the titles as recordings store them
    private static string Stored(string name) => name.ToLowerInvariant();
}
