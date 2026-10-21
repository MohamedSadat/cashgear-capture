namespace CashGear.Capture.Core.Capture;

/// <summary>Which capture API to use. <see cref="Auto"/> is only meaningful as a setting.</summary>
public enum CaptureBackend
{
    /// <summary>Windows.Graphics.Capture where it can run without the yellow border, GDI otherwise.</summary>
    Auto,

    /// <summary>Windows.Graphics.Capture (falls back to GDI on failure).</summary>
    Wgc,

    /// <summary>GDI <c>BitBlt</c>.</summary>
    Gdi,
}

/// <summary>How captures read a monitor in HDR mode.</summary>
public enum HdrCapture
{
    /// <summary>As 16-bit float, tone-mapped to SDR, so captures look as the screen does (WGC, also on Windows 10).</summary>
    Auto,

    /// <summary>As 8-bit, the way Windows hands it to older apps (HDR monitors' captures look washed out).</summary>
    Off,
}

/// <summary>What a capture targeted.</summary>
public enum CaptureKind
{
    Monitor,
    AllMonitors,
    Window,
    Region,

    /// <summary>A scrolling area stitched from several frames (taller than the screen).</summary>
    Scrolling,

    /// <summary>A screen recording (an MP4 file).</summary>
    Recording,

    /// <summary>A part of a window (a control, a panel) picked with UI Automation.</summary>
    Object,

    /// <summary>Open menus (and their submenus).</summary>
    Menu,
}
