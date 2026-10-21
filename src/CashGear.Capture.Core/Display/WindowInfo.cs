using CashGear.Capture.Core.Geometry;

namespace CashGear.Capture.Core.Display;

/// <summary>A top-level window.</summary>
/// <param name="Handle">The <c>HWND</c>.</param>
/// <param name="Bounds">Visible frame bounds (DWM extended frame, no drop shadow) in physical pixels.</param>
/// <param name="ZOrder">0 = topmost of the enumerated windows.</param>
public sealed record WindowInfo(
    nint Handle,
    string Title,
    string ClassName,
    int ProcessId,
    PixelRect Bounds,
    bool IsMinimized,
    int ZOrder)
{
    /// <summary>Hidden by Windows (on another virtual desktop, a suspended store app): nothing of it can be captured.</summary>
    public bool IsCloaked { get; init; }

    /// <summary>
    /// The window asked Windows to keep it out of captures (display affinity): video players showing protected
    /// video, password managers, some banking apps. It comes out black, or not at all.
    /// </summary>
    public bool HidesFromCapture { get; init; }
}
