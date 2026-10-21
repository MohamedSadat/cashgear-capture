using CashGear.Capture.App.Hosting;
using CashGear.Capture.App.Resources;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Settings;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.App.Scrolling;

/// <summary>
/// Runs a scrolling capture of a picked window: finds the part that scrolls, brings the window to the front, then
/// captures and scrolls it step by step (see <see cref="ScrollingCaptureLoop"/>) with a progress window up.
/// Esc or Cancel abandons the capture; the mouse goes back where it was.
/// </summary>
public sealed partial class ScrollingCaptureService(
    IScreenCaptureService capture,
    IMonitorService monitors,
    IScrollPlatform platform,
    ScrollTargetFinder finder,
    IUiDispatcher ui,
    ISettingsService settings,
    TimeProvider time,
    ILogger<ScrollingCaptureService> logger)
{
    /// <summary>Right-hand columns (at 100% scale) allowed to differ between frames: a scrollbar and a little margin.</summary>
    public const int ScrollbarAllowance = 24;

    private static readonly TimeSpan EscapePoll = TimeSpan.FromMilliseconds(30);
    private static readonly TimeSpan RecheckDelay = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan ActivationDelay = TimeSpan.FromMilliseconds(250);

    /// <param name="point">Where the user clicked when picking the window (physical pixels).</param>
    /// <returns>The stitched capture, or null if the user cancelled.</returns>
    public async Task<ScrollingResult?> CaptureAsync(WindowInfo window, PixelPoint point, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(window);
        var current = settings.Current;
        var target = await Task.Run(() => finder.Find(window, point), ct).ConfigureAwait(false);
        if (target.Area.Width < ScrollTargetFinder.MinSize || target.Area.Height < ScrollTargetFinder.MinSize)
        {
            throw new CaptureException(CapturingText.NothingToScroll(window.Title));
        }

        var monitor = monitors.GetMonitors().FirstOrDefault(m => m.Bounds.Contains(Center(target.Area)))
            ?? monitors.GetMonitors().First(m => m.IsPrimary);
        var cursor = platform.CursorPosition;
        LogStarting(window.Title, target.Area, target.Source);

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var (progressWindow, progress) = await ui.InvokeAsync(() =>
        {
            var viewModel = new ScrollProgressViewModel(ui, () => cancel.Cancel());
            var shown = new ScrollProgressWindow(viewModel, current.Appearance.Theme, monitor.WorkArea, target.WheelPoint);
            shown.Show();
            return (shown, viewModel);
        }).ConfigureAwait(false);

        var escape = WatchEscapeAsync(cancel);
        try
        {
            platform.BringToFront(window.Handle);
            await Task.Delay(ActivationDelay, time, cancel.Token).ConfigureAwait(false);

            var options = new ScrollingOptions(
                TimeSpan.FromMilliseconds(current.Capture.ScrollSettleMs),
                current.Capture.ScrollMaxHeight,
                (int)Math.Ceiling(ScrollbarAllowance * monitor.Scale.ScaleX),
                RecheckDelay);
            var metadata = new CaptureMetadata
            {
                Kind = CaptureKind.Scrolling,
                SourceBounds = target.Area,
                CapturedAt = time.GetLocalNow(),
                WindowTitle = window.Title,
                MonitorName = monitor.ShortName,
                IsPrimaryMonitor = monitor.IsPrimary,
                Dpi = monitor.Scale,
            };
            var loop = new ScrollingCaptureLoop(t => capture.CaptureRegionAsync(target.Area, includeCursor: false, t), time, logger);
            return await loop.RunAsync(target.Scroller, options, progress, metadata, cancel.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            LogCancelled();
            return null;
        }
        finally
        {
            await cancel.CancelAsync().ConfigureAwait(false);
            await escape.ConfigureAwait(false);
            await ui.InvokeAsync(progressWindow.Close).ConfigureAwait(false);
            platform.CursorPosition = cursor;
        }
    }

    /// <summary>Esc anywhere cancels (the window being scrolled has the focus, so a key binding wouldn't see it).</summary>
    private async Task WatchEscapeAsync(CancellationTokenSource cancel)
    {
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                if (platform.IsEscapeDown())
                {
                    await cancel.CancelAsync().ConfigureAwait(false);
                    return;
                }

                await Task.Delay(EscapePoll, time, cancel.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // finished or cancelled
        }
    }

    private static PixelPoint Center(PixelRect r) => new(r.X + (r.Width / 2), r.Y + (r.Height / 2));

    [LoggerMessage(Level = LogLevel.Information, Message = "Scrolling capture of \"{Title}\": area {Area} ({Source})")]
    private partial void LogStarting(string title, PixelRect area, string source);

    [LoggerMessage(Level = LogLevel.Information, Message = "Scrolling capture cancelled by the user")]
    private partial void LogCancelled();
}
