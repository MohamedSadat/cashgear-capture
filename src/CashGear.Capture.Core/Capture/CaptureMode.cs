using CashGear.Capture.Core.Resources;

namespace CashGear.Capture.Core.Capture;

/// <summary>How a capture is taken (what the user starts), as opposed to <see cref="CaptureKind"/>, what it shows.</summary>
public enum CaptureMode
{
    /// <summary>Drag a rectangle or click a window on the frozen screen.</summary>
    Region,

    /// <summary>Pick a window, then capture its own content.</summary>
    Window,

    /// <summary>The window in front when the hotkey was pressed.</summary>
    ActiveWindow,

    FullScreen,

    Scrolling,

    /// <summary>Capture Text (OCR).</summary>
    Text,

    /// <summary>A frame of a set size.</summary>
    FixedRegion,

    /// <summary>An outline drawn by hand.</summary>
    Freehand,

    Polygon,

    /// <summary>A rectangle with rounded corners.</summary>
    RoundedRegion,

    /// <summary>A part of a window under the pointer (UI Automation).</summary>
    Object,

    /// <summary>The menus open when the delay ends.</summary>
    Menu,
}

public static class CaptureModes
{
    /// <summary>The name shown to the user (countdown, notifications, settings).</summary>
    public static string Label(this CaptureMode mode) => mode switch
    {
        CaptureMode.Region => CoreStrings.ModeRegion,
        CaptureMode.Window => CoreStrings.ModeWindow,
        CaptureMode.ActiveWindow => CoreStrings.ModeActiveWindow,
        CaptureMode.FullScreen => CoreStrings.ModeFullScreen,
        CaptureMode.Scrolling => CoreStrings.ModeScrolling,
        CaptureMode.Text => CoreStrings.ModeText,
        CaptureMode.FixedRegion => CoreStrings.ModeFixedRegion,
        CaptureMode.Freehand => CoreStrings.ModeFreehand,
        CaptureMode.Polygon => CoreStrings.ModePolygon,
        CaptureMode.RoundedRegion => CoreStrings.ModeRoundedRegion,
        CaptureMode.Object => CoreStrings.ModeObject,
        CaptureMode.Menu => CoreStrings.ModeMenu,
        _ => mode.ToString(),
    };
}
