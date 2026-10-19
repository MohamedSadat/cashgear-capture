using System.Text.Json.Serialization;
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
    /// <summary>
    /// 2: <c>output.copyToClipboard</c> moved to <c>afterCapture</c>. 3: <c>recording.quality</c> is a preset instead of a
    /// number, and <c>recording.encoder</c> an encoder id. 4: <c>recording.showClicks</c> moved to
    /// <c>overlays.clicks.enabled</c> (see <see cref="SettingsMigration"/>).
    /// </summary>
    public const int CurrentSchemaVersion = 4;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public CaptureSettings Capture { get; init; } = new();

    public OutputSettings Output { get; init; } = new();

    public AfterCaptureSettings AfterCapture { get; init; } = new();

    public HotkeySettings Hotkeys { get; init; } = new();

    public AppearanceSettings Appearance { get; init; } = new();

    public RecordingSettings Recording { get; init; } = new();

    /// <summary>Clicks, pointer, keystrokes, webcam and the on-screen pen in recordings.</summary>
    public OverlaySettings Overlays { get; init; } = new();

    public OcrSettings Ocr { get; init; } = new();

    public HistorySettings History { get; init; } = new();

    public UpdateSettings Updates { get; init; } = new();

    /// <summary>The capture toolbar and the screen tools (magnifier, colour picker, ruler).</summary>
    public ToolsSettings Tools { get; init; } = new();

    /// <summary>Replaces missing sections with defaults and clamps out-of-range values.</summary>
    public AppSettings Normalize() => this with
    {
        SchemaVersion = CurrentSchemaVersion,
        Capture = (Capture ?? new CaptureSettings()).Normalize(),
        Output = (Output ?? new OutputSettings()).Normalize(),
        AfterCapture = AfterCapture ?? new AfterCaptureSettings(),
        Hotkeys = (Hotkeys ?? new HotkeySettings()).Normalize(),
        Appearance = (Appearance ?? new AppearanceSettings()).Normalize(),
        Recording = (Recording ?? new RecordingSettings()).Normalize(),
        Overlays = (Overlays ?? new OverlaySettings()).Normalize(),
        Ocr = (Ocr ?? new OcrSettings()).Normalize(),
        History = (History ?? new HistorySettings()).Normalize(),
        Updates = (Updates ?? new UpdateSettings()).Normalize(),
        Tools = (Tools ?? new ToolsSettings()).Normalize(),
    };
}

/// <summary>The capture toolbar and the screen tools.</summary>
/// <param name="ShowToolbarAtStartup">Show the floating capture toolbar when the app starts.</param>
public sealed record ToolsSettings(bool ShowToolbarAtStartup = false)
{
    public ToolsSettings Normalize() => this;
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
/// <param name="ScrollSettleMs">Scrolling capture: how long to let the page settle after each scroll before capturing.</param>
/// <param name="ScrollMaxHeight">Scrolling capture: stop once the stitched image is this tall (pixels).</param>
/// <param name="FixedRegionWidth">Fixed-size region: the width last used (pixels).</param>
/// <param name="FixedRegionHeight">Fixed-size region: the height last used (pixels).</param>
/// <param name="CornerRadius">Rounded region: the corner radius last used (pixels).</param>
/// <param name="Delays">A delay per capture mode that overrides <paramref name="DelaySeconds"/>.</param>
public sealed record CaptureSettings(
    CaptureBackend Backend = CaptureBackend.Auto,
    bool IncludeCursor = false,
    int HideDelayMs = CaptureSettings.DefaultHideDelayMs,
    int WgcFrameTimeoutMs = CaptureSettings.DefaultWgcFrameTimeoutMs,
    FullScreenOutput FullScreenOutput = FullScreenOutput.AllAndEachMonitor,
    bool IncludeWindowShadow = false,
    int DelaySeconds = 0,
    int ScrollSettleMs = CaptureSettings.DefaultScrollSettleMs,
    int ScrollMaxHeight = CaptureSettings.DefaultScrollMaxHeight,
    int FixedRegionWidth = 800,
    int FixedRegionHeight = 600,
    int CornerRadius = CaptureSettings.DefaultCornerRadius,
    ModeDelays? Delays = null)
{
    /// <summary>Never null when made by the constructor (or read from a file), so settings compare by value.</summary>
    public ModeDelays? Delays { get; init; } = Delays ?? new ModeDelays();

    public const int DefaultCornerRadius = 16;
    public const int MaxFixedRegionSide = 20_000;
    public const int MaxCornerRadius = Selection.RegionSelectionModel.MaxCornerRadius;
    public const int DefaultHideDelayMs = 200;
    public const int DefaultScrollSettleMs = 250;
    public const int DefaultScrollMaxHeight = 20_000;
    public const int MaxScrollSettleMs = 5_000;
    public const int MinScrollMaxHeight = 2_000;
    public const int MaxScrollMaxHeight = 60_000;
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
        ScrollSettleMs = Math.Clamp(ScrollSettleMs, 0, MaxScrollSettleMs),
        ScrollMaxHeight = Math.Clamp(ScrollMaxHeight, MinScrollMaxHeight, MaxScrollMaxHeight),
        FixedRegionWidth = Math.Clamp(FixedRegionWidth, 1, MaxFixedRegionSide),
        FixedRegionHeight = Math.Clamp(FixedRegionHeight, 1, MaxFixedRegionSide),
        CornerRadius = Math.Clamp(CornerRadius, 0, MaxCornerRadius),
        Delays = (Delays ?? new ModeDelays()).Normalize(),
    };

    [JsonIgnore]
    public Geometry.PixelSize FixedRegionSize => new(FixedRegionWidth, FixedRegionHeight);

    /// <summary>The delay before a capture of this kind: its own, or else the general <see cref="DelaySeconds"/>.</summary>
    public int DelayFor(CaptureMode mode) => Delays?.For(mode) ?? DelaySeconds;
}

/// <summary>
/// A delay (seconds) for each kind of capture; null uses the general delay (<see cref="CaptureSettings.DelaySeconds"/>).
/// Capturing an open menu waits <see cref="DefaultMenuDelay"/> by default: time to open the menu.
/// </summary>
public sealed record ModeDelays(
    int? Region = null,
    int? Window = null,
    int? ActiveWindow = null,
    int? FullScreen = null,
    int? Scrolling = null,
    int? Text = null,
    int? FixedRegion = null,
    int? Freehand = null,
    int? Polygon = null,
    int? RoundedRegion = null,
    int? Object = null,
    int? Menu = ModeDelays.DefaultMenuDelay)
{
    public const int DefaultMenuDelay = 3;

    public int? For(CaptureMode mode) => mode switch
    {
        CaptureMode.Region => Region,
        CaptureMode.Window => Window,
        CaptureMode.ActiveWindow => ActiveWindow,
        CaptureMode.FullScreen => FullScreen,
        CaptureMode.Scrolling => Scrolling,
        CaptureMode.Text => Text,
        CaptureMode.FixedRegion => FixedRegion,
        CaptureMode.Freehand => Freehand,
        CaptureMode.Polygon => Polygon,
        CaptureMode.RoundedRegion => RoundedRegion,
        CaptureMode.Object => Object,
        CaptureMode.Menu => Menu,
        _ => null,
    };

    /// <summary>A copy with one mode's delay replaced (null: the general delay).</summary>
    public ModeDelays With(CaptureMode mode, int? seconds) => mode switch
    {
        CaptureMode.Region => this with { Region = seconds },
        CaptureMode.Window => this with { Window = seconds },
        CaptureMode.ActiveWindow => this with { ActiveWindow = seconds },
        CaptureMode.FullScreen => this with { FullScreen = seconds },
        CaptureMode.Scrolling => this with { Scrolling = seconds },
        CaptureMode.Text => this with { Text = seconds },
        CaptureMode.FixedRegion => this with { FixedRegion = seconds },
        CaptureMode.Freehand => this with { Freehand = seconds },
        CaptureMode.Polygon => this with { Polygon = seconds },
        CaptureMode.RoundedRegion => this with { RoundedRegion = seconds },
        CaptureMode.Object => this with { Object = seconds },
        CaptureMode.Menu => this with { Menu = seconds },
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    public ModeDelays Normalize() => Enum.GetValues<CaptureMode>().Aggregate(this, (d, mode) => d.For(mode) is { } s ? d.With(mode, Math.Clamp(s, 0, CaptureSettings.MaxDelaySeconds)) : d);
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
/// <param name="OpenEditor">Open it in the editor (the spec's default action).</param>
public sealed record AfterCaptureSettings(bool Save = false, bool CopyToClipboard = false, bool OpenEditor = true)
{
    [JsonIgnore]
    public bool DoesAnything => Save || CopyToClipboard || OpenEditor;
}

public enum AppTheme
{
    /// <summary>Follow the Windows app theme (light or dark).</summary>
    System,
    Light,
    Dark,
}

/// <summary>Screen recording.</summary>
/// <param name="Fps">Frames per second: 15, 30 or 60.</param>
/// <param name="Quality">Small files to near lossless; Balanced is a good balance.</param>
/// <param name="IncludeCursor">Show the mouse pointer.</param>
/// <param name="Encoder"><c>auto</c> (the best that works here), <c>software</c>, or an encoder's id (see <see cref="Recording.EncoderPresets"/>).</param>
/// <param name="SystemAudio">Record what the PC plays.</param>
/// <param name="SystemAudioDevice">The output device whose sound is recorded: a device ID, or null for Windows' default output device (followed when it changes).</param>
/// <param name="SystemAudioScope">All of system audio, or only (or all but) one app's sound.</param>
/// <param name="SystemAudioApp">The app for <paramref name="SystemAudioScope"/>, by program file name (<c>spotify.exe</c>).</param>
/// <param name="SystemAudioAppName">The app's name as shown in Settings and messages.</param>
/// <param name="MicrophoneProcessing">Noise suppression, noise gate, compressor and limiter for the microphone.</param>
/// <param name="NormalizeLoudness">Even out each sound track to −16 LUFS when a recording is saved (the sound is encoded again; the video isn't).</param>
/// <param name="PushToTalk">A key (as hotkey text, e.g. <c>F13</c> or <c>Ctrl+Space</c>) that must be held for the microphone to be heard while recording; null for none.</param>
/// <param name="Microphone">The microphone to record: null for none, <see cref="DefaultMicrophone"/> for the Windows default, or a device ID.</param>
/// <param name="Tracks">How system audio and the microphone become tracks when both are recorded.</param>
/// <param name="SystemAudioGain">System audio's volume in recordings, 1 as captured (0 to 4; set on the recording toolbar).</param>
/// <param name="MicrophoneGain">The microphone's volume in recordings, 1 as captured (0 to 4; set on the recording toolbar).</param>
public sealed record RecordingSettings(
    int Fps = 30,
    Recording.QualityPreset Quality = Recording.QualityPreset.Balanced,
    bool IncludeCursor = true,
    string Encoder = Recording.EncoderPresets.Auto,
    bool SystemAudio = true,
    string? Microphone = null,
    Recording.AudioTrackLayout Tracks = Recording.AudioTrackLayout.Mixed,
    float SystemAudioGain = 1,
    float MicrophoneGain = 1,
    string? SystemAudioDevice = null,
    Recording.SystemAudioScope SystemAudioScope = Recording.SystemAudioScope.All,
    string? SystemAudioApp = null,
    string? SystemAudioAppName = null,
    MicrophoneProcessing? MicrophoneProcessing = null,
    bool NormalizeLoudness = false,
    string? PushToTalk = null)
{
    public const float MaxGain = 4;

    /// <summary>Never null when made by the constructor (or read from a file), so settings compare by value.</summary>
    public MicrophoneProcessing? MicrophoneProcessing { get; init; } = MicrophoneProcessing ?? new MicrophoneProcessing();

    /// <summary><see cref="Microphone"/> value for "whichever microphone Windows uses by default".</summary>
    public const string DefaultMicrophone = "default";

    public static IReadOnlyList<int> FrameRates { get; } = [15, 30, 60];

    public RecordingSettings Normalize() => this with
    {
        Fps = FrameRates.MinBy(f => Math.Abs(f - Fps)),
        Quality = Enum.IsDefined(Quality) ? Quality : Recording.QualityPreset.Balanced,
        Encoder = string.IsNullOrWhiteSpace(Encoder) ? Recording.EncoderPresets.Auto : Encoder.Trim().ToLowerInvariant(),
        Microphone = string.IsNullOrWhiteSpace(Microphone) ? null : Microphone.Trim(),
        SystemAudioDevice = string.IsNullOrWhiteSpace(SystemAudioDevice) ? null : SystemAudioDevice.Trim(),
        PushToTalk = string.IsNullOrWhiteSpace(PushToTalk) ? null : PushToTalk.Trim(),
        MicrophoneProcessing = (MicrophoneProcessing ?? new MicrophoneProcessing()).Normalize(),
        SystemAudioApp = string.IsNullOrWhiteSpace(SystemAudioApp) ? null : SystemAudioApp.Trim().ToLowerInvariant(),
        SystemAudioAppName = string.IsNullOrWhiteSpace(SystemAudioAppName) ? null : SystemAudioAppName.Trim(),
        // Without an app there is nothing to pick out: all of system audio.
        SystemAudioScope = Enum.IsDefined(SystemAudioScope) && !string.IsNullOrWhiteSpace(SystemAudioApp) ? SystemAudioScope : Recording.SystemAudioScope.All,
        Tracks = Enum.IsDefined(Tracks) ? Tracks : Recording.AudioTrackLayout.Mixed,
        SystemAudioGain = Gain(SystemAudioGain),
        MicrophoneGain = Gain(MicrophoneGain),
    };

    private static float Gain(float value) => float.IsFinite(value) ? Math.Clamp(value, 0, MaxGain) : 1;
}

/// <summary>
/// What is done to the microphone's sound before it is recorded, in this order: noise suppression (RNNoise), noise
/// gate, compressor, limiter. Only the limiter is on by default: below −1 dBFS it changes nothing.
/// </summary>
/// <param name="NoiseSuppression">Remove steady background noise (fans, hum, keyboard) with RNNoise.</param>
/// <param name="NoiseSuppressionStrength">How much of it (percent, 0 to 100): 100 removes all it can; less leaves some of the original in.</param>
/// <param name="NoiseGate">Silence the microphone between words.</param>
/// <param name="GateThresholdDb">The level (dBFS) above which the gate lets the sound through (−80 to −10).</param>
/// <param name="Compressor">Even out loud and quiet speech.</param>
/// <param name="Limiter">Never let the microphone go above −1 dBFS (no clipping).</param>
public sealed record MicrophoneProcessing(
    bool NoiseSuppression = false,
    int NoiseSuppressionStrength = 100,
    bool NoiseGate = false,
    int GateThresholdDb = MicrophoneProcessing.DefaultGateThresholdDb,
    bool Compressor = false,
    bool Limiter = true)
{
    public const int DefaultGateThresholdDb = -45;
    public const int MinGateThresholdDb = -80;
    public const int MaxGateThresholdDb = -10;

    /// <summary>Whether anything changes the sound at all.</summary>
    [JsonIgnore]
    public bool DoesAnything => NoiseSuppression || NoiseGate || Compressor || Limiter;

    public MicrophoneProcessing Normalize() => this with
    {
        NoiseSuppressionStrength = Math.Clamp(NoiseSuppressionStrength, 0, 100),
        GateThresholdDb = Math.Clamp(GateThresholdDb, MinGateThresholdDb, MaxGateThresholdDb),
    };
}

/// <summary>Capture Text (OCR).</summary>
/// <param name="Language">The language to recognize (a Windows OCR language tag such as <c>ar-SA</c>), or null to choose automatically.</param>
public sealed record OcrSettings(string? Language = null)
{
    public OcrSettings Normalize() => this with { Language = string.IsNullOrWhiteSpace(Language) ? null : Language.Trim() };
}

/// <summary>Capture history (tray › Recent Captures and the History window).</summary>
/// <param name="MaxItems">How many recent captures to keep; 0 keeps none (history off).</param>
public sealed record HistorySettings(int MaxItems = HistorySettings.DefaultMaxItems)
{
    public const int DefaultMaxItems = 20;
    public const int Limit = 100;

    public HistorySettings Normalize() => this with { MaxItems = Math.Clamp(MaxItems, 0, Limit) };
}

/// <summary>Updates of the installed app.</summary>
/// <param name="CheckAutomatically">Check (and download) in the background, once a day.</param>
/// <param name="FeedUrl">Where to look: a URL or folder with a release feed. Null uses the address built into the app.</param>
public sealed record UpdateSettings(bool CheckAutomatically = true, string? FeedUrl = null)
{
    public UpdateSettings Normalize() => this with { FeedUrl = string.IsNullOrWhiteSpace(FeedUrl) ? null : FeedUrl.Trim() };
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
    string? CaptureScrolling = HotkeySettings.DefaultCaptureScrolling,
    string? Record = HotkeySettings.DefaultRecord,
    string? CaptureText = HotkeySettings.DefaultCaptureText,
    string? ToggleDrawing = HotkeySettings.DefaultToggleDrawing,
    string? RepeatLastCapture = HotkeySettings.DefaultRepeatLastCapture,
    string? CaptureWindow = null,
    string? CaptureFixedRegion = null,
    string? CaptureFreehand = null,
    string? CapturePolygon = null,
    string? CaptureRoundedRegion = null,
    string? CaptureObject = null,
    string? CaptureMenu = null,
    string? ToggleToolbar = null)
{
    public const string DefaultRepeatLastCapture = "Ctrl+Alt+Shift+PrintScreen";

    public const string DefaultCaptureRegion = "PrintScreen";
    public const string DefaultCaptureActiveWindow = "Alt+PrintScreen";
    public const string DefaultCaptureFullScreen = "Ctrl+PrintScreen";
    public const string DefaultCaptureScrolling = "Ctrl+Shift+PrintScreen";
    public const string DefaultRecord = "Shift+PrintScreen";
    public const string DefaultCaptureText = "Ctrl+Alt+PrintScreen";

    /// <summary>Registered only while a recording runs, so the key isn't taken from other apps otherwise.</summary>
    public const string DefaultToggleDrawing = "Ctrl+Shift+D";

    /// <summary>The configured text for an action, or null when it has no hotkey.</summary>
    public string? For(HotkeyAction action) => action switch
    {
        HotkeyAction.CaptureRegion => CaptureRegion,
        HotkeyAction.CaptureActiveWindow => CaptureActiveWindow,
        HotkeyAction.CaptureFullScreen => CaptureFullScreen,
        HotkeyAction.CaptureScrolling => CaptureScrolling,
        HotkeyAction.Record => Record,
        HotkeyAction.CaptureText => CaptureText,
        HotkeyAction.ToggleDrawing => ToggleDrawing,
        HotkeyAction.RepeatLastCapture => RepeatLastCapture,
        HotkeyAction.CaptureWindow => CaptureWindow,
        HotkeyAction.CaptureFixedRegion => CaptureFixedRegion,
        HotkeyAction.CaptureFreehand => CaptureFreehand,
        HotkeyAction.CapturePolygon => CapturePolygon,
        HotkeyAction.CaptureRoundedRegion => CaptureRoundedRegion,
        HotkeyAction.CaptureObject => CaptureObject,
        HotkeyAction.CaptureMenu => CaptureMenu,
        HotkeyAction.ToggleToolbar => ToggleToolbar,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };

    /// <summary>A copy with one action's hotkey replaced.</summary>
    public HotkeySettings With(HotkeyAction action, string? text) => action switch
    {
        HotkeyAction.CaptureRegion => this with { CaptureRegion = text },
        HotkeyAction.CaptureActiveWindow => this with { CaptureActiveWindow = text },
        HotkeyAction.CaptureFullScreen => this with { CaptureFullScreen = text },
        HotkeyAction.CaptureScrolling => this with { CaptureScrolling = text },
        HotkeyAction.Record => this with { Record = text },
        HotkeyAction.CaptureText => this with { CaptureText = text },
        HotkeyAction.ToggleDrawing => this with { ToggleDrawing = text },
        HotkeyAction.RepeatLastCapture => this with { RepeatLastCapture = text },
        HotkeyAction.CaptureWindow => this with { CaptureWindow = text },
        HotkeyAction.CaptureFixedRegion => this with { CaptureFixedRegion = text },
        HotkeyAction.CaptureFreehand => this with { CaptureFreehand = text },
        HotkeyAction.CapturePolygon => this with { CapturePolygon = text },
        HotkeyAction.CaptureRoundedRegion => this with { CaptureRoundedRegion = text },
        HotkeyAction.CaptureObject => this with { CaptureObject = text },
        HotkeyAction.CaptureMenu => this with { CaptureMenu = text },
        HotkeyAction.ToggleToolbar => this with { ToggleToolbar = text },
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };

    public HotkeySettings Normalize() => Enum.GetValues<HotkeyAction>().Aggregate(this, (h, action) => h.With(action, Clean(h.For(action))));

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
