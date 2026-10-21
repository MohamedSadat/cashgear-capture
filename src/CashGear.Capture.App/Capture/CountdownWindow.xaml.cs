using System.Globalization;
using System.Windows;
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
/// close — and is left out of captures. It closes before the capture.
/// </summary>
internal sealed class WpfCaptureCountdown(
    IUiDispatcher ui, ISettingsService settings, IMonitorService monitors, IScrollPlatform pointer, TimeProvider time) : ICaptureCountdown
{
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
            await ui.InvokeAsync(window.Close).ConfigureAwait(false);
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

    /// <summary>Shows the seconds left.</summary>
    public void Show(int secondsLeft) => Seconds.Text = secondsLeft.ToString(CultureInfo.CurrentCulture);

    private void OnCancel(object sender, RoutedEventArgs e) => _cancel();

    /// <summary>Bottom-right of the work area, in physical pixels whatever the monitor's scale.</summary>
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
            new WindowInteropHelper(this).Handle, 0, area.Right - width - EdgeMargin, area.Bottom - height - EdgeMargin, 0, 0,
            User32.SwpNoActivate | 0x0001 /* NOSIZE */ | 0x0004 /* NOZORDER */);
    }
}
