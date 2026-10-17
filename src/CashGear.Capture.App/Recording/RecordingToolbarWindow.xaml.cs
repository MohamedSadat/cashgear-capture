using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Wpf;
using CashGear.Capture.Core.Audio;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Interop.Native;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CashGear.Capture.App.Recording;

/// <summary>
/// The recording toolbar's state: elapsed time (refreshed a few times a second) and a level meter per source
/// (twenty times a second), what sound is recorded, the microphone's mute, each source's mute and volume (the Sound
/// panel), pause and stop.
/// </summary>
public sealed partial class RecordingToolbarViewModel : ObservableObject
{
    private readonly MeterBallistics _mix = new();
    private readonly IScreenRecorder _recorder;
    private readonly Action _togglePause;
    private readonly Action _stop;

    public RecordingToolbarViewModel(IScreenRecorder recorder, Action togglePause, Action stop)
    {
        _recorder = recorder;
        _togglePause = togglePause;
        _stop = stop;
        var sound = recorder.Sound;
        HasSound = sound != RecordingSound.None;
        HasMicrophone = sound.HasFlag(RecordingSound.Microphone);
        HasSystemAudio = sound.HasFlag(RecordingSound.System);
        Refresh();
    }

    /// <summary>The highest volume the toolbar offers, in percent (settings.json allows up to 400).</summary>
    public const double MaxVolume = 200;

    [ObservableProperty]
    private string _elapsedText = "0:00";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRecordingNow), nameof(PauseText))]
    private bool _isPaused;

    /// <summary>A source, or the mix, clipped in the last two seconds.</summary>
    [ObservableProperty]
    private bool _isClipping;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MicrophoneText))]
    private bool _microphoneMuted;

    [ObservableProperty]
    private bool _systemAudioMuted;

    /// <summary>The Sound panel (mute and volume for each source) is open.</summary>
    [ObservableProperty]
    private bool _isSoundPanelOpen;

    /// <summary>Why sound that was asked for is missing (shown as the tooltip of a crossed-out speaker).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSoundMissing), nameof(SoundToolTip))]
    private string? _soundNote;

    public bool IsRecordingNow => !IsPaused;

    public string PauseText => IsPaused ? "Resume" : "Pause";

    public bool HasSound { get; }

    public bool HasMicrophone { get; }

    public bool HasSystemAudio { get; }

    /// <summary>System audio's level as recorded (after its volume and mute).</summary>
    public MeterReading SystemMeter { get; } = new();

    /// <summary>The microphone's level as recorded (after its processing, volume and mute).</summary>
    public MeterReading MicrophoneMeter { get; } = new();

    /// <summary>The microphone's volume in the recording, in percent of what it captures.</summary>
    public double MicrophoneVolume
    {
        get => Math.Round(_recorder.MicrophoneGain * 100);
        set
        {
            _recorder.MicrophoneGain = (float)(Math.Clamp(value, 0, MaxVolume) / 100);
            OnPropertyChanged();
            OnPropertyChanged(nameof(MicrophoneVolumeText));
        }
    }

    /// <summary>System audio's volume in the recording, in percent of what it captures.</summary>
    public double SystemAudioVolume
    {
        get => Math.Round(_recorder.SystemAudioGain * 100);
        set
        {
            _recorder.SystemAudioGain = (float)(Math.Clamp(value, 0, MaxVolume) / 100);
            OnPropertyChanged();
            OnPropertyChanged(nameof(SystemAudioVolumeText));
        }
    }

    public string MicrophoneVolumeText => $"{MicrophoneVolume:0}%";

    public string SystemAudioVolumeText => $"{SystemAudioVolume:0}%";

    public bool IsSoundMissing => SoundNote is not null && !HasSound;

    public string MicrophoneText => MicrophoneMuted ? "Unmute mic" : "Mute mic";

    public string SoundToolTip => HasSound
        ? "Recording " + RecordingController.SoundText(_recorder.Sound) + (SoundNote is null ? string.Empty : ". " + SoundNote)
        : SoundNote ?? "No sound";

    /// <summary>Reads the recorder's clock and state.</summary>
    public void Refresh()
    {
        ElapsedText = RecordingController.Duration(_recorder.Elapsed);
        IsPaused = _recorder.IsPaused;
        MicrophoneMuted = _recorder.MicrophoneMuted;
        SystemAudioMuted = _recorder.SystemAudioMuted;
        SoundNote = _recorder.SoundNote;
    }

    /// <summary>Moves the meters with the sound recorded since the last call (<paramref name="now"/>: a steady clock).</summary>
    public void RefreshMeters(TimeSpan now)
    {
        if (!HasSound)
        {
            return;
        }

        var levels = _recorder.TakeSoundLevels();
        SystemMeter.Update(levels.System, now);
        MicrophoneMeter.Update(levels.Microphone, now);
        _mix.Update(levels.Mix, now);
        IsClipping = SystemMeter.Clipping || MicrophoneMeter.Clipping || _mix.Clipping;
    }

    partial void OnMicrophoneMutedChanged(bool value) => _recorder.MicrophoneMuted = value;

    partial void OnSystemAudioMutedChanged(bool value) => _recorder.SystemAudioMuted = value;

    [RelayCommand]
    private void Pause()
    {
        _togglePause();
        Refresh();
    }

    [RelayCommand]
    private void ToggleMicrophone()
    {
        _recorder.MicrophoneMuted = !_recorder.MicrophoneMuted;
        Refresh();
    }

    [RelayCommand]
    private void ToggleSoundPanel() => IsSoundPanelOpen = !IsSoundPanelOpen;

    [RelayCommand]
    private void Stop() => _stop();
}

/// <summary>
/// The small bar shown while recording. It is left out of the recording itself (and of screenshots), never takes
/// the focus, stays on top at the top centre of the recorded monitor, and can be dragged out of the way.
/// </summary>
public partial class RecordingToolbarWindow : Window, IThemedWindow
{
    private const int EdgeMargin = 8;

    private readonly PixelRect _workArea;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _meters;

    public RecordingToolbarWindow(RecordingToolbarViewModel viewModel, AppTheme theme, PixelRect workArea)
    {
        InitializeComponent();
        DataContext = viewModel;
        _workArea = workArea;
        ApplyTheme(theme);
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => viewModel.Refresh(), Dispatcher);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        _meters = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) => viewModel.RefreshMeters(clock.Elapsed), Dispatcher);
        SourceInitialized += (_, _) =>
            User32.SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, User32.WdaExcludeFromCapture);
        Loaded += (_, _) =>
        {
            Place();
            _timer.Start();
            if (viewModel.HasSound)
            {
                _meters.Start();
            }
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            _meters.Stop();
        };
    }

    public void ApplyTheme(AppTheme theme) => Theming.Apply(this, theme);

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    /// <summary>Top centre of the monitor's work area (physical pixels, whatever the monitor's scale).</summary>
    private void Place()
    {
        var scale = DpiHelper.GetScale(this);
        var width = (int)Math.Ceiling(ActualWidth * scale.ScaleX);
        var x = _workArea.X + ((_workArea.Width - width) / 2);
        User32.SetWindowPos(
            new WindowInteropHelper(this).Handle, 0, x, _workArea.Y + EdgeMargin, 0, 0, User32.SwpNoActivate | 0x0001 /* NOSIZE */ | 0x0004 /* NOZORDER */);
    }
}
