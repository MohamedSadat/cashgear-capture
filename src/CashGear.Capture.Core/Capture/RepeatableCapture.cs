using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Selection;

namespace CashGear.Capture.Core.Capture;

/// <summary>
/// What Repeat Last Capture takes again: the same kind of capture, of the same pixels (or the same window), with no
/// selection on the screen.
/// </summary>
/// <param name="Mode">How the capture was taken.</param>
/// <param name="Bounds">The virtual-screen rectangle captured (for a window: where it was).</param>
public sealed record RepeatableCapture(CaptureMode Mode, PixelRect Bounds)
{
    /// <summary>The outline, for a capture that isn't a plain rectangle.</summary>
    public CaptureShape? Shape { get; init; }

    /// <summary>The window (<c>HWND</c>) captured or picked, else 0.</summary>
    public long Window { get; init; }

    /// <summary>Its title then (for the file name).</summary>
    public string? WindowTitle { get; init; }

    /// <summary>Its class and process: a later window with the same handle but not these is another window.</summary>
    public string? WindowClass { get; init; }

    public int ProcessId { get; init; }

    /// <summary>Scrolling capture: the point in the window that scrolls.</summary>
    public PixelPoint? Point { get; init; }

    /// <summary>For a capture of <paramref name="window"/>.</summary>
    public static RepeatableCapture Of(CaptureMode mode, WindowInfo window) => new(mode, window.Bounds)
    {
        Window = window.Handle,
        WindowTitle = window.Title,
        WindowClass = window.ClassName,
        ProcessId = window.ProcessId,
    };

    /// <summary>Whether <paramref name="window"/> (looked up by <see cref="Window"/>) is still the window that was captured.</summary>
    public bool IsSameWindow(WindowInfo? window) =>
        window is not null && window.Handle == Window && window.ProcessId == ProcessId && window.ClassName == WindowClass;
}
