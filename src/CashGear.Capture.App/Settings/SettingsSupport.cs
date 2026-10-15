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

/// <summary>Friendly names for the enums shown in the settings window.</summary>
public sealed class EnumLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ImageFileFormat.Png => "PNG (lossless)",
        Core.Recording.RecordingEncoder.Auto => "Hardware when available (recommended)",
        Core.Recording.RecordingEncoder.Software => "Software (x264)",
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
