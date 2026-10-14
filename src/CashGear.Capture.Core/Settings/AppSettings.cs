using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Imaging;

namespace CashGear.Capture.Core.Settings;

// Leaf sections are positional records on purpose: the System.Text.Json source generator resets init-only
// properties that are absent from the JSON to default(T), discarding initializers, but it does honour
// constructor parameter defaults. Sections themselves may come back null and are fixed up by Normalize().

/// <summary>User settings, persisted as JSON. Immutable; change via <see cref="ISettingsService.UpdateAsync"/>.</summary>
public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public CaptureSettings Capture { get; init; } = new();

    public OutputSettings Output { get; init; } = new();

    /// <summary>Replaces missing sections with defaults and clamps out-of-range values.</summary>
    public AppSettings Normalize() => this with
    {
        SchemaVersion = CurrentSchemaVersion,
        Capture = (Capture ?? new CaptureSettings()).Normalize(),
        Output = (Output ?? new OutputSettings()).Normalize(),
    };
}

/// <summary>What "Capture Full Screen" writes.</summary>
public enum FullScreenOutput
{
    /// <summary>One image of the whole virtual screen.</summary>
    AllMonitors,

    /// <summary>One image per monitor.</summary>
    EachMonitor,

    /// <summary>Both of the above.</summary>
    AllAndEachMonitor,
}

/// <param name="Backend">Which capture API to use.</param>
/// <param name="IncludeCursor">Draw the mouse cursor into captures.</param>
/// <param name="HideDelayMs">How long to wait after hiding the app's own windows before capturing.</param>
/// <param name="WgcFrameTimeoutMs">How long Windows.Graphics.Capture may take to deliver a frame before falling back to GDI.</param>
/// <param name="FullScreenOutput">What "Capture Full Screen" writes.</param>
public sealed record CaptureSettings(
    CaptureBackend Backend = CaptureBackend.Auto,
    bool IncludeCursor = false,
    int HideDelayMs = CaptureSettings.DefaultHideDelayMs,
    int WgcFrameTimeoutMs = CaptureSettings.DefaultWgcFrameTimeoutMs,
    FullScreenOutput FullScreenOutput = FullScreenOutput.AllAndEachMonitor)
{
    public const int DefaultHideDelayMs = 200;
    public const int DefaultWgcFrameTimeoutMs = 2_000;
    public const int MaxHideDelayMs = 5_000;
    public const int MaxFrameTimeoutMs = 30_000;

    public CaptureSettings Normalize() => this with
    {
        Backend = Enum.IsDefined(Backend) ? Backend : CaptureBackend.Auto,
        HideDelayMs = Math.Clamp(HideDelayMs, 0, MaxHideDelayMs),
        WgcFrameTimeoutMs = Math.Clamp(WgcFrameTimeoutMs, 1, MaxFrameTimeoutMs),
        FullScreenOutput = Enum.IsDefined(FullScreenOutput) ? FullScreenOutput : FullScreenOutput.AllAndEachMonitor,
    };
}

/// <param name="Folder">Folder for saved captures; null means <see cref="AppPaths.DefaultCaptureDirectory"/>. Environment variables are expanded.</param>
/// <param name="Format">File format for saved captures.</param>
/// <param name="JpegQuality">1–100.</param>
/// <param name="CopyToClipboard">Also put the capture on the clipboard.</param>
public sealed record OutputSettings(
    string? Folder = null,
    ImageFileFormat Format = ImageFileFormat.Png,
    int JpegQuality = OutputSettings.DefaultJpegQuality,
    bool CopyToClipboard = true)
{
    public const int DefaultJpegQuality = 90;

    public OutputSettings Normalize() => this with
    {
        Folder = string.IsNullOrWhiteSpace(Folder) ? null : Folder.Trim(),
        Format = Enum.IsDefined(Format) ? Format : ImageFileFormat.Png,
        JpegQuality = Math.Clamp(JpegQuality, 1, 100),
    };
}
