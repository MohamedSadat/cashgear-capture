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
    int ZOrder);
