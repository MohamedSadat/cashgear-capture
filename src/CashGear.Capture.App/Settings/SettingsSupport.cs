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

/// <summary>A delay for one kind of capture: seconds, or null for the general capture delay.</summary>
public sealed record ModeDelayChoice(int? Seconds, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One kind of capture's delay in Settings › Capture.</summary>
public sealed partial class ModeDelayRow(CaptureMode mode) : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public CaptureMode Mode => mode;

    public string Label => mode.Label();

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private ModeDelayChoice? _choice;
}

/// <summary>True for false and false for true (a radio button for the "off" side of a setting).</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public static InverseBooleanConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
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
        PointerEffect.None => "None",
        PointerEffect.Halo => "A halo around it",
        PointerEffect.Spotlight => "A spotlight on it (the rest dimmed)",
        KeystrokeMode.Off => "Nothing",
        KeystrokeMode.Shortcuts => "Shortcuts (Ctrl, Alt or Win + a key, F-keys, Esc)",
        KeystrokeMode.AllKeys => "All keys, including typing",
        OverlayAnchor.BottomCenter => "Bottom centre",
        OverlayAnchor.BottomLeft => "Bottom left",
        OverlayAnchor.BottomRight => "Bottom right",
        OverlayAnchor.TopCenter => "Top centre",
        OverlayAnchor.TopLeft => "Top left",
        OverlayAnchor.TopRight => "Top right",
        WebcamShape.Circle => "Circle",
        WebcamShape.RoundedRectangle => "Rounded rectangle",
        ColorFormat.Hex => "HEX (#1E90FF)",
        ColorFormat.Rgb => "RGB (rgb(30, 144, 255))",
        ColorFormat.Hsl => "HSL (hsl(210, 100%, 56%))",
        ColorFormat.CSharp => "C# (Color.FromArgb(…))",
        PrintFit.FitToPage => "As large as fits (all of it)",
        PrintFit.FillPage => "Fill the page (the edges may be cut off)",
        PrintFit.ActualSize => "As big as on the screen",
        PageOrientation.Auto => "Landscape for wide captures, else portrait",
        PageOrientation.Portrait => "Portrait",
        PageOrientation.Landscape => "Landscape",
        ServerProtocol.Ftp => "FTP (not encrypted)",
        ServerProtocol.FtpsExplicit => "FTPS (FTP with TLS, port 21)",
        ServerProtocol.FtpsImplicit => "FTPS, implicit TLS (port 990)",
        ServerProtocol.Sftp => "SFTP (over SSH, port 22)",
        Core.Geometry.RulerUnit.Pixels => "Pixels",
        Core.Geometry.RulerUnit.Inches => "Inches",
        Core.Geometry.RulerUnit.Centimetres => "Centimetres",
        _ => value?.ToString() ?? string.Empty,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
