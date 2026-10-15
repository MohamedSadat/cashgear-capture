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

/// <summary>What a capture targeted.</summary>
public enum CaptureKind
{
    Monitor,
    AllMonitors,
    Window,
    Region,

    /// <summary>A scrolling area stitched from several frames (taller than the screen).</summary>
    Scrolling,
}
