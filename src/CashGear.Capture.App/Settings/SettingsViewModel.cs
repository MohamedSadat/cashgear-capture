using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CashGear.Capture.App.Hotkeys;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Wpf;
using CashGear.Capture.Core.Audio;
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
    private readonly UpdateService? _updates;
    private readonly IRecordingEncoders? _encoders;
    private readonly IScreenRecorder? _recorder;
    private readonly IAudioApps? _apps;
    private readonly IAudioMonitor? _monitor;
    private readonly SynchronizationContext? _context;
    private Task _monitorWork = Task.CompletedTask;
    private bool _monitoring;
    private float[]? _testRecording;
    private CancellationTokenSource? _playback;
    private IDisposable? _hotkeySuspension;

    public SettingsViewModel(
        ISettingsService settings,
        AppPaths paths,
        HotkeyRegistrar hotkeys,
        IStartupRegistration startup,
        IFolderPicker folderPicker,
        ILogger<SettingsViewModel> logger,
        IRecordingEncoders? encoders = null,
        IOcrService? ocr = null,
        UpdateService? updates = null,
        IAudioDevices? audio = null,
        IScreenRecorder? recorder = null,
        IAudioApps? apps = null,
        IAudioMonitor? monitor = null,
        IReadOnlyList<string>? fonts = null)
    {
        _apps = apps;
        _monitor = monitor;
        Overlays = new OverlaySettingsViewModel(apps, fonts ?? []);
        IsAppAudioSupported = apps?.IsSupported == true;
        SystemAudioScopes = IsAppAudioSupported ? Enum.GetValues<SystemAudioScope>() : [SystemAudioScope.All];
        _updates = updates;
        _encoders = encoders;
        _recorder = recorder;
        _context = SynchronizationContext.Current;
        OcrLanguages = [new OcrLanguageChoice(null, "Automatic (your Windows languages)"),
            .. (ocr?.AvailableLanguages ?? []).Select(l => new OcrLanguageChoice(l.Tag, $"{l.DisplayName} — {l.NativeName}"))];
        FfmpegStatus = encoders is null ? "Screen recording isn't available."
            : encoders.Problem is { } problem ? "Screen recording isn't available. " + problem
            : $"FFmpeg {encoders.FfmpegVersion}, built into the app (LGPL; see THIRD-PARTY-NOTICES.txt).";
        IsFfmpegMissing = encoders is null || encoders.Problem is not null;
        Microphones =
        [
            new DeviceChoice(null, "None"),
            new DeviceChoice(RecordingSettings.DefaultMicrophone, "Default microphone (Windows setting)"),
            .. (audio?.Microphones() ?? []).Select(m => new DeviceChoice(m.Id, m.Name)),
        ];
        OutputDevices =
        [
            new DeviceChoice(null, "Default output device (follows Windows)"),
            .. (audio?.Outputs() ?? []).Select(d => new DeviceChoice(d.Id, d.Name)),
        ];
        SpeakersStatus = audio?.DefaultOutput() is { } speakers
            ? $"The default output device is {speakers.Name} now. If another becomes the default during a recording, system audio follows it; a device that is unplugged is recorded again when it is back."
            : "No sound output device was found; system audio is silent until one is connected.";
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
        UpdateStatus = updates?.StatusText ?? string.Empty;
        if (updates is not null)
        {
            updates.StateChanged += OnUpdateStateChanged;
        }
    }

    /// <summary>Settings › Overlays.</summary>
    public OverlaySettingsViewModel Overlays { get; }

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

    /// <summary>How many captures the history keeps (a value set by hand in settings.json is added).</summary>
    public ObservableCollection<CountChoice> HistorySizes { get; } =
        [new(0, "Off (keep none)"), new(10, "10 captures"), new(20, "20 captures"), new(50, "50 captures"), new(100, "100 captures")];

    /// <summary>E.g. "Version 1.0.0".</summary>
    public string VersionText => "Version " + BuildInfo.Version;

    /// <summary>Whether this copy can check for updates (installed, with an update address).</summary>
    public bool CanCheckForUpdates => _updates?.IsAvailable == true;

    public IReadOnlyList<int> ScrollSettleChoices { get; } = [100, 250, 500, 1000];

    public IReadOnlyList<int> FrameRates { get; } = RecordingSettings.FrameRates;

    public IReadOnlyList<QualityPreset> QualityPresets { get; } = Enum.GetValues<QualityPreset>();

    public IReadOnlyList<AudioTrackLayout> TrackLayouts { get; } = Enum.GetValues<AudioTrackLayout>();

    /// <summary>Automatic, software only, and every encoder the app knows, with what detection found about each.</summary>
    public ObservableCollection<EncoderChoice> Encoders { get; } = [];

    /// <summary>Which encoders work here, or that they are being checked.</summary>
    [ObservableProperty]
    private string _encoderStatus = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RedetectEncodersCommand))]
    private bool _isDetectingEncoders;

    /// <summary>"Automatic" and each language Windows can recognize.</summary>
    public IReadOnlyList<OcrLanguageChoice> OcrLanguages { get; }

    /// <summary>The FFmpeg in use, or why recording is unavailable.</summary>
    public string FfmpegStatus { get; }

    /// <summary>None, the Windows default and each microphone connected (plus a saved one that isn't).</summary>
    public ObservableCollection<DeviceChoice> Microphones { get; }

    /// <summary>Windows' default output device, and each one connected (plus a saved one that isn't).</summary>
    public ObservableCollection<DeviceChoice> OutputDevices { get; }

    /// <summary>Which device system audio comes from by default, and how devices are followed.</summary>
    public string SpeakersStatus { get; }

    /// <summary>All sound, and (where Windows can) only one app or all but one app.</summary>
    public IReadOnlyList<SystemAudioScope> SystemAudioScopes { get; }

    /// <summary>Windows can record one app's sound by itself (Windows 10 2004 and later).</summary>
    public bool IsAppAudioSupported { get; }

    /// <summary>The apps running now (plus the one chosen before, if it isn't), for recording one app's sound.</summary>
    public ObservableCollection<AppChoice> Apps { get; } = [];

    /// <summary>The app list applies: system audio is on and limited to (or excludes) one app.</summary>
    public bool IsAppChoiceEnabled => RecordSystemAudio && RecordingSystemAudioScope != SystemAudioScope.All;

    /// <summary>The output device list applies: system audio is on and records all sound.</summary>
    public bool IsOutputChoiceEnabled => RecordSystemAudio && RecordingSystemAudioScope == SystemAudioScope.All;

    public bool IsFfmpegMissing { get; }

    /// <summary>Lists the encoder choices with what detection found, keeping <paramref name="selectedId"/> selected (or the current choice).</summary>
    private void RefreshEncoders(string? selectedId = null)
    {
        selectedId ??= RecordingEncoder?.Id ?? EncoderPresets.Auto;
        var detected = _encoders?.Detected;
        Encoders.Clear();
        Encoders.Add(new EncoderChoice(EncoderPresets.Auto, "Automatic: the best that works here (recommended)"));
        Encoders.Add(new EncoderChoice(EncoderPresets.Software, "Software only (uses the CPU)"));
        foreach (var candidate in EncoderPresets.All)
        {
            var probe = detected?.FirstOrDefault(p => p.Id == candidate.Id);
            var label = probe is null ? candidate.DisplayName
                : !probe.Works ? candidate.DisplayName + " (not available here)"
                : candidate.FfmpegName is "h264_mf" or "hevc_mf" && candidate.Hardware && probe.Detail is { } device ? $"{candidate.DisplayName}: {device}"
                : candidate.DisplayName;
            Encoders.Add(new EncoderChoice(candidate.Id, label));
        }

        RecordingEncoder = Encoders.FirstOrDefault(e => string.Equals(e.Id, selectedId, StringComparison.OrdinalIgnoreCase));
        if (RecordingEncoder is null)
        {
            // An id set by hand that this version doesn't know stays (and is kept) rather than silently becoming Automatic.
            RecordingEncoder = new EncoderChoice(selectedId, selectedId + " (unknown)");
            Encoders.Add(RecordingEncoder);
        }

        if (_encoders is null || _encoders.Problem is not null)
        {
            EncoderStatus = string.Empty;
        }
        else if (detected is null || IsDetectingEncoders)
        {
            EncoderStatus = "Checking which encoders work on this PC…";
            if (detected is null && !IsDetectingEncoders)
            {
                _ = DetectEncodersAsync(force: false);
            }
        }
        else
        {
            var working = EncoderPresets.All.Where(c => detected.Any(p => p.Id == c.Id && p.Works)).Select(c => c.DisplayName).ToList();
            EncoderStatus = working.Count == 0
                ? "No encoder works on this PC, so recording is unavailable."
                : "Works on this PC: " + string.Join(", ", working) + ".";
        }
    }

    /// <summary>Lists the running apps again, keeping the chosen one.</summary>
    [RelayCommand]
    private void RefreshApps() => RefreshApps(RecordingApp?.Exe, RecordingApp?.Name);

    /// <summary>Lists the apps running now and selects <paramref name="exe"/>; an app that isn't running stays listed and chosen.</summary>
    private void RefreshApps(string? exe, string? name)
    {
        Apps.Clear();
        foreach (var app in _apps?.List() ?? [])
        {
            Apps.Add(new AppChoice(app.Exe, app.Name, app.PlayingSound ? $"{app.Name} (playing sound)" : app.Name));
        }

        RecordingApp = exe is null ? null : Apps.FirstOrDefault(a => string.Equals(a.Exe, exe, StringComparison.OrdinalIgnoreCase));
        if (RecordingApp is null && exe is not null)
        {
            RecordingApp = new AppChoice(exe, name ?? exe, $"{name ?? exe} (not running)");
            Apps.Insert(0, RecordingApp);
        }
    }

    partial void OnRecordingSystemAudioScopeChanged(SystemAudioScope value)
    {
        Revalidate();
        RestartMonitor();
    }

    partial void OnRecordingAppChanged(AppChoice? value)
    {
        Revalidate();
        RestartMonitor();
    }

    partial void OnRecordSystemAudioChanged(bool value)
    {
        Revalidate();
        RestartMonitor();
    }

    private bool CanRedetectEncoders() => _encoders is { Problem: null } && !IsDetectingEncoders && _recorder?.IsRecording != true;

    /// <summary>Tests every encoder again (after a driver update, say); the list shows the new results.</summary>
    [RelayCommand(CanExecute = nameof(CanRedetectEncoders))]
    private Task RedetectEncodersAsync() => DetectEncodersAsync(force: true);

    private async Task DetectEncodersAsync(bool force)
    {
        IsDetectingEncoders = true;
        EncoderStatus = "Checking which encoders work on this PC…";
        try
        {
            await Task.Run(() => _encoders!.DetectAsync(force)).ConfigureAwait(true);
        }
        finally
        {
            IsDetectingEncoders = false;
            RefreshEncoders();
        }
    }

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

    [ObservableProperty]
    private QualityPreset _recordingQuality;

    [ObservableProperty]
    private AudioTrackLayout _recordingTracks;

    [ObservableProperty]
    private bool _recordingIncludeCursor;

    [ObservableProperty]
    private EncoderChoice? _recordingEncoder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAppChoiceEnabled), nameof(IsOutputChoiceEnabled))]
    private bool _recordSystemAudio;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAppChoiceEnabled), nameof(IsOutputChoiceEnabled))]
    private SystemAudioScope _recordingSystemAudioScope;

    /// <summary>The app whose sound is recorded by itself (or left out).</summary>
    [ObservableProperty]
    private AppChoice? _recordingApp;

    /// <summary>RNNoise on the microphone.</summary>
    [ObservableProperty]
    private bool _microphoneNoiseSuppression;

    /// <summary>How much of the noise is taken out (percent).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoiseSuppressionStrengthText))]
    private double _noiseSuppressionStrength;

    public string NoiseSuppressionStrengthText => $"{NoiseSuppressionStrength:0}%";

    /// <summary>RNNoise is there, so noise suppression can be offered.</summary>
    public bool CanSuppressNoise => _monitor?.CanSuppressNoise ?? false;

    /// <summary>Silence the microphone between words.</summary>
    [ObservableProperty]
    private bool _microphoneGate;

    /// <summary>The level (dBFS) above which the gate opens.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GateThresholdText))]
    private double _gateThresholdDb;

    /// <summary>Even out loud and quiet speech.</summary>
    [ObservableProperty]
    private bool _microphoneCompressor;

    /// <summary>Never let the microphone clip.</summary>
    [ObservableProperty]
    private bool _microphoneLimiter;

    /// <summary>Even out each sound track to −16 LUFS when a recording is saved.</summary>
    [ObservableProperty]
    private bool _normalizeLoudness;

    /// <summary>Push-to-talk's key as text (empty: none).</summary>
    [ObservableProperty]
    private string _pushToTalkText = string.Empty;

    /// <summary>"Not set", "Hold it while recording", or why it doesn't work.</summary>
    [ObservableProperty]
    private string _pushToTalkStatus = string.Empty;

    [ObservableProperty]
    private bool _pushToTalkHasProblem;

    [RelayCommand]
    private void ClearPushToTalk() => PushToTalkText = string.Empty;

    partial void OnPushToTalkTextChanged(string value) => Revalidate();

    /// <summary>Checks push-to-talk's key against the hotkeys (one key can't do both); returns the problem, if any.</summary>
    private string? ValidatePushToTalk()
    {
        if (string.IsNullOrWhiteSpace(PushToTalkText))
        {
            (PushToTalkStatus, PushToTalkHasProblem) = ("Not set: the microphone is always recorded", false);
            return null;
        }

        if (!HotkeyGesture.TryParse(PushToTalkText, out var key))
        {
            (PushToTalkStatus, PushToTalkHasProblem) = ("Not a valid key", true);
            return $"Push to talk: \"{PushToTalkText}\" is not a valid key.";
        }

        var clash = Hotkeys.FirstOrDefault(r => HotkeyGesture.TryParse(r.SettingText, out var other) && other == key);
        if (clash is not null)
        {
            (PushToTalkStatus, PushToTalkHasProblem) = ($"Same as {clash.Label}", true);
            return $"Push to talk: {key} is already the hotkey for {clash.Label}.";
        }

        (PushToTalkStatus, PushToTalkHasProblem) = ("Hold it while recording", false);
        return null;
    }

    public string GateThresholdText => $"{GateThresholdDb:0} dB";

    public double MinGateThresholdDb => MicrophoneProcessing.MinGateThresholdDb;

    public double MaxGateThresholdDb => MicrophoneProcessing.MaxGateThresholdDb;

    /// <summary>The microphone's processing applies: a microphone is chosen.</summary>
    public bool HasMicrophone => RecordingMicrophone?.Id is not null;

    /// <summary>The microphone's level, as it would be recorded (after the processing).</summary>
    public MeterReading MicrophoneLevel { get; } = new();

    /// <summary>System audio's level, as it would be recorded.</summary>
    public MeterReading SystemAudioLevel { get; } = new();

    /// <summary>Settings can listen to the devices (meters and the microphone test).</summary>
    public bool CanMonitor => _monitor is not null;

    /// <summary>Why a meter is silent (no device, app not running), or empty.</summary>
    [ObservableProperty]
    private string _monitorStatus = string.Empty;

    [ObservableProperty]
    private string _microphoneTestStatus = "Records 5 seconds of the microphone, then plays them back as recorded or as processed.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestMicrophoneCommand), nameof(PlayOriginalCommand), nameof(PlayProcessedCommand))]
    private bool _isTestingMicrophone;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestMicrophoneCommand), nameof(PlayOriginalCommand), nameof(PlayProcessedCommand), nameof(StopPlaybackCommand))]
    private bool _isPlayingTest;

    /// <summary>The work of opening and closing the monitored devices, done one after another off the UI thread (for tests).</summary>
    internal Task MonitorWork => _monitorWork;

    /// <summary>Starts the meters (the Recording page is shown).</summary>
    public void StartMonitoring()
    {
        _monitoring = true;
        RestartMonitor();
    }

    /// <summary>Stops the meters and closes the devices (another page, or the window closes).</summary>
    public void StopMonitoring()
    {
        _monitoring = false;
        if (_monitor is { } monitor)
        {
            Queue(monitor.Stop);
        }

        MicrophoneLevel.Reset();
        SystemAudioLevel.Reset();
    }

    /// <summary>Moves the meters with the sound heard since the last call (<paramref name="now"/>: a steady clock).</summary>
    public void RefreshMeters(TimeSpan now)
    {
        if (!_monitoring || _monitor is null)
        {
            return;
        }

        var levels = _monitor.TakeLevels();
        MicrophoneLevel.Update(levels.Microphone, now);
        SystemAudioLevel.Update(levels.System, now);
        MonitorStatus = _monitor.Problem ?? string.Empty;
    }

    /// <summary>The processing as set on this page now (what a recording would use, and what the test plays).</summary>
    private MicrophoneProcessing CurrentProcessing() => (_settings.Current.Recording.MicrophoneProcessing ?? new MicrophoneProcessing()) with
    {
        NoiseSuppression = MicrophoneNoiseSuppression,
        NoiseSuppressionStrength = (int)Math.Round(NoiseSuppressionStrength),
        NoiseGate = MicrophoneGate,
        GateThresholdDb = (int)Math.Round(GateThresholdDb),
        Compressor = MicrophoneCompressor,
        Limiter = MicrophoneLimiter,
    };

    /// <summary>Listens to what a recording would record with this page's choices (when the meters run).</summary>
    private void RestartMonitor()
    {
        if (!_monitoring || _monitor is not { } monitor)
        {
            return;
        }

        var request = new MonitorRequest(
            RecordingMicrophone?.Id, CurrentProcessing(), RecordSystemAudio, RecordingOutput?.Id, RecordingSystemAudioScope, RecordingApp?.Exe, RecordingApp?.Name);
        Queue(() => monitor.Start(request));
    }

    /// <summary>Opening devices can take a moment (and an app's sound must not be activated on the UI thread): one at a time, in the background.</summary>
    private void Queue(Action work) =>
        _monitorWork = _monitorWork.ContinueWith(_ => work(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

    private void UpdateMonitorProcessing()
    {
        if (_monitoring)
        {
            _monitor?.SetProcessing(CurrentProcessing());
        }
    }

    partial void OnMicrophoneNoiseSuppressionChanged(bool value) => UpdateMonitorProcessing();

    partial void OnNoiseSuppressionStrengthChanged(double value) => UpdateMonitorProcessing();

    partial void OnMicrophoneGateChanged(bool value) => UpdateMonitorProcessing();

    partial void OnGateThresholdDbChanged(double value) => UpdateMonitorProcessing();

    partial void OnMicrophoneCompressorChanged(bool value) => UpdateMonitorProcessing();

    partial void OnMicrophoneLimiterChanged(bool value) => UpdateMonitorProcessing();

    partial void OnRecordingMicrophoneChanged(DeviceChoice? value)
    {
        RestartMonitor();
        TestMicrophoneCommand.NotifyCanExecuteChanged();
    }

    partial void OnRecordingOutputChanged(DeviceChoice? value) => RestartMonitor();

    private bool CanTestMicrophone() => _monitor is not null && HasMicrophone && !IsTestingMicrophone && !IsPlayingTest;

    /// <summary>Records 5 s of the chosen microphone, as captured.</summary>
    [RelayCommand(CanExecute = nameof(CanTestMicrophone))]
    private async Task TestMicrophoneAsync()
    {
        IsTestingMicrophone = true;
        MicrophoneTestStatus = "Recording 5 seconds: say something, and let the room be heard too…";
        try
        {
            _testRecording = await _monitor!.RecordMicrophoneAsync(RecordingMicrophone!.Id!, TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            MicrophoneTestStatus = "Recorded. Play it as recorded, or with the processing above; change the processing and play again to compare.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.Runtime.InteropServices.COMException)
        {
            MicrophoneTestStatus = "The microphone couldn't be recorded: " + ex.Message;
        }
        finally
        {
            IsTestingMicrophone = false;
            PlayOriginalCommand.NotifyCanExecuteChanged();
            PlayProcessedCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanPlayTest() => _testRecording is not null && !IsTestingMicrophone && !IsPlayingTest;

    [RelayCommand(CanExecute = nameof(CanPlayTest))]
    private Task PlayOriginalAsync() => PlayTestAsync(processed: false);

    [RelayCommand(CanExecute = nameof(CanPlayTest))]
    private Task PlayProcessedAsync() => PlayTestAsync(processed: true);

    [RelayCommand(CanExecute = nameof(IsPlayingTest))]
    private void StopPlayback() => _playback?.Cancel();

    private async Task PlayTestAsync(bool processed)
    {
        var recording = _testRecording!;
        IsPlayingTest = true;
        using var playback = new CancellationTokenSource();
        _playback = playback;
        try
        {
            var samples = processed
                ? await Task.Run(() => _monitor!.Process(recording, CurrentProcessing())).ConfigureAwait(true)
                : recording;
            MicrophoneTestStatus = processed ? "Playing with the processing…" : "Playing as recorded…";
            await _monitor!.PlayAsync(samples, playback.Token).ConfigureAwait(true);
            MicrophoneTestStatus = "Played. Play again, or record a new test.";
        }
        catch (OperationCanceledException)
        {
            MicrophoneTestStatus = "Stopped.";
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            MicrophoneTestStatus = "It couldn't be played: " + ex.Message.Trim();
        }
        finally
        {
            _playback = null;
            IsPlayingTest = false;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMicrophone))]
    private DeviceChoice? _recordingMicrophone;

    /// <summary>The output device whose sound is recorded (Id null: Windows' default).</summary>
    [ObservableProperty]
    private DeviceChoice? _recordingOutput;

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

    [ObservableProperty]
    private CountChoice? _historySize;

    [ObservableProperty]
    private bool _checkForUpdates;

    /// <summary>What the last update check found, or why updates are unavailable.</summary>
    [ObservableProperty]
    private string _updateStatus = string.Empty;

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
        StopMonitoring();
        _playback?.Cancel();
        EndHotkeyRecording();
        _hotkeys.RegistrationsChanged -= OnRegistrationsChanged;
        if (_updates is not null)
        {
            _updates.StateChanged -= OnUpdateStateChanged;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
    private Task CheckForUpdatesNowAsync() => _updates?.CheckAsync(userAsked: true) ?? Task.CompletedTask;

    private void OnUpdateStateChanged(object? sender, EventArgs e)
    {
        if (_context is { } context)
        {
            context.Post(_ => UpdateStatus = _updates?.StatusText ?? string.Empty, null);
        }
        else
        {
            UpdateStatus = _updates?.StatusText ?? string.Empty;
        }
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
        Recording = current.Recording with
        {
            Fps = RecordingFps,
            Quality = RecordingQuality,
            IncludeCursor = RecordingIncludeCursor,
            Encoder = RecordingEncoder?.Id ?? EncoderPresets.Auto,
            SystemAudio = RecordSystemAudio,
            SystemAudioDevice = RecordingOutput?.Id,
            SystemAudioScope = RecordingSystemAudioScope,
            SystemAudioApp = RecordingApp?.Exe,
            SystemAudioAppName = RecordingApp?.Name,
            NormalizeLoudness = NormalizeLoudness,
            PushToTalk = string.IsNullOrWhiteSpace(PushToTalkText) ? null : PushToTalkText.Trim(),
            MicrophoneProcessing = (current.Recording.MicrophoneProcessing ?? new MicrophoneProcessing()) with
            {
                NoiseSuppression = MicrophoneNoiseSuppression,
                NoiseSuppressionStrength = (int)Math.Round(NoiseSuppressionStrength),
                NoiseGate = MicrophoneGate,
                GateThresholdDb = (int)Math.Round(GateThresholdDb),
                Compressor = MicrophoneCompressor,
                Limiter = MicrophoneLimiter,
            },
            Microphone = RecordingMicrophone?.Id,
            Tracks = RecordingTracks,
        },
        Overlays = Overlays.Build(current.Overlays),
        Ocr = new OcrSettings(OcrLanguage?.Tag),
        History = new HistorySettings(HistorySize?.Value ?? current.History.MaxItems),
        Updates = current.Updates with { CheckAutomatically = CheckForUpdates },
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
        RecordingTracks = s.Recording.Tracks;
        RecordingIncludeCursor = s.Recording.IncludeCursor;
        Overlays.Load(s.Overlays);
        RefreshEncoders(s.Recording.Encoder);
        RecordSystemAudio = s.Recording.SystemAudio;
        // A microphone that isn't connected now stays selected (and saved) rather than silently becoming None.
        RecordingMicrophone = Microphones.FirstOrDefault(m => string.Equals(m.Id, s.Recording.Microphone, StringComparison.OrdinalIgnoreCase));
        if (RecordingMicrophone is null)
        {
            RecordingMicrophone = new DeviceChoice(s.Recording.Microphone, "The microphone chosen before (not connected)");
            Microphones.Add(RecordingMicrophone);
        }

        var processing = s.Recording.MicrophoneProcessing ?? new MicrophoneProcessing();
        MicrophoneNoiseSuppression = processing.NoiseSuppression;
        NoiseSuppressionStrength = processing.NoiseSuppressionStrength;
        MicrophoneGate = processing.NoiseGate;
        GateThresholdDb = processing.GateThresholdDb;
        MicrophoneCompressor = processing.Compressor;
        MicrophoneLimiter = processing.Limiter;
        NormalizeLoudness = s.Recording.NormalizeLoudness;
        PushToTalkText = s.Recording.PushToTalk ?? string.Empty;
        RecordingSystemAudioScope = SystemAudioScopes.Contains(s.Recording.SystemAudioScope) ? s.Recording.SystemAudioScope : SystemAudioScope.All;
        RefreshApps(s.Recording.SystemAudioApp, s.Recording.SystemAudioAppName);
        RecordingOutput = OutputDevices.FirstOrDefault(d => string.Equals(d.Id, s.Recording.SystemAudioDevice, StringComparison.OrdinalIgnoreCase));
        if (RecordingOutput is null)
        {
            RecordingOutput = new DeviceChoice(s.Recording.SystemAudioDevice, "The output device chosen before (not connected)");
            OutputDevices.Add(RecordingOutput);
        }

        ScrollMaxHeight = s.Capture.ScrollMaxHeight;
        IncludeCursor = s.Capture.IncludeCursor;
        IncludeWindowShadow = s.Capture.IncludeWindowShadow;
        Backend = s.Capture.Backend;
        FullScreenOutput = s.Capture.FullScreenOutput;
        StartWithWindows = startWithWindows;
        Theme = s.Appearance.Theme;
        HistorySize = HistorySizes.FirstOrDefault(c => c.Value == s.History.MaxItems);
        if (HistorySize is null)
        {
            HistorySize = new CountChoice(s.History.MaxItems, $"{s.History.MaxItems} captures");
            HistorySizes.Add(HistorySize);
        }

        CheckForUpdates = s.Updates.CheckAutomatically;
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

        if (IsAppChoiceEnabled && RecordingApp is null)
        {
            errors.Add("Sound: choose the app whose sound is recorded (or left out), or record all sound.");
        }

        errors.AddRange(ValidateHotkeys());
        if (ValidatePushToTalk() is { } talk)
        {
            errors.Add(talk);
        }

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
