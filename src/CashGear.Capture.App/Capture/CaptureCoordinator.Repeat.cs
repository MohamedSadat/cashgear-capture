using CashGear.Capture.App.Tray;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Geometry;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.App.Capture;

/// <summary>Repeat Last Capture: the same kind of capture again, of the same pixels or window, without selecting.</summary>
public sealed partial class CaptureCoordinator
{
    /// <summary>The last capture taken, as Repeat Last Capture would take it again (null before the first).</summary>
    public RepeatableCapture? LastCapture => _state.Current.LastCapture;

    /// <summary>
    /// Takes the last capture again after its kind's delay, with no overlay:
    /// <list type="bullet">
    /// <item>an area (region, fixed size, shape, object): exactly the same pixels, live, with the same shape;</item>
    /// <item>a window, the active window or a scrolling capture: the same window, if it is still open;</item>
    /// <item>full screen, text and open menus: those captures again.</item>
    /// </list>
    /// </summary>
    public Task RepeatLastCaptureAsync(CancellationToken ct = default)
    {
        if (LastCapture is not { } last)
        {
            _notifier.Notify("Repeat Last Capture", "Nothing has been captured yet. Take a capture first; this repeats it.");
            return Task.CompletedTask;
        }

        LogRepeating(last.Mode, last.Bounds);
        return last.Mode switch
        {
            CaptureMode.FullScreen => CaptureFullScreenAsync(ct),
            CaptureMode.Menu => CaptureMenuAsync(ct),
            CaptureMode.Window or CaptureMode.ActiveWindow or CaptureMode.Scrolling => RepeatWindowAsync(last, ct),
            CaptureMode.Text => RepeatTextAsync(last, ct),
            _ => RepeatAreaAsync(last, ct),
        };
    }

    private Task RepeatAreaAsync(RepeatableCapture last, CancellationToken ct) => RunAsync(async current =>
    {
        using var hidden = await PrepareAsync(current, last.Mode, hideOwnUi: true, ct).ConfigureAwait(false);
        var displays = _monitors.GetMonitors();
        if (!OnScreen(last.Bounds, displays))
        {
            return;
        }

        using var screen = await _capture.CaptureRegionAsync(last.Bounds, ct).ConfigureAwait(false);
        using var image = Cut(screen, last.Bounds, last.Shape, displays, KindOf(last), last.WindowTitle);
        LogRegionCaptured(last.Bounds, image.Metadata.Kind);
        await DeliverAsync(image, [image], current, hidden, ct).ConfigureAwait(false);
    });

    private Task RepeatTextAsync(RepeatableCapture last, CancellationToken ct) => RunAsync(
        async current =>
        {
            using var hidden = await PrepareAsync(current, last.Mode, hideOwnUi: true, ct).ConfigureAwait(false);
            if (OnScreen(last.Bounds, _monitors.GetMonitors()))
            {
                using var image = await _capture.CaptureRegionAsync(last.Bounds, ct).ConfigureAwait(false);
                await RecognizeAndCopyAsync(image, current, ct).ConfigureAwait(false);
            }
        },
        producesImage: false);

    private Task RepeatWindowAsync(RepeatableCapture last, CancellationToken ct) => RunAsync(async current =>
    {
        var window = _windows.GetWindow((nint)last.Window);
        if (!last.IsSameWindow(window))
        {
            _notifier.Notify(
                "Repeat Last Capture",
                $"The window of the last capture ({last.WindowTitle}) isn't open any more. Capture another window first.",
                NotificationKind.Warning);
            return;
        }

        using var hidden = await PrepareAsync(current, last.Mode, hideOwnUi: last.Mode != CaptureMode.ActiveWindow, ct).ConfigureAwait(false);
        if (last.Mode == CaptureMode.Scrolling)
        {
            await ScrollAndDeliverAsync(window!, last.Point ?? Centre(window!.Bounds), current, hidden, ct).ConfigureAwait(false);
        }
        else
        {
            await CaptureWindowCoreAsync(window!.Handle, current, hidden, ct).ConfigureAwait(false);
        }
    });

    /// <summary>Whether any of <paramref name="bounds"/> is on a monitor now; tells the user when it isn't.</summary>
    private bool OnScreen(PixelRect bounds, IReadOnlyList<Core.Display.MonitorInfo> displays)
    {
        if (displays.Any(m => m.Bounds.IntersectsWith(bounds)))
        {
            return true;
        }

        _notifier.Notify(
            "Repeat Last Capture",
            "The area of the last capture isn't on any monitor now (the display layout changed). Take a new capture.",
            NotificationKind.Warning);
        return false;
    }

    /// <summary>Stores what to repeat. Failures are logged by the store; the capture goes on regardless.</summary>
    private void Remember(RepeatableCapture capture) => _ = _state.UpdateAsync(s => s with { LastCapture = capture });

    private static CaptureKind KindOf(RepeatableCapture capture) => capture.Mode switch
    {
        CaptureMode.Object => CaptureKind.Object,
        CaptureMode.Menu => CaptureKind.Menu,
        CaptureMode.Region when capture.WindowTitle is not null => CaptureKind.Window, // a window clicked on the overlay
        _ => CaptureKind.Region,
    };

    private static PixelPoint Centre(PixelRect r) => new(r.X + (r.Width / 2), r.Y + (r.Height / 2));

    [LoggerMessage(Level = LogLevel.Information, Message = "Repeating the last capture: {Mode} {Bounds}")]
    private partial void LogRepeating(CaptureMode mode, PixelRect bounds);
}
