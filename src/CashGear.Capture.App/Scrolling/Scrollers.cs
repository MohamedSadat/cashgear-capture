using System.Runtime.InteropServices;
using System.Windows.Automation;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Interop.Native;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.App.Scrolling;

/// <summary>The bits of Windows the scrolling capture drives (a seam for tests).</summary>
public interface IScrollPlatform
{
    /// <summary>The mouse position in physical pixels.</summary>
    PixelPoint CursorPosition { get; set; }

    /// <summary>Moves the mouse to <paramref name="at"/> and turns the wheel; negative notches scroll down.</summary>
    void Wheel(PixelPoint at, int notches);

    bool IsEscapeDown();

    /// <summary>A window's client area on screen (physical pixels), or null if it can't be read.</summary>
    PixelRect? ClientArea(nint window);

    void BringToFront(nint window);
}

internal sealed unsafe class Win32ScrollPlatform : IScrollPlatform
{
    public PixelPoint CursorPosition
    {
        get
        {
            POINT p;
            return User32.GetCursorPos(&p) ? new PixelPoint(p.X, p.Y) : default;
        }

        set => User32.SetCursorPos(value.X, value.Y);
    }

    public void Wheel(PixelPoint at, int notches)
    {
        User32.SetCursorPos(at.X, at.Y);
        var input = INPUT.Wheel(notches * User32.WheelDelta);
        User32.SendInput(1, &input, sizeof(INPUT));
    }

    public bool IsEscapeDown() => (User32.GetAsyncKeyState(User32.VkEscape) & 0x8000) != 0;

    public PixelRect? ClientArea(nint window)
    {
        RECT client;
        var origin = new POINT(0, 0);
        if (!User32.GetClientRect(window, &client) || !User32.ClientToScreen(window, &origin))
        {
            return null;
        }

        return new PixelRect(origin.X, origin.Y, client.Width, client.Height);
    }

    public void BringToFront(nint window) => User32.SetForegroundWindow(window);
}

/// <summary>
/// Scrolls with the mouse wheel over the area (any app that scrolls with the wheel). The first step is one notch;
/// after that the number of notches is worked out from how far a notch actually scrolled, to move about 60% of
/// the view each time, which leaves plenty of overlap for lining the frames up.
/// </summary>
public sealed class WheelScroller(IScrollPlatform platform, PixelPoint at) : IScroller
{
    public const double StepFraction = 0.6;
    private const int MaxNotches = 60;

    private double _pixelsPerNotch;
    private int _lastNotches;

    public string Name => "the mouse wheel";

    public IScroller? Fallback => null;

    public PixelPoint At => at;

    public Task<bool> ScrollAsync(ScrollFeedback feedback, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // The largest distance per notch seen so far: a short last step (the bottom) mustn't shrink the next ones.
        if (feedback.LastOffset > 0 && _lastNotches > 0)
        {
            _pixelsPerNotch = Math.Max(_pixelsPerNotch, (double)feedback.LastOffset / _lastNotches);
        }

        var notches = _pixelsPerNotch > 0
            ? Math.Clamp((int)(StepFraction * feedback.ViewHeight / _pixelsPerNotch), 1, MaxNotches)
            : 1;
        platform.Wheel(at, -notches);
        _lastNotches = notches;
        return Task.FromResult(true);
    }
}

/// <summary>
/// Scrolls through UI Automation's ScrollPattern: exact, no mouse involved, and it knows when the bottom is reached.
/// If the app's UI Automation support fails, the mouse wheel takes over.
/// </summary>
public sealed partial class UiaScroller(ScrollPattern pattern, IScroller fallback, ILogger logger) : IScroller
{
    private bool _failed;

    public string Name => _failed ? fallback.Name : "UI Automation";

    public IScroller? Fallback => _failed ? null : fallback;

    public async Task<bool> ScrollAsync(ScrollFeedback feedback, CancellationToken ct)
    {
        if (_failed)
        {
            return await fallback.ScrollAsync(feedback, ct).ConfigureAwait(false);
        }

        try
        {
            // UI Automation calls block on the target app; keep them off the caller's thread.
            return await Task.Run(
                () =>
                {
                    var current = pattern.Current;
                    if (!current.VerticallyScrollable || current.VerticalScrollPercent is < 0 or >= 99.99)
                    {
                        return false;
                    }

                    var step = Math.Max(1, current.VerticalViewSize * WheelScroller.StepFraction);
                    pattern.SetScrollPercent(ScrollPattern.NoScroll, Math.Min(100, current.VerticalScrollPercent + step));
                    return true;
                },
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException or ArgumentException)
        {
            LogFailed(ex);
            _failed = true;
            return await fallback.ScrollAsync(feedback, ct).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "UI Automation scrolling failed; using the mouse wheel")]
    private partial void LogFailed(Exception ex);
}
