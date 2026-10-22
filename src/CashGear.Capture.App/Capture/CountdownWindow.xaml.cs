using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Interop;
using CashGear.Capture.App.Hosting;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Resources;
using CashGear.Capture.App.Scrolling;
using CashGear.Capture.App.Wpf;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Interop.Native;

namespace CashGear.Capture.App.Capture;

/// <summary>Counts down before a delayed capture.</summary>
public interface ICaptureCountdown
{
    /// <summary>
    /// Shows the count with what is about to be captured (e.g. "Open menu"), and completes after
    /// <paramref name="seconds"/>; throws <see cref="OperationCanceledException"/> if the user cancels.
    /// </summary>
    Task RunAsync(int seconds, string what, CancellationToken ct);
}

/// <summary>
/// Shows the remaining seconds in a small window in the bottom-right corner of the monitor with the mouse. The
/// window never takes the focus — the point of a delay is to open a menu or hover a tooltip, which focus would
/// close — and is left out of captures. It closes before the capture. Esc anywhere cancels: the key goes to the
/// user's window, so it is watched rather than handled.
/// </summary>
internal sealed class WpfCaptureCountdown(
    IUiDispatcher ui, ISettingsService settings, IMonitorService monitors, IScrollPlatform pointer, TimeProvider time) : ICaptureCountdown
{
    /// <summary>How often Esc is looked at.</summary>
    internal static readonly TimeSpan EscapePoll = TimeSpan.FromMilliseconds(30);

    public async Task RunAsync(int seconds, string what, CancellationToken ct)
    {
        if (seconds <= 0)
        {
            return;
        }

        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var cursor = pointer.CursorPosition;
        var monitor = monitors.GetMonitors() is { Count: > 0 } all
            ? all.FirstOrDefault(m => m.Bounds.Contains(cursor)) ?? all[0]
            : null;
        var window = await ui.InvokeAsync(() =>
        {
            var created = new CountdownWindow(settings.Current.Appearance.Theme, monitor?.WorkArea, cancelled.Cancel) { What = what };
            created.Show();
            return created;
        }).ConfigureAwait(false);

        using var counted = new CancellationTokenSource();
        var escape = WatchEscapeAsync(cancelled, counted.Token);
        try
        {
            for (var left = seconds; left > 0; left--)
            {
                var shown = left;
                await ui.InvokeAsync(() => window.Show(shown)).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(1), time, cancelled.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            await counted.CancelAsync().ConfigureAwait(false);
            await escape.ConfigureAwait(false);
            await ui.InvokeAsync(window.Close).ConfigureAwait(false);
        }
    }

    /// <summary>Cancels <paramref name="cancel"/> when Esc goes down, until <paramref name="done"/> (or the cancel).</summary>
    private async Task WatchEscapeAsync(CancellationTokenSource cancel, CancellationToken done)
    {
        using var either = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token, done);
        try
        {
            // An Esc already held when the count starts (the one that closed something) doesn't count: only a new press.
            var wasDown = pointer.IsEscapeDown();
            while (!either.IsCancellationRequested)
            {
                var down = pointer.IsEscapeDown();
                if (down && !wasDown)
                {
                    await cancel.CancelAsync().ConfigureAwait(false);
                    return;
                }

                wasDown = down;
                await Task.Delay(EscapePoll, time, either.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // counted down, or cancelled
        }
    }
}

public partial class CountdownWindow : Window, IThemedWindow
{
    private const int EdgeMargin = 16;
    private const int ExNoActivate = 0x08000000;
    private const int ExToolWindow = 0x00000080;

    private readonly PixelRect? _workArea;
    private readonly Action _cancel;

    public CountdownWindow(AppTheme theme, PixelRect? workArea, Action cancel)
    {
        InitializeComponent();
        _workArea = workArea;
        _cancel = cancel;
        ApplyTheme(theme);
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            User32.SetWindowLongPtrW(hwnd, User32.GwlExStyle, User32.GetWindowLongPtrW(hwnd, User32.GwlExStyle) | ExNoActivate | ExToolWindow);
            User32.SetWindowDisplayAffinity(hwnd, User32.WdaExcludeFromCapture);
        };
        Loaded += (_, _) => Place();
    }

    public void ApplyTheme(AppTheme theme) => Theming.Apply(this, theme);

    /// <summary>What is captured when the count ends: "Open menu in 3".</summary>
    public string What
    {
        init => Heading.Text = CapturingText.CountdownWhat(value);
    }

    /// <summary>Shows the seconds left (and a screen reader says them).</summary>
    public void Show(int secondsLeft)
    {
        Seconds.Text = secondsLeft.ToString(CultureInfo.CurrentCulture);
        if (AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
        {
            UIElementAutomationPeer.CreatePeerForElement(Seconds)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _cancel();

    /// <summary>
    /// Bottom-right of the work area (bottom-left in a right-to-left language, where the eye starts), in physical
    /// pixels whatever the monitor's scale.
    /// </summary>
    private void Place()
    {
        if (_workArea is not { } area)
        {
            return;
        }

        var scale = DpiHelper.GetScale(this);
        var width = (int)Math.Ceiling(ActualWidth * scale.ScaleX);
        var height = (int)Math.Ceiling(ActualHeight * scale.ScaleY);
        User32.SetWindowPos(
            new WindowInteropHelper(this).Handle,
            0,
            AppLanguage.IsRightToLeft ? area.X + EdgeMargin : area.Right - width - EdgeMargin,
            area.Bottom - height - EdgeMargin,
            0,
            0,
            User32.SwpNoActivate | 0x0001 /* NOSIZE */ | 0x0004 /* NOZORDER */);
    }
}
