namespace CashGear.Capture.Core.Capture;

public sealed record CaptureWindowOptions
{
    public bool IncludeShadow { get; init; }

    public bool IncludeCursor { get; init; }
}
