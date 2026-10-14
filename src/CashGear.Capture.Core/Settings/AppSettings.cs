using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Hotkeys;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Output;

namespace CashGear.Capture.Core.Settings;

// Leaf sections are positional records on purpose: the System.Text.Json source generator resets init-only
// properties that are absent from the JSON to default(T), discarding initializers, but it does honour
// constructor parameter defaults. Sections themselves may come back null and are fixed up by Normalize().

/// <summary>User settings, persisted as JSON. Immutable; change via <see cref="ISettingsService.UpdateAsync"/>.</summary>
public sealed record AppSettings
{
    /// <summary>2: <c>output.copyToClipboard</c> moved to <c>afterCapture</c> (see <see cref="SettingsMigration"/>).</summary>
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public CaptureSettings Capture { get; init; } = new();

    public OutputSettings Output { get; init; } = new();

    public AfterCaptureSettings AfterCapture { get; init; } = new();

    public HotkeySettings Hotkeys { get; init; } = new();

    public AppearanceSettings Appearance { get; init; } = new();

    /// <summary>Replaces missing sections with defaults and clamps out-of-range values.</summary>
    public AppSettings Normalize() => this with
    {
        SchemaVersion = CurrentSchemaVersion,
        Capture = (Capture ?? new CaptureSettings()).Normalize(),
        Output = (Output ?? new OutputSettings()).Normalize(),
        AfterCapture = AfterCapture ?? new AfterCaptureSettings(),
        Hotkeys = (Hotkeys ?? new HotkeySettings()).Normalize(),
        Appearance = (Appearance ?? new AppearanceSettings()).Normalize(),
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
/// <param name="IncludeWindowShadow">Window captures get a soft drop shadow on a transparent margin.</param>
/// <param name="DelaySeconds">Wait this long before every capture (time to open a menu or hover a tooltip).</param>
public sealed record CaptureSettings(
    CaptureBackend Backend = CaptureBackend.Auto,
    bool IncludeCursor = false,
    int HideDelayMs = CaptureSettings.DefaultHideDelayMs,
    int WgcFrameTimeoutMs = CaptureSettings.DefaultWgcFrameTimeoutMs,
    FullScreenOutput FullScreenOutput = FullScreenOutput.AllAndEachMonitor,
    bool IncludeWindowShadow = false,
    int DelaySeconds = 0)
{
    public const int DefaultHideDelayMs = 200;
    public const int DefaultWgcFrameTimeoutMs = 2_000;
    public const int MaxHideDelayMs = 5_000;
    public const int MaxFrameTimeoutMs = 30_000;
    public const int MaxDelaySeconds = 60;

    public CaptureSettings Normalize() => this with
    {
        Backend = Enum.IsDefined(Backend) ? Backend : CaptureBackend.Auto,
        HideDelayMs = Math.Clamp(HideDelayMs, 0, MaxHideDelayMs),
        WgcFrameTimeoutMs = Math.Clamp(WgcFrameTimeoutMs, 1, MaxFrameTimeoutMs),
        FullScreenOutput = Enum.IsDefined(FullScreenOutput) ? FullScreenOutput : FullScreenOutput.AllAndEachMonitor,
        DelaySeconds = Math.Clamp(DelaySeconds, 0, MaxDelaySeconds),
    };
}

/// <param name="Folder">Folder for saved captures; null means <see cref="AppPaths.DefaultCaptureDirectory"/>. Environment variables are expanded.</param>
/// <param name="Format">File format for saved captures.</param>
/// <param name="JpegQuality">1–100.</param>
/// <param name="FileNameTemplate">File name without extension; see <see cref="Output.FileNameTemplate"/> for the tokens.</param>
public sealed record OutputSettings(
    string? Folder = null,
    ImageFileFormat Format = ImageFileFormat.Png,
    int JpegQuality = OutputSettings.DefaultJpegQuality,
    string FileNameTemplate = Output.FileNameTemplate.Default)
{
    public const int DefaultJpegQuality = 90;

    public OutputSettings Normalize() => this with
    {
        Folder = string.IsNullOrWhiteSpace(Folder) ? null : Folder.Trim(),
        Format = Enum.IsDefined(Format) ? Format : ImageFileFormat.Png,
        JpegQuality = Math.Clamp(JpegQuality, 1, 100),
        FileNameTemplate = string.IsNullOrWhiteSpace(FileNameTemplate) ? Output.FileNameTemplate.Default : FileNameTemplate.Trim(),
    };
}

/// <summary>What happens to a capture. Any combination; with none selected the capture is discarded (and the user told).</summary>
/// <param name="Save">Write it to the capture folder ("auto-save").</param>
/// <param name="CopyToClipboard">Put it on the clipboard.</param>
/// <param name="OpenEditor">Open it in the editor (Phase 5; ignored until the editor exists).</param>
public sealed record AfterCaptureSettings(bool Save = true, bool CopyToClipboard = true, bool OpenEditor = false)
{
    public bool DoesAnything => Save || CopyToClipboard || OpenEditor;
}

public enum AppTheme
{
    /// <summary>Follow the Windows app theme (light or dark).</summary>
    System,
    Light,
    Dark,
}

public sealed record AppearanceSettings(AppTheme Theme = AppTheme.System)
{
    public AppearanceSettings Normalize() => this with { Theme = Enum.IsDefined(Theme) ? Theme : AppTheme.System };
}

/// <summary>
/// Global hotkey per capture, as text (see <see cref="HotkeyGesture.TryParse"/>), e.g. <c>Ctrl+Shift+PrintScreen</c>.
/// A missing entry gets the default; <c>null</c> or <c>""</c> means no hotkey.
/// </summary>
public sealed record HotkeySettings(
    string? CaptureRegion = HotkeySettings.DefaultCaptureRegion,
    string? CaptureActiveWindow = HotkeySettings.DefaultCaptureActiveWindow,
    string? CaptureFullScreen = HotkeySettings.DefaultCaptureFullScreen,
    string? CaptureScrolling = HotkeySettings.DefaultCaptureScrolling)
{
    public const string DefaultCaptureRegion = "PrintScreen";
    public const string DefaultCaptureActiveWindow = "Alt+PrintScreen";
    public const string DefaultCaptureFullScreen = "Ctrl+PrintScreen";
    public const string DefaultCaptureScrolling = "Ctrl+Shift+PrintScreen";

    /// <summary>The configured text for an action, or null when it has no hotkey.</summary>
    public string? For(HotkeyAction action) => action switch
    {
        HotkeyAction.CaptureRegion => CaptureRegion,
        HotkeyAction.CaptureActiveWindow => CaptureActiveWindow,
        HotkeyAction.CaptureFullScreen => CaptureFullScreen,
        HotkeyAction.CaptureScrolling => CaptureScrolling,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };

    /// <summary>A copy with one action's hotkey replaced.</summary>
    public HotkeySettings With(HotkeyAction action, string? text) => action switch
    {
        HotkeyAction.CaptureRegion => this with { CaptureRegion = text },
        HotkeyAction.CaptureActiveWindow => this with { CaptureActiveWindow = text },
        HotkeyAction.CaptureFullScreen => this with { CaptureFullScreen = text },
        HotkeyAction.CaptureScrolling => this with { CaptureScrolling = text },
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };

    public HotkeySettings Normalize() => new(
        Clean(CaptureRegion), Clean(CaptureActiveWindow), Clean(CaptureFullScreen), Clean(CaptureScrolling));

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
