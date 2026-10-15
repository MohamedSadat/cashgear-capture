using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CashGear.Capture.App.Hotkeys;
using CashGear.Capture.App.Platform;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Hotkeys;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Ocr;
using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Templates = CashGear.Capture.Core.Output.FileNameTemplate;

namespace CashGear.Capture.App.Settings;

/// <summary>Picks a folder (behind an interface so the view model is testable).</summary>
public interface IFolderPicker
{
    /// <summary>Returns the chosen folder, or null if the user cancelled.</summary>
    string? PickFolder(string? initialFolder);
}

/// <summary>
/// The settings window's state. Loads a copy of the current settings, edits it in memory, validates, and saves it
/// through <see cref="ISettingsService"/> (which persists it and, via the registrar, re-registers hotkeys).
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private static readonly FileNameContext PreviewContext =
        new(new DateTimeOffset(2026, 3, 7, 14, 30, 5, TimeSpan.Zero), CaptureKind.Region, "DISPLAY1", "Untitled - Notepad");

    private readonly ISettingsService _settings;
    private readonly AppPaths _paths;
    private readonly HotkeyRegistrar _hotkeys;
    private readonly IStartupRegistration _startup;
    private readonly IFolderPicker _folderPicker;
    private readonly ILogger<SettingsViewModel> _logger;
    private IDisposable? _hotkeySuspension;

    public SettingsViewModel(
        ISettingsService settings,
        AppPaths paths,
        HotkeyRegistrar hotkeys,
        IStartupRegistration startup,
        IFolderPicker folderPicker,
        ILogger<SettingsViewModel> logger,
        IFfmpeg? ffmpeg = null,
        IOcrService? ocr = null)
    {
        OcrLanguages = [new OcrLanguageChoice(null, "Automatic (your Windows languages)"),
            .. (ocr?.AvailableLanguages ?? []).Select(l => new OcrLanguageChoice(l.Tag, $"{l.DisplayName} — {l.NativeName}"))];
        FfmpegStatus = ffmpeg?.Path is { } found
            ? "ffmpeg: " + found
            : $"ffmpeg.exe was not found, so recording is unavailable. Put it in {ffmpeg?.ExpectedPath ?? "the app's ffmpeg folder"}.";
        IsFfmpegMissing = ffmpeg?.Path is null;
        _settings = settings;
        _paths = paths;
        _hotkeys = hotkeys;
        _startup = startup;
        _folderPicker = folderPicker;
        _logger = logger;

        Hotkeys = new ObservableCollection<HotkeyRowViewModel>(
            Enum.GetValues<HotkeyAction>().Select(a => new HotkeyRowViewModel(a, null)));
        foreach (var row in Hotkeys)
        {
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(HotkeyRowViewModel.Text))
                {
                    Revalidate();
                }
            };
        }

        Load(settings.Current, startup.IsEnabled);
        _hotkeys.RegistrationsChanged += OnRegistrationsChanged;
    }

    /// <summary>Raised when the window should close (after OK or Cancel).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Raised after settings were saved, so the window can re-apply the theme.</summary>
    public event EventHandler? Saved;

    public IReadOnlyList<ImageFileFormat> Formats { get; } = Enum.GetValues<ImageFileFormat>();

    public IReadOnlyList<CaptureBackend> Backends { get; } = Enum.GetValues<CaptureBackend>();

    public IReadOnlyList<FullScreenOutput> FullScreenOutputs { get; } = Enum.GetValues<FullScreenOutput>();

    public IReadOnlyList<AppTheme> Themes { get; } = Enum.GetValues<AppTheme>();

    public IReadOnlyList<int> DelayChoices { get; } = [0, 1, 2, 3, 5, 10, 15, 30];

    public IReadOnlyList<int> HideDelayChoices { get; } = [0, 100, 200, 300, 500, 1000];

    public IReadOnlyList<int> ScrollSettleChoices { get; } = [100, 250, 500, 1000];

    public IReadOnlyList<int> FrameRates { get; } = RecordingSettings.FrameRates;

    public IReadOnlyList<RecordingEncoder> Encoders { get; } = Enum.GetValues<RecordingEncoder>();

    /// <summary>"Automatic" and each language Windows can recognize.</summary>
    public IReadOnlyList<OcrLanguageChoice> OcrLanguages { get; }

    /// <summary>Where ffmpeg is, or why recording is unavailable.</summary>
    public string FfmpegStatus { get; }

    public bool IsFfmpegMissing { get; }

    public IReadOnlyList<int> ScrollMaxHeightChoices { get; } = [10_000, 20_000, 40_000, 60_000];

    public IReadOnlyList<string> TokenHelp { get; } =
        Templates.Tokens.Select(t => $"{t.Token}  {t.Description}").ToList();

    public ObservableCollection<HotkeyRowViewModel> Hotkeys { get; }

    /// <summary>Shown when the folder box is empty.</summary>
    public string DefaultFolder => _paths.DefaultCaptureDirectory;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileNamePreview))]
    private string _outputFolder = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsJpeg), nameof(FileNamePreview))]
    private ImageFileFormat _format;

    [ObservableProperty]
    private int _jpegQuality;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileNamePreview))]
    private string _fileNameTemplate = string.Empty;

    [ObservableProperty]
    private bool _saveToFile;

    [ObservableProperty]
    private bool _copyToClipboard;

    [ObservableProperty]
    private bool _openEditor;

    [ObservableProperty]
    private int _delaySeconds;

    [ObservableProperty]
    private int _hideDelayMs;

    [ObservableProperty]
    private OcrLanguageChoice? _ocrLanguage;

    [ObservableProperty]
    private int _recordingFps;

    /// <summary>15 (best) to 35 (smallest).</summary>
    [ObservableProperty]
    private int _recordingQuality;

    [ObservableProperty]
    private bool _recordingIncludeCursor;

    [ObservableProperty]
    private bool _recordingShowClicks;

    [ObservableProperty]
    private RecordingEncoder _recordingEncoder;

    /// <summary>Scrolling capture: pause after each scroll.</summary>
    [ObservableProperty]
    private int _scrollSettleMs;

    /// <summary>Scrolling capture: stop at this height.</summary>
    [ObservableProperty]
    private int _scrollMaxHeight;

    [ObservableProperty]
    private bool _includeCursor;

    [ObservableProperty]
    private bool _includeWindowShadow;

    [ObservableProperty]
    private CaptureBackend _backend;

    [ObservableProperty]
    private FullScreenOutput _fullScreenOutput;

    [ObservableProperty]
    private bool _startWithWindows;

    [ObservableProperty]
    private AppTheme _theme;

    /// <summary>Every validation problem, one per line; empty when the settings can be saved.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(ApplyCommand))]
    private string _errors = string.Empty;

    public bool IsJpeg => Format == ImageFileFormat.Jpeg;

    /// <summary>What a region capture would be called, e.g. <c>Capture_2026-03-07_14-30-05_region.png</c>.</summary>
    public string FileNamePreview =>
        Templates.TryValidate(FileNameTemplate, out _)
            ? Templates.Expand(FileNameTemplate, PreviewContext, () => 1) + Format.FileExtension()
            : "—";

    public bool HasErrors => Errors.Length > 0;

    /// <summary>
    /// Starts recording a hotkey: global hotkeys are released so the pressed keys reach the box (otherwise
    /// PrintScreen would take a screenshot instead). Paired with <see cref="EndHotkeyRecording"/>.
    /// </summary>
    public void BeginHotkeyRecording() => _hotkeySuspension ??= _hotkeys.Suspend();

    public void EndHotkeyRecording()
    {
        _hotkeySuspension?.Dispose();
        _hotkeySuspension = null;
    }

    public void Dispose()
    {
        EndHotkeyRecording();
        _hotkeys.RegistrationsChanged -= OnRegistrationsChanged;
    }

    partial void OnOutputFolderChanged(string value) => Revalidate();

    partial void OnFileNameTemplateChanged(string value) => Revalidate();

    partial void OnSaveToFileChanged(bool value) => Revalidate();

    partial void OnCopyToClipboardChanged(bool value) => Revalidate();

    partial void OnOpenEditorChanged(bool value) => Revalidate();

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (await ApplyChangesAsync())
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task ApplyAsync() => ApplyChangesAsync();

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void BrowseFolder()
    {
        var initial = string.IsNullOrWhiteSpace(OutputFolder) ? DefaultFolder : Environment.ExpandEnvironmentVariables(OutputFolder);
        if (_folderPicker.PickFolder(initial) is { } folder)
        {
            OutputFolder = string.Equals(folder, DefaultFolder, StringComparison.OrdinalIgnoreCase) ? string.Empty : folder;
        }
    }

    /// <summary>Puts every setting back to its default in the window (nothing is saved until OK or Apply).</summary>
    [RelayCommand]
    private void ResetToDefaults() => Load(new AppSettings(), StartWithWindows);

    [RelayCommand]
    private void OpenCaptureFolder()
    {
        var folder = string.IsNullOrWhiteSpace(OutputFolder) ? DefaultFolder : Environment.ExpandEnvironmentVariables(OutputFolder);
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true })?.Dispose();
    }

    [RelayCommand]
    private void OpenSettingsFile() =>
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_paths.SettingsFile}\"") { UseShellExecute = true })?.Dispose();

    private bool CanSave() => !HasErrors;

    private async Task<bool> ApplyChangesAsync()
    {
        Revalidate();
        if (HasErrors)
        {
            return false;
        }

        var startupChanged = StartWithWindows != _startup.IsEnabled;
        await _settings.UpdateAsync(BuildSettings);
        if (startupChanged)
        {
            try
            {
                _startup.SetEnabled(StartWithWindows);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                LogStartupFailed(ex);
                Errors = "Could not change \"Start with Windows\": " + ex.Message;
                return false;
            }
        }

        RefreshHotkeyStatus();
        Saved?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>The edited settings applied on top of <paramref name="current"/>.</summary>
    internal AppSettings BuildSettings(AppSettings current) => current with
    {
        Capture = current.Capture with
        {
            Backend = Backend,
            IncludeCursor = IncludeCursor,
            HideDelayMs = HideDelayMs,
            FullScreenOutput = FullScreenOutput,
            IncludeWindowShadow = IncludeWindowShadow,
            DelaySeconds = DelaySeconds,
            ScrollSettleMs = ScrollSettleMs,
            ScrollMaxHeight = ScrollMaxHeight,
        },
        Output = current.Output with
        {
            Folder = string.IsNullOrWhiteSpace(OutputFolder) ? null : OutputFolder.Trim(),
            Format = Format,
            JpegQuality = JpegQuality,
            FileNameTemplate = FileNameTemplate.Trim(),
        },
        AfterCapture = new AfterCaptureSettings(SaveToFile, CopyToClipboard, OpenEditor),
        Hotkeys = Hotkeys.Aggregate(current.Hotkeys, (h, row) => h.With(row.Action, row.SettingText)),
        Appearance = new AppearanceSettings(Theme),
        Recording = new RecordingSettings(RecordingFps, RecordingQuality, RecordingIncludeCursor, RecordingShowClicks, RecordingEncoder),
        Ocr = new OcrSettings(OcrLanguage?.Tag),
    };

    private void Load(AppSettings s, bool startWithWindows)
    {
        OutputFolder = s.Output.Folder ?? string.Empty;
        Format = s.Output.Format;
        JpegQuality = s.Output.JpegQuality;
        FileNameTemplate = s.Output.FileNameTemplate;
        SaveToFile = s.AfterCapture.Save;
        CopyToClipboard = s.AfterCapture.CopyToClipboard;
        OpenEditor = s.AfterCapture.OpenEditor;
        DelaySeconds = s.Capture.DelaySeconds;
        HideDelayMs = s.Capture.HideDelayMs;
        ScrollSettleMs = s.Capture.ScrollSettleMs;
        // A language set by hand but not installed still shows (and is kept) rather than silently becoming Automatic.
        OcrLanguage = OcrLanguages.FirstOrDefault(l => string.Equals(l.Tag, s.Ocr.Language, StringComparison.OrdinalIgnoreCase))
            ?? new OcrLanguageChoice(s.Ocr.Language, s.Ocr.Language + " (not installed)");
        RecordingFps = s.Recording.Fps;
        RecordingQuality = s.Recording.Quality;
        RecordingIncludeCursor = s.Recording.IncludeCursor;
        RecordingShowClicks = s.Recording.ShowClicks;
        RecordingEncoder = s.Recording.Encoder;
        ScrollMaxHeight = s.Capture.ScrollMaxHeight;
        IncludeCursor = s.Capture.IncludeCursor;
        IncludeWindowShadow = s.Capture.IncludeWindowShadow;
        Backend = s.Capture.Backend;
        FullScreenOutput = s.Capture.FullScreenOutput;
        StartWithWindows = startWithWindows;
        Theme = s.Appearance.Theme;
        foreach (var row in Hotkeys)
        {
            row.Text = s.Hotkeys.For(row.Action) ?? string.Empty;
        }

        Revalidate();
    }

    private void Revalidate()
    {
        var errors = new List<string>();
        if (!Templates.TryValidate(FileNameTemplate, out var templateError))
        {
            errors.Add("File name: " + templateError);
        }

        if (!string.IsNullOrWhiteSpace(OutputFolder))
        {
            var expanded = Environment.ExpandEnvironmentVariables(OutputFolder.Trim());
            if (expanded.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || !Path.IsPathFullyQualified(expanded))
            {
                errors.Add("Folder: use a full path such as D:\\Screenshots (or leave it empty for the default).");
            }
        }

        if (!SaveToFile && !CopyToClipboard && !OpenEditor)
        {
            errors.Add("After capture: choose at least one of \"Open in the editor\", \"Save to file\" and \"Copy to clipboard\".");
        }

        errors.AddRange(ValidateHotkeys());
        Errors = string.Join(Environment.NewLine, errors);
        OnPropertyChanged(nameof(HasErrors));
    }

    /// <summary>Checks the rows against each other and shows each row's status; returns the blocking problems.</summary>
    private IEnumerable<string> ValidateHotkeys()
    {
        var seen = new Dictionary<HotkeyGesture, HotkeyRowViewModel>();
        foreach (var row in Hotkeys)
        {
            if (row.SettingText is not { } text)
            {
                row.SetStatus("Not set", problem: false);
                continue;
            }

            if (!HotkeyGesture.TryParse(text, out var gesture))
            {
                row.SetStatus("Not a valid hotkey", problem: true);
                yield return $"{row.Label}: \"{text}\" is not a valid hotkey.";
                continue;
            }

            if (!gesture.IsSafeAsGlobalHotkey)
            {
                row.SetStatus("Add Ctrl, Alt, Shift or Win", problem: true);
                yield return $"{row.Label}: {gesture} on its own would block that key in every app; add Ctrl, Alt, Shift or Win.";
                continue;
            }

            if (seen.TryGetValue(gesture, out var other))
            {
                row.SetStatus($"Same as {other.Label}", problem: true);
                yield return $"{row.Label}: {gesture} is already used for {other.Label}.";
                continue;
            }

            seen[gesture] = row;
            row.SetStatus(RegistrationStatus(row.Action, gesture), _hotkeys.Problems.ContainsKey(row.Action) && IsApplied(row.Action, gesture));
        }
    }

    /// <summary>What the registrar says about the key as it is registered now (only meaningful once saved).</summary>
    private string RegistrationStatus(HotkeyAction action, HotkeyGesture gesture)
    {
        if (!IsApplied(action, gesture))
        {
            return "Applies when you save";
        }

        return _hotkeys.Problems.TryGetValue(action, out var problem) ? problem : "Active";
    }

    private bool IsApplied(HotkeyAction action, HotkeyGesture gesture) =>
        HotkeyGesture.TryParse(_settings.Current.Hotkeys.For(action), out var saved) && saved == gesture;

    private void RefreshHotkeyStatus() => _ = ValidateHotkeys().ToList();

    private void OnRegistrationsChanged(object? sender, EventArgs e) => RefreshHotkeyStatus();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Changing start-with-Windows failed")]
    private partial void LogStartupFailed(Exception ex);
}
