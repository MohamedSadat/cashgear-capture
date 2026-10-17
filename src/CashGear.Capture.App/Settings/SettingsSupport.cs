using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Settings;
using Microsoft.Win32;

namespace CashGear.Capture.App.Settings;

internal sealed class WpfFolderPicker : IFolderPicker
{
    public string? PickFolder(string? initialFolder)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose where captures are saved",
            InitialDirectory = initialFolder is not null && Directory.Exists(initialFolder) ? initialFolder : null,
        };
        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        return dialog.ShowDialog(owner) == true ? dialog.FolderName : null;
    }
}

/// <summary>An entry of the OCR language list: a Windows OCR language tag, or null for automatic.</summary>
public sealed record OcrLanguageChoice(string? Tag, string Label)
{
    public override string ToString() => Label;
}

/// <summary>An entry of a sound device list: a device ID, or for microphones <see cref="RecordingSettings.DefaultMicrophone"/> or null for none, and for output devices null for Windows' default.</summary>
public sealed record DeviceChoice(string? Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>An encoder setting: <c>auto</c>, <c>software</c> or an encoder's id, with what detection found.</summary>
/// <summary>An entry of the app list for recording one app's sound: its program file (<c>spotify.exe</c>) and name.</summary>
public sealed record AppChoice(string Exe, string Name, string Label)
{
    public override string ToString() => Label;
}

public sealed record EncoderChoice(string Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A number with a label, e.g. how many captures the history keeps.</summary>
public sealed record CountChoice(int Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Friendly names for the enums shown in the settings window.</summary>
public sealed class EnumLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ImageFileFormat.Png => "PNG (lossless)",
        Core.Recording.QualityPreset.Small => "Small files",
        Core.Recording.QualityPreset.Balanced => "Balanced (recommended)",
        Core.Recording.QualityPreset.High => "High quality",
        Core.Recording.QualityPreset.Lossless => "Near lossless (very large files)",
        Core.Recording.AudioTrackLayout.Mixed => "One track with everything (plays everywhere)",
        Core.Recording.AudioTrackLayout.Separate => "One track per source (for editing)",
        Core.Recording.AudioTrackLayout.Both => "The mix, then one track per source",
        Core.Recording.SystemAudioScope.All => "All sound",
        Core.Recording.SystemAudioScope.OnlyApp => "Only one app",
        Core.Recording.SystemAudioScope.AllExceptApp => "All sound except one app",
        ImageFileFormat.Jpeg => "JPEG",
        ImageFileFormat.Bmp => "BMP",
        CaptureBackend.Auto => "Automatic (recommended)",
        CaptureBackend.Wgc => "Windows.Graphics.Capture",
        CaptureBackend.Gdi => "GDI (classic)",
        FullScreenOutput.AllMonitors => "One image of all monitors",
        FullScreenOutput.EachMonitor => "One image per monitor",
        FullScreenOutput.AllAndEachMonitor => "Both",
        AppTheme.System => "Use the Windows setting",
        AppTheme.Light => "Light",
        AppTheme.Dark => "Dark",
        _ => value?.ToString() ?? string.Empty,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
