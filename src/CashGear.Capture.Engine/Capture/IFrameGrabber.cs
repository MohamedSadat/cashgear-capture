using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Imaging;

namespace CashGear.Capture.Engine.Capture;

/// <param name="Monitor">The monitor the area lies on.</param>
/// <param name="Area">Virtual-screen rectangle to capture; must lie within <c>Monitor.Bounds</c>.</param>
/// <param name="FrameTimeout">How long a frame-based backend may wait for a frame.</param>
internal readonly record struct FrameRequest(MonitorInfo Monitor, PixelRect Area, bool IncludeCursor, TimeSpan FrameTimeout);

/// <summary>One capture backend. Captures (part of) a single monitor.</summary>
internal interface IFrameGrabber
{
    CaptureBackend Backend { get; }

    /// <summary>Returns an image of exactly <c>request.Area</c>'s size with <see cref="CaptureKind.Monitor"/> metadata.</summary>
    /// <exception cref="TimeoutException">No frame arrived within <see cref="FrameRequest.FrameTimeout"/>.</exception>
    Task<CapturedImage> CaptureAsync(FrameRequest request, CancellationToken ct);
}

internal static class FrameRequestExtensions
{
    public static CaptureMetadata ToMetadata(this FrameRequest request, CaptureBackend backend, TimeProvider time) => new()
    {
        Kind = CaptureKind.Monitor,
        SourceBounds = request.Area,
        Dpi = request.Monitor.Scale,
        CapturedAt = time.GetLocalNow(),
        Backend = backend,
        MonitorName = request.Monitor.ShortName,
        IsPrimaryMonitor = request.Monitor.IsPrimary,
    };
}
