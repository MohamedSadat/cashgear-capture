using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using CashGear.Capture.App.Hosting;
using CashGear.Capture.Interop.Native;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.App.Tray;

/// <summary>Owns the notification-area icon defined in <c>TrayIconResources.xaml</c>.</summary>
internal sealed partial class TrayIconHost(IUiDispatcher ui, ILogger<TrayIconHost> logger) : ITrayNotifier, IDisposable
{
    private TaskbarIcon? _icon;
    private System.Drawing.Icon? _image;
    private Action? _onClick;

    /// <summary>Creates the icon. UI thread only.</summary>
    public void Show(TrayViewModel viewModel)
    {
        var icon = (TaskbarIcon)Application.Current.FindResource("TrayIcon");
        icon.DataContext = viewModel;
        if (icon.ContextMenu is { } menu)
        {
            menu.DataContext = viewModel;
            menu.Opened += OnMenuOpened;
        }

        // The app icon at the notification area's size (16 px at 100%), so Windows doesn't scale a bigger one down.
        var size = Math.Max(16, User32.GetSystemMetrics(User32.SmCxSmIcon));
        using (var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico")).Stream)
        {
            _image = new System.Drawing.Icon(stream, size, size);
        }

        icon.Icon = _image;
        icon.TrayBalloonTipClicked += OnNotificationClicked;

        // Efficiency mode (on by default in H.NotifyIcon) drops the process to idle priority — bad for capture latency.
        icon.ForceCreate(enablesEfficiencyMode: false);
        _icon = icon;
        LogCreated(size);
    }

    public void Notify(string title, string message, NotificationKind kind = NotificationKind.Info, Action? onClick = null)
    {
        if (!ui.CheckAccess())
        {
            _ = ui.InvokeAsync(() => Notify(title, message, kind, onClick));
            return;
        }

        if (_icon is null)
        {
            LogNotShown(title);
            return;
        }

        var icon = kind switch
        {
            NotificationKind.Warning => NotificationIcon.Warning,
            NotificationKind.Error => NotificationIcon.Error,
            _ => NotificationIcon.Info,
        };

        // Windows shows one notification per icon at a time, so a click always belongs to the latest one.
        _onClick = onClick;
        _icon.ShowNotification(title, message, icon);
    }

    public void Dispose()
    {
        if (_icon is null)
        {
            return;
        }

        if (_icon.ContextMenu is { } menu)
        {
            menu.Opened -= OnMenuOpened;
        }

        _icon.TrayBalloonTipClicked -= OnNotificationClicked;

        // Removes the icon immediately instead of leaving a ghost until the user hovers the tray.
        _icon.Dispose();
        _icon = null;
        _image?.Dispose();
        _image = null;
    }

    private void OnNotificationClicked(object sender, RoutedEventArgs e)
    {
        var action = _onClick;
        _onClick = null;
        LogClicked(action is not null);
        try
        {
            action?.Invoke();
        }
        catch (Exception ex)
        {
            LogClickFailed(ex);
        }
    }

    private void OnMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || PresentationSource.FromVisual(menu) is not HwndSource source)
        {
            return;
        }

        // Best effort: keeps a still-fading menu out of captures. May be refused for some layered popups.
        if (!User32.SetWindowDisplayAffinity(source.Handle, User32.WdaExcludeFromCapture))
        {
            LogAffinityFailed(Marshal.GetLastPInvokeError());
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Tray icon created ({Size} px)")]
    private partial void LogCreated(int size);

    [LoggerMessage(Level = LogLevel.Information, Message = "Notification clicked (has an action: {HasAction})")]
    private partial void LogClicked(bool hasAction);

    [LoggerMessage(Level = LogLevel.Error, Message = "Handling a click on a notification failed")]
    private partial void LogClickFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification '{Title}' dropped: tray icon not created yet")]
    private partial void LogNotShown(string title);

    [LoggerMessage(Level = LogLevel.Debug, Message = "SetWindowDisplayAffinity on tray menu failed (Win32 error {Error})")]
    private partial void LogAffinityFailed(int error);
}
