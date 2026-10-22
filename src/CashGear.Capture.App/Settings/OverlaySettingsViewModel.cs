using System.Collections.ObjectModel;
using System.Globalization;
using CashGear.Capture.App.Resources;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Overlays.Webcam;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CashGear.Capture.App.Settings;

/// <summary>A camera in the list: its device ID (null: the first camera) and name.</summary>
public sealed record CameraChoice(string? Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A webcam picture size.</summary>
public sealed record SizeChoice(int Width, int Height)
{
    public override string ToString() => $"{Width} × {Height}";
}

/// <summary>A colour in a list: its <c>#RRGGBB</c> value and a name.</summary>
public sealed record ColorChoice(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Settings › Overlays: clicks, the pointer, keystrokes (with the apps whose keys are never shown), whether
/// overlays go into the video while recording or are added later, and the metadata file. Part of the settings
/// window's view model (<see cref="SettingsViewModel.Overlays"/>): loaded from and built back into
/// <see cref="OverlaySettings"/> with the rest.
/// </summary>
public sealed partial class OverlaySettingsViewModel : ObservableObject
{
    private readonly IAudioApps? _apps;
    private readonly IWebcams? _webcams;

    public OverlaySettingsViewModel(IAudioApps? apps, IReadOnlyList<string> fonts, IWebcams? webcams = null)
    {
        _apps = apps;
        _webcams = webcams;
        Fonts = fonts;
    }

    /// <summary>Lets the window show the chosen camera in the placement preview.</summary>
    public IWebcams? Webcams => _webcams;

    public ObservableCollection<CameraChoice> Cameras { get; } = [new CameraChoice(null, OverlaysText.FirstCamera)];

    public IReadOnlyList<SizeChoice> WebcamSizes { get; } = [new(640, 360), new(640, 480), new(1280, 720), new(1920, 1080)];

    public IReadOnlyList<int> WebcamRates { get; } = [15, 24, 30, 60];

    public IReadOnlyList<WebcamShape> WebcamShapes { get; } = Enum.GetValues<WebcamShape>();

    [ObservableProperty]
    private bool _webcamEnabled;

    [ObservableProperty]
    private CameraChoice? _camera;

    [ObservableProperty]
    private SizeChoice? _webcamSize;

    [ObservableProperty]
    private int _webcamFps;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WebcamIsCircle))]
    private WebcamShape _webcamShape;

    /// <summary>Its height as a share of the recorded area's height, percent.</summary>
    [ObservableProperty]
    private double _webcamHeight;

    /// <summary>Where its centre is, percent of the recorded area's width.</summary>
    [ObservableProperty]
    private double _webcamCenterX;

    [ObservableProperty]
    private double _webcamCenterY;

    [ObservableProperty]
    private ColorChoice? _webcamBorderColor;

    [ObservableProperty]
    private double _webcamBorderWidth;

    [ObservableProperty]
    private bool _webcamMirror;

    [ObservableProperty]
    private ColorChoice? _penColor;

    [ObservableProperty]
    private double _penWidth;

    /// <summary>Drawings fade away this long after they are drawn; 0 keeps them until cleared.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PenFadeText))]
    private double _penFade;

    public string PenFadeText => PenFade <= 0 ? OverlaysText.Never : string.Format(CultureInfo.CurrentCulture, OverlaysText.WholeSecondsFormat, PenFade);

    /// <summary>What the camera list or preview found (no camera, privacy settings), shown under the list.</summary>
    [ObservableProperty]
    private string _webcamStatus = string.Empty;

    public bool WebcamIsCircle => WebcamShape == WebcamShape.Circle;

    /// <summary>Lists the cameras connected now, keeping the chosen one (also when it isn't connected).</summary>
    [RelayCommand]
    public async Task RefreshCamerasAsync()
    {
        var chosen = Camera?.Id;
        var found = _webcams is null ? [] : await _webcams.ListAsync().ConfigureAwait(true);
        while (Cameras.Count > 1)
        {
            Cameras.RemoveAt(1);
        }

        foreach (var camera in found)
        {
            Cameras.Add(new CameraChoice(camera.Id, camera.Name));
        }

        Camera = CameraFor(chosen);
        WebcamStatus = found.Count == 0 ? OverlaysText.NoCamera : string.Empty;
    }

    /// <summary>Moves the webcam's centre to a point of the preview (shares of the recorded area, 0 to 1).</summary>
    public void MoveWebcam(double x, double y)
    {
        WebcamCenterX = Math.Round(Math.Clamp(x, 0, 1) * 100, 1);
        WebcamCenterY = Math.Round(Math.Clamp(y, 0, 1) * 100, 1);
    }

    /// <summary>
    /// Moves the webcam by <paramref name="dx"/> and <paramref name="dy"/> percentage points of the recorded area (the
    /// arrow keys), keeping all of it inside: its centre stays <paramref name="halfWidth"/> and
    /// <paramref name="halfHeight"/> points in from the edges. A centre set outside that moves from where the box shows.
    /// </summary>
    public void NudgeWebcam(double dx, double dy, double halfWidth, double halfHeight)
    {
        static double Step(double centre, double by, double half)
        {
            half = Math.Clamp(half, 0, 50);
            return Math.Clamp(Math.Clamp(centre, half, 100 - half) + by, half, 100 - half);
        }

        MoveWebcam(Step(WebcamCenterX, dx, halfWidth) / 100, Step(WebcamCenterY, dy, halfHeight) / 100);
    }

    private CameraChoice CameraFor(string? id)
    {
        if (id is null)
        {
            return Cameras[0];
        }

        var known = Cameras.FirstOrDefault(c => c.Id == id);
        if (known is null)
        {
            known = new CameraChoice(id, OverlaysText.CameraNotConnected);
            Cameras.Add(known);
        }

        return known;
    }

    /// <summary>The colours offered (a colour set by hand in the settings file is added when loaded).</summary>
    public ObservableCollection<ColorChoice> Colors { get; } =
    [
        new("#FFC800", OverlaysText.ColorYellow),
        new("#FF8C00", OverlaysText.ColorOrange),
        new("#FF4040", OverlaysText.ColorRed),
        new("#FF4FA3", OverlaysText.ColorPink),
        new("#30C060", OverlaysText.ColorGreen),
        new("#00C8E0", OverlaysText.ColorCyan),
        new("#2F80FF", OverlaysText.ColorBlue),
        new("#FFFFFF", OverlaysText.ColorWhite),
        new("#000000", OverlaysText.ColorBlack),
    ];

    public IReadOnlyList<PointerEffect> PointerEffects { get; } = Enum.GetValues<PointerEffect>();

    public IReadOnlyList<KeystrokeMode> KeystrokeModes { get; } = Enum.GetValues<KeystrokeMode>();

    public IReadOnlyList<OverlayAnchor> Positions { get; } = Enum.GetValues<OverlayAnchor>();

    /// <summary>The fonts installed here, by name.</summary>
    public IReadOnlyList<string> Fonts { get; }

    /// <summary>Programs whose keys are never shown or kept (file names such as <c>keepass.exe</c>).</summary>
    public ObservableCollection<string> ExcludedApps { get; } = [];

    /// <summary>Programs running now, to pick one to exclude.</summary>
    public ObservableCollection<AppChoice> RunningApps { get; } = [];

    [ObservableProperty]
    private bool _burnIn;

    [ObservableProperty]
    private bool _writeMetadata;

    [ObservableProperty]
    private bool _clicksEnabled;

    [ObservableProperty]
    private ColorChoice? _leftColor;

    [ObservableProperty]
    private ColorChoice? _rightColor;

    [ObservableProperty]
    private ColorChoice? _middleColor;

    [ObservableProperty]
    private double _clickSize;

    [ObservableProperty]
    private bool _clickSound;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHalo), nameof(IsSpotlight), nameof(HasEffect))]
    private PointerEffect _pointerEffect;

    [ObservableProperty]
    private ColorChoice? _pointerColor;

    [ObservableProperty]
    private double _pointerRadius;

    [ObservableProperty]
    private double _pointerDim;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PointerScaleText))]
    private double _pointerScale;

    [ObservableProperty]
    private bool _hideIdlePointer;

    [ObservableProperty]
    private double _idleSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeystrokesOn))]
    private KeystrokeMode _keystrokeMode;

    [ObservableProperty]
    private OverlayAnchor _keystrokePosition;

    [ObservableProperty]
    private string _keystrokeFont = KeystrokeOverlaySettings.DefaultFontFamily;

    [ObservableProperty]
    private double _keystrokeSize;

    [ObservableProperty]
    private double _keystrokeFade;

    /// <summary>A program typed or picked to add to <see cref="ExcludedApps"/>.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddExcludedAppCommand))]
    private string _newExcludedApp = string.Empty;

    public bool IsHalo => PointerEffect == PointerEffect.Halo;

    public bool IsSpotlight => PointerEffect == PointerEffect.Spotlight;

    public bool HasEffect => PointerEffect != PointerEffect.None;

    public bool KeystrokesOn => KeystrokeMode != KeystrokeMode.Off;

    public string PointerScaleText => $"{PointerScale:0.##}×";

    public void Load(OverlaySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var s = settings.Normalize();
        BurnIn = s.BurnIn;
        WriteMetadata = s.WriteMetadata;
        var clicks = s.ClickSettings;
        ClicksEnabled = clicks.Enabled;
        LeftColor = Choice(clicks.LeftColor);
        RightColor = Choice(clicks.RightColor);
        MiddleColor = Choice(clicks.MiddleColor);
        ClickSize = clicks.Size;
        ClickSound = clicks.Sound;
        var pointer = s.PointerSettings;
        PointerEffect = pointer.Effect;
        PointerColor = Choice(pointer.Color);
        PointerRadius = pointer.Radius;
        PointerDim = pointer.Dim;
        PointerScale = pointer.Scale;
        HideIdlePointer = pointer.HideWhenIdle;
        IdleSeconds = pointer.IdleSeconds;
        var keys = s.KeystrokeSettings;
        KeystrokeMode = keys.Mode;
        KeystrokePosition = keys.Position;
        KeystrokeFont = keys.FontFamily;
        KeystrokeSize = keys.FontSize;
        KeystrokeFade = keys.FadeSeconds;
        ExcludedApps.Clear();
        foreach (var app in keys.ExcludedApps ?? [])
        {
            ExcludedApps.Add(app);
        }

        var webcam = s.WebcamSettings;
        WebcamEnabled = webcam.Enabled;
        Camera = CameraFor(webcam.Device);
        WebcamSize = WebcamSizes.FirstOrDefault(c => c.Width == webcam.Width && c.Height == webcam.Height) ?? new SizeChoice(webcam.Width, webcam.Height);
        WebcamFps = webcam.Fps;
        WebcamShape = webcam.Shape;
        WebcamHeight = webcam.Size;
        WebcamCenterX = webcam.CenterX;
        WebcamCenterY = webcam.CenterY;
        WebcamBorderColor = Choice(webcam.BorderColor);
        WebcamBorderWidth = webcam.BorderWidth;
        WebcamMirror = webcam.Mirror;
        var pen = s.DrawingSettings;
        PenColor = Choice(pen.Color);
        PenWidth = pen.Width;
        PenFade = pen.FadeSeconds;
    }

    /// <summary>The edited values applied on top of <paramref name="current"/> (webcam and drawing settings are kept).</summary>
    public OverlaySettings Build(OverlaySettings current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return current with
        {
            BurnIn = BurnIn,
            WriteMetadata = WriteMetadata,
            Clicks = current.ClickSettings with
            {
                Enabled = ClicksEnabled,
                LeftColor = LeftColor?.Value ?? ClickOverlaySettings.DefaultLeftColor,
                RightColor = RightColor?.Value ?? ClickOverlaySettings.DefaultRightColor,
                MiddleColor = MiddleColor?.Value ?? ClickOverlaySettings.DefaultMiddleColor,
                Size = (int)Math.Round(ClickSize),
                Sound = ClickSound,
            },
            Pointer = current.PointerSettings with
            {
                Effect = PointerEffect,
                Color = PointerColor?.Value ?? PointerOverlaySettings.DefaultColor,
                Radius = (int)Math.Round(PointerRadius),
                Dim = (int)Math.Round(PointerDim),
                Scale = Math.Round(PointerScale, 2),
                HideWhenIdle = HideIdlePointer,
                IdleSeconds = Math.Round(IdleSeconds, 1),
            },
            Keystrokes = current.KeystrokeSettings with
            {
                Mode = KeystrokeMode,
                Position = KeystrokePosition,
                FontFamily = string.IsNullOrWhiteSpace(KeystrokeFont) ? KeystrokeOverlaySettings.DefaultFontFamily : KeystrokeFont.Trim(),
                FontSize = (int)Math.Round(KeystrokeSize),
                FadeSeconds = Math.Round(KeystrokeFade, 1),
                ExcludedApps = [.. ExcludedApps],
            },
            Webcam = current.WebcamSettings with
            {
                Enabled = WebcamEnabled,
                Device = Camera?.Id,
                Width = WebcamSize?.Width ?? 1280,
                Height = WebcamSize?.Height ?? 720,
                Fps = WebcamFps,
                Shape = WebcamShape,
                Size = (int)Math.Round(WebcamHeight),
                CenterX = Math.Round(WebcamCenterX, 1),
                CenterY = Math.Round(WebcamCenterY, 1),
                BorderColor = WebcamBorderColor?.Value ?? WebcamSettings.DefaultBorderColor,
                BorderWidth = (int)Math.Round(WebcamBorderWidth),
                Mirror = WebcamMirror,
            },
            Drawing = new DrawingSettings(PenColor?.Value ?? DrawingSettings.DefaultColor, (int)Math.Round(PenWidth), Math.Round(PenFade)),
        };
    }

    /// <summary>Lists the programs running now, to pick one to exclude.</summary>
    [RelayCommand]
    public void RefreshRunningApps()
    {
        RunningApps.Clear();
        foreach (var app in _apps?.List() ?? [])
        {
            RunningApps.Add(new AppChoice(app.Exe, app.Name, $"{app.Name} ({app.Exe})"));
        }
    }

    private bool CanAddExcludedApp() => Program(NewExcludedApp) is { } exe && !ExcludedApps.Contains(exe);

    [RelayCommand(CanExecute = nameof(CanAddExcludedApp))]
    private void AddExcludedApp()
    {
        if (Program(NewExcludedApp) is { } exe && !ExcludedApps.Contains(exe))
        {
            var at = 0;
            while (at < ExcludedApps.Count && string.CompareOrdinal(ExcludedApps[at], exe) < 0)
            {
                at++;
            }

            ExcludedApps.Insert(at, exe);
            NewExcludedApp = string.Empty;
        }
    }

    [RelayCommand]
    private void RemoveExcludedApp(string? exe)
    {
        if (exe is not null)
        {
            ExcludedApps.Remove(exe);
            AddExcludedAppCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>A program file name from what was typed or picked: <c>KeePass</c> → <c>keepass.exe</c>; null if it can't be one.</summary>
    internal static string? Program(string? text)
    {
        var name = text?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        // "KeePass (keepass.exe)" from the running apps list.
        if (name.EndsWith(')') && name.LastIndexOf('(') is var open and >= 0)
        {
            name = name[(open + 1)..^1].Trim();
        }

        name = System.IO.Path.GetFileName(name).ToLowerInvariant();
        if (name.Length == 0 || name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
        {
            return null;
        }

        return name.EndsWith(".exe", StringComparison.Ordinal) ? name : name + ".exe";
    }

    /// <summary>The list's entry for a colour, adding one for a colour set by hand.</summary>
    private ColorChoice Choice(string value)
    {
        var found = Colors.FirstOrDefault(c => string.Equals(c.Value, value, StringComparison.OrdinalIgnoreCase));
        if (found is not null)
        {
            return found;
        }

        var custom = new ColorChoice(value, OverlaysText.ColorCustom(value));
        Colors.Add(custom);
        return custom;
    }
}
