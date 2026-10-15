using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using CashGear.Capture.App.Hosting;
using CashGear.Capture.Interop.Native;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.App.Tray;

/// <summary>Owns the notification-area icon defined in <c>TrayIconResources.xaml</c>.</summary>
internal sealed partial class TrayIconHost(IUiDispatcher ui, ILogger<TrayIconHost> logger) : ITrayNotifier, IDisposable
{
    /// <summary>How long to keep trying when Windows refuses the icon (e.g. the taskbar isn't up yet at sign-in).</summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);
    private const int MaxAttempts = 60;

    private TaskbarIcon? _icon;
    private System.Drawing.Icon? _image;
    private Action? _onClick;
    private DispatcherTimer? _retry;
    private int _attempts;
    private bool _created;
    private (string Title, string Message, NotificationKind Kind, Action? OnClick)? _pending;

    /// <summary>
    /// Creates the icon. UI thread only. Never throws because Windows refused the icon: it retries for a while (the
    /// app works through its hotkeys meanwhile) and says so if it never succeeds.
    /// </summary>
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

        icon.Id = IconId(Environment.ProcessPath ?? AppContext.BaseDirectory);
        icon.Icon = _image;
        icon.TrayBalloonTipClicked += OnNotificationClicked;
        _icon = icon;
        TryCreate();
    }

    /// <summary>
    /// The icon's identity for Windows, one per program file. Windows ties an icon GUID to the program that last
    /// used it and refuses it from any other path while that file exists. H.NotifyIcon's default GUID comes from the
    /// assembly name, so a development build and the installed copy (same name, other folders) locked each other
    /// out and the installed app started without an icon. The installed copy always runs from <c>current\</c>, so
    /// it keeps its GUID across updates and Windows keeps the user's "always show this icon" choice.
    /// </summary>
    internal static Guid IconId(string programPath) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes("CashGear.Capture tray icon|" + programPath.ToUpperInvariant()))[..16]);

    private void TryCreate()
    {
        if (_icon is null || _created)
        {
            return;
        }

        _attempts++;
        try
        {
            // Efficiency mode (on by default in H.NotifyIcon) drops the process to idle priority — bad for capture latency.
            _icon.ForceCreate(enablesEfficiencyMode: false);
            _created = true;
            _retry?.Stop();
            LogCreated(_image?.Width ?? 0, _icon.Id, _attempts);
            if (_pending is { } pending)
            {
                _pending = null;
                Notify(pending.Title, pending.Message, pending.Kind, pending.OnClick);
            }
        }
        catch (InvalidOperationException ex)
        {
            if (_attempts == 1)
            {
                LogCreateFailed(ex);
            }

            if (_attempts >= MaxAttempts)
            {
                _retry?.Stop();
                LogGaveUp(_attempts);
                MessageBox.Show(
                    "CashGear Capture is running, but Windows did not let it add its icon to the notification area.\n\n" +
                    "The capture hotkeys still work. Restarting the app (or Windows) usually brings the icon back.",
                    "CashGear Capture",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            _retry ??= new DispatcherTimer(RetryInterval, DispatcherPriority.Background, (_, _) => TryCreate(), Dispatcher.CurrentDispatcher);
            _retry.Start();
        }
    }

    public void Notify(string title, string message, NotificationKind kind = NotificationKind.Info, Action? onClick = null)
    {
        if (!ui.CheckAccess())
        {
            _ = ui.InvokeAsync(() => Notify(title, message, kind, onClick));
            return;
        }

        if (_icon is null || !_created)
        {
            // Shown once the icon exists (e.g. the first-run welcome while Windows is still refusing the icon).
            _pending = (title, message, kind, onClick);
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
        _retry?.Stop();
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Tray icon created ({Size} px, id {Id}, attempt {Attempt})")]
    private partial void LogCreated(int size, Guid id, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Windows refused the tray icon; retrying every 2 s")]
    private partial void LogCreateFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Gave up adding the tray icon after {Attempts} attempts")]
    private partial void LogGaveUp(int attempts);

    [LoggerMessage(Level = LogLevel.Information, Message = "Notification clicked (has an action: {HasAction})")]
    private partial void LogClicked(bool hasAction);

    [LoggerMessage(Level = LogLevel.Error, Message = "Handling a click on a notification failed")]
    private partial void LogClickFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Notification '{Title}' held until the tray icon exists")]
    private partial void LogNotShown(string title);

    [LoggerMessage(Level = LogLevel.Debug, Message = "SetWindowDisplayAffinity on tray menu failed (Win32 error {Error})")]
    private partial void LogAffinityFailed(int error);
}
