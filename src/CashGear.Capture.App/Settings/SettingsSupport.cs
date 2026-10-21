using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using CashGear.Capture.App.Resources;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Settings;
using Microsoft.Win32;

namespace CashGear.Capture.App.Settings;

internal sealed class WpfFolderPicker : IFolderPicker
{
    public string? PickFolder(string? initialFolder)
    {
        var dialog = new OpenFolderDialog
        {
            Title = SettingsText.ChooseCaptureFolder,
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

/// <summary>An update channel in Settings › General › Updates; null follows the channel installed.</summary>
public sealed record ChannelChoice(UpdateChannel? Value, string Label)
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
        ImageFileFormat.Png => SettingsText.FormatPng,
        Core.Recording.QualityPreset.Small => SettingsText.QualitySmall,
        Core.Recording.QualityPreset.Balanced => SettingsText.QualityBalanced,
        Core.Recording.QualityPreset.High => SettingsText.QualityHigh,
        Core.Recording.QualityPreset.Lossless => SettingsText.QualityLossless,
        Core.Recording.AudioTrackLayout.Mixed => SettingsText.TracksMixed,
        Core.Recording.AudioTrackLayout.Separate => SettingsText.TracksSeparate,
        Core.Recording.AudioTrackLayout.Both => SettingsText.TracksBoth,
        Core.Recording.SystemAudioScope.All => SettingsText.AudioAll,
        Core.Recording.SystemAudioScope.OnlyApp => SettingsText.AudioOnlyApp,
        Core.Recording.SystemAudioScope.AllExceptApp => SettingsText.AudioAllExceptApp,
        ImageFileFormat.Jpeg => "JPEG",
        ImageFileFormat.Bmp => "BMP",
        CaptureBackend.Auto => SettingsText.BackendAuto,
        CaptureBackend.Wgc => "Windows.Graphics.Capture",
        CaptureBackend.Gdi => SettingsText.BackendGdi,
        HdrCapture.Auto => SettingsText.HdrAuto,
        HdrCapture.Off => SettingsText.HdrOff,
        FullScreenOutput.AllMonitors => SettingsText.FullScreenAll,
        FullScreenOutput.EachMonitor => SettingsText.FullScreenEach,
        FullScreenOutput.AllAndEachMonitor => SettingsText.FullScreenBoth,
        AppTheme.System => SettingsText.ThemeSystem,
        AppTheme.Light => SettingsText.ThemeLight,
        AppTheme.Dark => SettingsText.ThemeDark,
        UiLanguage.Automatic => SettingsText.LanguageAutomatic,
        UiLanguage.English => SettingsText.LanguageEnglish,
        UiLanguage.Arabic => SettingsText.LanguageArabic,
        PointerEffect.None => CommonText.None,
        PointerEffect.Halo => OverlaysText.PointerHalo,
        PointerEffect.Spotlight => OverlaysText.PointerSpotlight,
        KeystrokeMode.Off => OverlaysText.KeystrokesOff,
        KeystrokeMode.Shortcuts => OverlaysText.KeystrokesShortcuts,
        KeystrokeMode.AllKeys => OverlaysText.KeystrokesAll,
        OverlayAnchor.BottomCenter => OverlaysText.AnchorBottomCenter,
        OverlayAnchor.BottomLeft => OverlaysText.AnchorBottomLeft,
        OverlayAnchor.BottomRight => OverlaysText.AnchorBottomRight,
        OverlayAnchor.TopCenter => OverlaysText.AnchorTopCenter,
        OverlayAnchor.TopLeft => OverlaysText.AnchorTopLeft,
        OverlayAnchor.TopRight => OverlaysText.AnchorTopRight,
        WebcamShape.Circle => OverlaysText.ShapeCircle,
        WebcamShape.RoundedRectangle => OverlaysText.ShapeRoundedRectangle,
        ColorFormat.Hex => "HEX (#1E90FF)",
        ColorFormat.Rgb => "RGB (rgb(30, 144, 255))",
        ColorFormat.Hsl => "HSL (hsl(210, 100%, 56%))",
        ColorFormat.CSharp => "C# (Color.FromArgb(…))",
        PrintFit.FitToPage => DestinationsText.PrintFitToPage,
        PrintFit.FillPage => DestinationsText.PrintFillPage,
        PrintFit.ActualSize => DestinationsText.PrintActualSize,
        PageOrientation.Auto => DestinationsText.OrientationAuto,
        PageOrientation.Portrait => DestinationsText.OrientationPortrait,
        PageOrientation.Landscape => DestinationsText.OrientationLandscape,
        PaperSize.Auto => DestinationsText.PaperAuto,
        PaperSize.A4 => "A4",
        PaperSize.Letter => DestinationsText.PaperLetter,
        PaperSize.Legal => DestinationsText.PaperLegal,
        PaperSize.A3 => "A3",
        PaperSize.A5 => "A5",
        PaperSize.Picture => DestinationsText.PaperPicture,
        ServerProtocol.Ftp => DestinationsText.ProtocolFtpLabel,
        ServerProtocol.FtpsExplicit => DestinationsText.ProtocolFtpsExplicitLabel,
        ServerProtocol.FtpsImplicit => DestinationsText.ProtocolFtpsImplicitLabel,
        ServerProtocol.Sftp => DestinationsText.ProtocolSftpLabel,
        Core.Geometry.RulerUnit.Pixels => SettingsText.RulerPixels,
        Core.Geometry.RulerUnit.Inches => SettingsText.RulerInches,
        Core.Geometry.RulerUnit.Centimetres => SettingsText.RulerCentimetres,
        _ => value?.ToString() ?? string.Empty,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
