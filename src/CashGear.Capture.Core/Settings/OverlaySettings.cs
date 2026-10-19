using System.Globalization;
using System.Text.Json.Serialization;

namespace CashGear.Capture.Core.Settings;

/// <summary>What the pointer gets in recordings besides itself.</summary>
public enum PointerEffect
{
    None,

    /// <summary>A soft coloured circle around the pointer.</summary>
    Halo,

    /// <summary>Everything but a circle around the pointer is dimmed.</summary>
    Spotlight,
}

/// <summary>Which keys the keystroke overlay shows (and the metadata keeps).</summary>
public enum KeystrokeMode
{
    Off,

    /// <summary>Keys pressed with Ctrl, Alt or Win, the function keys and Esc.</summary>
    Shortcuts,

    /// <summary>Everything typed, shortcuts included.</summary>
    AllKeys,
}

/// <summary>Where on the recorded area an overlay sits.</summary>
public enum OverlayAnchor
{
    BottomCenter,
    BottomLeft,
    BottomRight,
    TopCenter,
    TopLeft,
    TopRight,
}

public enum WebcamShape
{
    Circle,
    RoundedRectangle,
}

/// <summary>
/// What is drawn over recordings (Phase 12): clicks, the pointer, keystrokes and the webcam, either into the video
/// while recording (<see cref="BurnIn"/>) or kept in the metadata file to be added later; plus the on-screen pen.
/// </summary>
/// <param name="BurnIn">Draw the overlays into the video while recording. Off: the video is clean, and the overlays can be added later from the metadata file.</param>
/// <param name="WriteMetadata">Save <c>name.cgmeta.json</c> next to each recording: the pointer per frame, clicks, keys (as the keystroke overlay shows them) and focus changes.</param>
public sealed record OverlaySettings(
    bool BurnIn = true,
    bool WriteMetadata = true,
    ClickOverlaySettings? Clicks = null,
    PointerOverlaySettings? Pointer = null,
    KeystrokeOverlaySettings? Keystrokes = null,
    WebcamSettings? Webcam = null,
    DrawingSettings? Drawing = null)
{
    // Never null when made by the constructor (or read from a file), so settings compare by value.
    public ClickOverlaySettings? Clicks { get; init; } = Clicks ?? new ClickOverlaySettings();

    public PointerOverlaySettings? Pointer { get; init; } = Pointer ?? new PointerOverlaySettings();

    public KeystrokeOverlaySettings? Keystrokes { get; init; } = Keystrokes ?? new KeystrokeOverlaySettings();

    public WebcamSettings? Webcam { get; init; } = Webcam ?? new WebcamSettings();

    public DrawingSettings? Drawing { get; init; } = Drawing ?? new DrawingSettings();

    /// <summary>The click settings (never null after <see cref="Normalize"/>).</summary>
    [JsonIgnore]
    public ClickOverlaySettings ClickSettings => Clicks ?? new ClickOverlaySettings();

    [JsonIgnore]
    public PointerOverlaySettings PointerSettings => Pointer ?? new PointerOverlaySettings();

    [JsonIgnore]
    public KeystrokeOverlaySettings KeystrokeSettings => Keystrokes ?? new KeystrokeOverlaySettings();

    [JsonIgnore]
    public WebcamSettings WebcamSettings => Webcam ?? new WebcamSettings();

    [JsonIgnore]
    public DrawingSettings DrawingSettings => Drawing ?? new DrawingSettings();

    /// <summary>Something is drawn into the video while recording.</summary>
    [JsonIgnore]
    public bool DrawsLive => BurnIn && (DrawsInput || WebcamSettings.Enabled);

    /// <summary>The mouse (and keys, if shown) must be followed while recording: for overlays, the metadata or click sounds.</summary>
    [JsonIgnore]
    public bool NeedsInput => WriteMetadata || ClickSettings.Sound || (BurnIn && DrawsInput);

    /// <summary>Clicks, a pointer effect, the app's own pointer or keystrokes are on.</summary>
    [JsonIgnore]
    public bool DrawsInput =>
        ClickSettings.Enabled || PointerSettings.Effect != PointerEffect.None || PointerSettings.DrawsOwnPointer || KeystrokeSettings.Mode != KeystrokeMode.Off;

    public OverlaySettings Normalize() => this with
    {
        Clicks = ClickSettings.Normalize(),
        Pointer = PointerSettings.Normalize(),
        Keystrokes = KeystrokeSettings.Normalize(),
        Webcam = WebcamSettings.Normalize(),
        Drawing = DrawingSettings.Normalize(),
    };

    /// <summary>A colour as <c>#RRGGBB</c> or <c>#AARRGGBB</c>; anything else becomes <paramref name="fallback"/>.</summary>
    public static string Color(string? value, string fallback) => TryParseColor(value, out _) ? value!.Trim().ToUpperInvariant() : fallback;

    /// <summary>Reads <c>#RRGGBB</c> (opaque) or <c>#AARRGGBB</c> into ARGB.</summary>
    public static bool TryParseColor(string? value, out uint argb)
    {
        argb = 0;
        var text = value?.Trim();
        if (text is null || text.Length is not (7 or 9) || text[0] != '#'
            || !uint.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var number))
        {
            return false;
        }

        argb = text.Length == 7 ? 0xFF000000 | number : number;
        return true;
    }
}

/// <summary>An expanding ring where a mouse button is pressed.</summary>
/// <param name="Enabled">Show clicks.</param>
/// <param name="LeftColor">Ring colour for the left button (<c>#RRGGBB</c>).</param>
/// <param name="RightColor">… for the right button.</param>
/// <param name="MiddleColor">… for the middle button (and the side buttons).</param>
/// <param name="Size">The ring's largest radius at 100% scale, in pixels (16 to 80).</param>
/// <param name="Sound">Add a short click sound to the recording's sound at each press.</param>
public sealed record ClickOverlaySettings(
    bool Enabled = true,
    string LeftColor = ClickOverlaySettings.DefaultLeftColor,
    string RightColor = ClickOverlaySettings.DefaultRightColor,
    string MiddleColor = ClickOverlaySettings.DefaultMiddleColor,
    int Size = ClickOverlaySettings.DefaultSize,
    bool Sound = false)
{
    public const string DefaultLeftColor = "#FFC800";
    public const string DefaultRightColor = "#FF4040";
    public const string DefaultMiddleColor = "#30C060";
    public const int DefaultSize = 36;
    public const int MinSize = 16;
    public const int MaxSize = 80;

    public ClickOverlaySettings Normalize() => this with
    {
        LeftColor = OverlaySettings.Color(LeftColor, DefaultLeftColor),
        RightColor = OverlaySettings.Color(RightColor, DefaultRightColor),
        MiddleColor = OverlaySettings.Color(MiddleColor, DefaultMiddleColor),
        Size = Math.Clamp(Size, MinSize, MaxSize),
    };
}

/// <summary>What happens to the pointer in recordings.</summary>
/// <param name="Effect">A halo around it, a spotlight on it, or nothing.</param>
/// <param name="Color">The halo's colour.</param>
/// <param name="Radius">Halo or spotlight radius at 100% scale, in pixels (20 to 400).</param>
/// <param name="Dim">How dark the spotlight makes the rest, percent (10 to 90).</param>
/// <param name="Scale">The pointer drawn this many times its size (1 to 3). Above 1 the app draws the pointer itself.</param>
/// <param name="HideWhenIdle">Fade the pointer out when it hasn't moved for <paramref name="IdleSeconds"/>.</param>
/// <param name="IdleSeconds">1 to 30.</param>
public sealed record PointerOverlaySettings(
    PointerEffect Effect = PointerEffect.None,
    string Color = PointerOverlaySettings.DefaultColor,
    int Radius = PointerOverlaySettings.DefaultRadius,
    int Dim = PointerOverlaySettings.DefaultDim,
    double Scale = 1,
    bool HideWhenIdle = false,
    double IdleSeconds = PointerOverlaySettings.DefaultIdleSeconds)
{
    public const string DefaultColor = "#FFE040";
    public const int DefaultRadius = 60;
    public const int DefaultDim = 50;
    public const double DefaultIdleSeconds = 3;
    public const double MaxScale = 3;

    /// <summary>The app must draw the pointer itself (Windows' own can't be resized or hidden).</summary>
    [JsonIgnore]
    public bool DrawsOwnPointer => Scale > 1.001 || HideWhenIdle;

    public PointerOverlaySettings Normalize() => this with
    {
        Effect = Enum.IsDefined(Effect) ? Effect : PointerEffect.None,
        Color = OverlaySettings.Color(Color, DefaultColor),
        Radius = Math.Clamp(Radius, 20, 400),
        Dim = Math.Clamp(Dim, 10, 90),
        Scale = double.IsFinite(Scale) ? Math.Clamp(Math.Round(Scale, 2), 1, MaxScale) : 1,
        IdleSeconds = double.IsFinite(IdleSeconds) ? Math.Clamp(IdleSeconds, 1, 30) : DefaultIdleSeconds,
    };
}

/// <summary>Keys shown on the recording as they are pressed.</summary>
/// <param name="Mode">Off, shortcuts only, or everything typed.</param>
/// <param name="Position">Where the keys appear.</param>
/// <param name="FontFamily">The font (installed on this PC).</param>
/// <param name="FontSize">Size at 100% scale, in points (10 to 72).</param>
/// <param name="FadeSeconds">How long keys stay after the last press (0.5 to 10).</param>
/// <param name="ExcludedApps">Programs (file names, e.g. <c>keepass.exe</c>) whose keys are never shown or kept.</param>
public sealed record KeystrokeOverlaySettings(
    KeystrokeMode Mode = KeystrokeMode.Off,
    OverlayAnchor Position = OverlayAnchor.BottomCenter,
    string FontFamily = KeystrokeOverlaySettings.DefaultFontFamily,
    int FontSize = KeystrokeOverlaySettings.DefaultFontSize,
    double FadeSeconds = KeystrokeOverlaySettings.DefaultFadeSeconds,
    IReadOnlyList<string>? ExcludedApps = null)
{
    public const string DefaultFontFamily = "Segoe UI";
    public const int DefaultFontSize = 22;
    public const double DefaultFadeSeconds = 1.5;

    /// <summary>Password managers and Windows' credential prompts.</summary>
    public static IReadOnlyList<string> DefaultExcludedApps { get; } =
        ["1password.exe", "bitwarden.exe", "credentialuibroker.exe", "keepass.exe", "keepassxc.exe"];

    public IReadOnlyList<string>? ExcludedApps { get; init; } = ExcludedApps ?? DefaultExcludedApps;

    public KeystrokeOverlaySettings Normalize() => this with
    {
        Mode = Enum.IsDefined(Mode) ? Mode : KeystrokeMode.Off,
        Position = Enum.IsDefined(Position) ? Position : OverlayAnchor.BottomCenter,
        FontFamily = string.IsNullOrWhiteSpace(FontFamily) ? DefaultFontFamily : FontFamily.Trim(),
        FontSize = Math.Clamp(FontSize, 10, 72),
        FadeSeconds = double.IsFinite(FadeSeconds) ? Math.Clamp(FadeSeconds, 0.5, 10) : DefaultFadeSeconds,
        ExcludedApps = [.. (ExcludedApps ?? DefaultExcludedApps)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)],
    };

    /// <summary>Whether keys typed into <paramref name="exe"/> (a program file name) are left out.</summary>
    public bool Excludes(string? exe) =>
        exe is not null && (ExcludedApps ?? DefaultExcludedApps).Contains(exe.ToLowerInvariant(), StringComparer.Ordinal);

    /// <summary>Records compare their lists by reference; this compares the excluded apps by content.</summary>
    public bool Equals(KeystrokeOverlaySettings? other) =>
        other is not null
        && Mode == other.Mode
        && Position == other.Position
        && FontFamily == other.FontFamily
        && FontSize == other.FontSize
        && FadeSeconds.Equals(other.FadeSeconds)
        && (ExcludedApps ?? []).SequenceEqual(other.ExcludedApps ?? []);

    public override int GetHashCode() => HashCode.Combine(Mode, Position, FontFamily, FontSize, FadeSeconds, (ExcludedApps ?? []).Count);
}

/// <summary>The webcam in recordings: drawn over the screen and kept as its own video track.</summary>
/// <param name="Enabled">Record the webcam.</param>
/// <param name="Device">The camera's device ID, or null for the first one.</param>
/// <param name="Width">Capture width in pixels (the camera's nearest format is used).</param>
/// <param name="Height">Capture height in pixels.</param>
/// <param name="Fps">Capture frame rate.</param>
/// <param name="Shape">Circle or rounded rectangle.</param>
/// <param name="Size">Its height as a share of the recorded area's height, percent (10 to 60).</param>
/// <param name="CenterX">Where its centre is, as a share of the area's width, percent (0 to 100).</param>
/// <param name="CenterY">… of the area's height, percent.</param>
/// <param name="BorderColor">The border's colour.</param>
/// <param name="BorderWidth">The border's width at 100% scale, pixels (0 to 12).</param>
/// <param name="Mirror">Show it mirrored, like a mirror (the separate track stays as the camera sees).</param>
public sealed record WebcamSettings(
    bool Enabled = false,
    string? Device = null,
    int Width = 1280,
    int Height = 720,
    int Fps = 30,
    WebcamShape Shape = WebcamShape.Circle,
    int Size = WebcamSettings.DefaultSize,
    double CenterX = WebcamSettings.DefaultCenterX,
    double CenterY = WebcamSettings.DefaultCenterY,
    string BorderColor = WebcamSettings.DefaultBorderColor,
    int BorderWidth = 3,
    bool Mirror = true)
{
    public const int DefaultSize = 28;
    public const double DefaultCenterX = 86;
    public const double DefaultCenterY = 78;
    public const string DefaultBorderColor = "#FFFFFF";

    public WebcamSettings Normalize() => this with
    {
        Device = string.IsNullOrWhiteSpace(Device) ? null : Device.Trim(),
        Width = Math.Clamp(Width, 160, 3840),
        Height = Math.Clamp(Height, 120, 2160),
        Fps = Math.Clamp(Fps, 5, 60),
        Shape = Enum.IsDefined(Shape) ? Shape : WebcamShape.Circle,
        Size = Math.Clamp(Size, 10, 60),
        CenterX = double.IsFinite(CenterX) ? Math.Clamp(CenterX, 0, 100) : DefaultCenterX,
        CenterY = double.IsFinite(CenterY) ? Math.Clamp(CenterY, 0, 100) : DefaultCenterY,
        BorderColor = OverlaySettings.Color(BorderColor, DefaultBorderColor),
        BorderWidth = Math.Clamp(BorderWidth, 0, 12),
    };
}

/// <summary>The pen for drawing on the screen while recording.</summary>
/// <param name="Color">Pen colour.</param>
/// <param name="Width">Pen width at 100% scale, pixels (1 to 20).</param>
/// <param name="FadeSeconds">Strokes fade away this long after they are drawn; 0 keeps them until cleared (0 to 30).</param>
public sealed record DrawingSettings(string Color = DrawingSettings.DefaultColor, int Width = 4, double FadeSeconds = 0)
{
    public const string DefaultColor = "#FF3030";

    public DrawingSettings Normalize() => this with
    {
        Color = OverlaySettings.Color(Color, DefaultColor),
        Width = Math.Clamp(Width, 1, 20),
        FadeSeconds = double.IsFinite(FadeSeconds) ? Math.Clamp(FadeSeconds, 0, 30) : 0,
    };
}
