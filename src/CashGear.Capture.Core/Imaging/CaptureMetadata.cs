using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Geometry;

namespace CashGear.Capture.Core.Imaging;

/// <summary>Where a captured image came from.</summary>
public sealed record CaptureMetadata
{
    public required CaptureKind Kind { get; init; }

    /// <summary>The virtual-screen rectangle the image covers.</summary>
    public required PixelRect SourceBounds { get; init; }

    public DpiScale Dpi { get; init; } = DpiScale.Identity;

    public DateTimeOffset CapturedAt { get; init; }

    /// <summary>The backend that actually produced the pixels (never <see cref="CaptureBackend.Auto"/> once set).</summary>
    public CaptureBackend Backend { get; init; }

    /// <summary>Short monitor name (e.g. <c>DISPLAY1</c>) for single-monitor captures.</summary>
    public string? MonitorName { get; init; }

    public bool IsPrimaryMonitor { get; init; }

    public string? WindowTitle { get; init; }

    /// <summary>True when some pixels are deliberately transparent (gaps between monitors).</summary>
    public bool HasTransparency { get; init; }
}
