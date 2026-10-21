using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Resources;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.App.Tray;

/// <summary>
/// Says that Windows never let the app add its tray icon. A modeless window rather than a message box: a message box
/// runs its own modal loop, and WPF can't finish exiting (<c>--exit</c>, restart to update) until that loop ends, so
/// the app stayed alive until the user clicked OK. <see cref="TrayIconHost"/> closes this window when the app exits.
/// </summary>
public partial class TrayIconRefusedWindow : Window, IThemedWindow
{
    public TrayIconRefusedWindow(string logDirectory, AppTheme theme)
    {
        InitializeComponent();
        MessageText.Text = Message(logDirectory);
        WarningIcon.Source = Imaging.CreateBitmapSourceFromHIcon(
            System.Drawing.SystemIcons.Warning.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        ApplyTheme(theme);
    }

    internal static string Message(string logDirectory) => TrayText.IconRefused(logDirectory);

    public void ApplyTheme(AppTheme theme) => Theming.Apply(this, theme);

    private void OnOkClick(object sender, RoutedEventArgs e) => Close();
}
