using System.Collections.ObjectModel;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CashGear.Capture.App.Settings;

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

    public OverlaySettingsViewModel(IAudioApps? apps, IReadOnlyList<string> fonts)
    {
        _apps = apps;
        Fonts = fonts;
    }

    /// <summary>The colours offered (a colour set by hand in the settings file is added when loaded).</summary>
    public ObservableCollection<ColorChoice> Colors { get; } =
    [
        new("#FFC800", "Yellow"),
        new("#FF8C00", "Orange"),
        new("#FF4040", "Red"),
        new("#FF4FA3", "Pink"),
        new("#30C060", "Green"),
        new("#00C8E0", "Cyan"),
        new("#2F80FF", "Blue"),
        new("#FFFFFF", "White"),
        new("#000000", "Black"),
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

        var custom = new ColorChoice(value, $"Custom ({value})");
        Colors.Add(custom);
        return custom;
    }
}
