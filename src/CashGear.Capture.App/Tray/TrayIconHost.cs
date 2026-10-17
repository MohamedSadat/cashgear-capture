using System.Media;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using CashGear.Capture.App.Hosting;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Interop.Native;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.App.Tray;

/// <summary>Owns the notification-area icon defined in <c>TrayIconResources.xaml</c>.</summary>
internal sealed partial class TrayIconHost(IUiDispatcher ui, AppPaths paths, ISettingsService settings, ILogger<TrayIconHost> logger)
    : ITrayNotifier, IDisposable
{
    /// <summary>How long to keep trying when Windows refuses the icon (e.g. the taskbar isn't up yet at sign-in).</summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);
    private const int MaxAttempts = 60;

    /// <summary>Icon ids tried for one program file: its usual id, then fallbacks for when another copy holds it.</summary>
    internal const int IdSlots = 3;

    private TaskbarIcon? _icon;
    private System.Drawing.Icon? _image;
    private Action? _onClick;
    private DispatcherTimer? _retry;
    private TrayIconRefusedWindow? _refused;
    private Guid[] _ids = [];
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

        var programPath = Environment.ProcessPath ?? AppContext.BaseDirectory;
        _ids = [.. Enumerable.Range(0, IdSlots).Select(slot => IconId(programPath, slot))];
        icon.Icon = _image;
        icon.TrayBalloonTipClicked += OnNotificationClicked;
        _icon = icon;
        TryCreate();
    }

    /// <summary>
    /// The icon's identity for Windows, one per program file. Windows ties an icon GUID to the program file that first
    /// used it and refuses it from any other file, even after that file is deleted. H.NotifyIcon's default GUID comes
    /// from the assembly name, so a development build and the installed copy (same name, other folders) locked each
    /// other out and the installed app started without an icon. The installed copy always runs from <c>current\</c>,
    /// so it keeps its GUID across updates and Windows keeps the user's "always show this icon" choice.
    /// <para>
    /// Two copies can still report the same path: one installed from a packaged app's shell (e.g. Claude's) lands in
    /// that app's virtualized AppData but sees the real AppData path. Whichever starts first takes slot 0, so the
    /// other falls back to slot 1 (and so on). Slot 0 keeps the original formula so existing installs keep their id.
    /// </para>
    /// </summary>
    internal static Guid IconId(string programPath, int slot = 0) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(
            "CashGear.Capture tray icon|" + programPath.ToUpperInvariant() + (slot == 0 ? "" : "|" + slot)))[..16]);

    private void TryCreate()
    {
        if (_icon is null || _created)
        {
            return;
        }

        _attempts++;
        InvalidOperationException? refusal = null;
        for (var slot = 0; slot < _ids.Length; slot++)
        {
            _icon.Id = _ids[slot];
            try
            {
                // Efficiency mode (on by default in H.NotifyIcon) drops the process to idle priority — bad for capture latency.
                _icon.ForceCreate(enablesEfficiencyMode: false);
            }
            catch (InvalidOperationException ex)
            {
                refusal ??= ex;
                continue;
            }

            _created = true;
            _retry?.Stop();
            LogCreated(_image?.Width ?? 0, _icon.Id, _attempts);
            if (slot > 0)
            {
                LogFallbackId(_ids[0], slot);
            }

            if (_pending is { } pending)
            {
                _pending = null;
                Notify(pending.Title, pending.Message, pending.Kind, pending.OnClick);
            }

            return;
        }

        if (_attempts == 1)
        {
            LogCreateFailed(refusal!);
        }

        if (_attempts >= MaxAttempts)
        {
            _retry?.Stop();
            LogGaveUp(_attempts);
            SystemSounds.Exclamation.Play(); // what the warning message box used to play
            ShowRefusedWarning();
            return;
        }

        _retry ??= new DispatcherTimer(RetryInterval, DispatcherPriority.Background, (_, _) => TryCreate(), Dispatcher.CurrentDispatcher);
        _retry.Start();
    }

    /// <summary>The warning after the last refusal. Returns at once; <see cref="Dispose"/> closes it when the app exits.</summary>
    internal TrayIconRefusedWindow ShowRefusedWarning()
    {
        var window = new TrayIconRefusedWindow(paths.LogDirectory, settings.Current.Appearance.Theme);
        window.Closed += (_, _) => _refused = null;
        _refused = window;
        window.Show();
        window.Activate();
        return window;
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
        _refused?.Close();
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tray icon id {Usual} is held by another copy of the program; using fallback id {Slot}")]
    private partial void LogFallbackId(Guid usual, int slot);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Windows refused the tray icon under every id; retrying every 2 s")]
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
