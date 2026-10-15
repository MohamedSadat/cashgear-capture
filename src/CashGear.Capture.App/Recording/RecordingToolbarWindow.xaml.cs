using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Wpf;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Interop.Native;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CashGear.Capture.App.Recording;

/// <summary>
/// The recording toolbar's state: elapsed time and sound level (refreshed a few times a second), what sound is
/// recorded, the microphone's mute, pause and stop.
/// </summary>
public sealed partial class RecordingToolbarViewModel : ObservableObject
{
    /// <summary>The level meter shows −60 dB (empty) to 0 dB (full).</summary>
    private const double MeterRangeDb = 60;

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
        Refresh();
    }

    [ObservableProperty]
    private string _elapsedText = "0:00";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRecordingNow), nameof(PauseText))]
    private bool _isPaused;

    /// <summary>The loudest recent sound, 0 (silence or below −60 dB) to 1.</summary>
    [ObservableProperty]
    private double _soundLevel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MicrophoneText))]
    private bool _microphoneMuted;

    /// <summary>Why sound that was asked for is missing (shown as the tooltip of a crossed-out speaker).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSoundMissing), nameof(SoundToolTip))]
    private string? _soundNote;

    public bool IsRecordingNow => !IsPaused;

    public string PauseText => IsPaused ? "Resume" : "Pause";

    public bool HasSound { get; }

    public bool HasMicrophone { get; }

    public bool IsSoundMissing => SoundNote is not null && !HasSound;

    public string MicrophoneText => MicrophoneMuted ? "Unmute mic" : "Mute mic";

    public string SoundToolTip => HasSound
        ? "Recording " + RecordingController.SoundText(_recorder.Sound) + (SoundNote is null ? string.Empty : ". " + SoundNote)
        : SoundNote ?? "No sound";

    /// <summary>A peak sample (0–1) as a meter position on a decibel scale.</summary>
    public static double MeterLevel(float peak) =>
        peak <= 0 ? 0 : Math.Clamp(1 + (20 * Math.Log10(peak) / MeterRangeDb), 0, 1);

    /// <summary>Reads the recorder's clock and state.</summary>
    public void Refresh()
    {
        ElapsedText = RecordingController.Duration(_recorder.Elapsed);
        IsPaused = _recorder.IsPaused;
        SoundLevel = HasSound ? MeterLevel(_recorder.TakeSoundLevel()) : 0;
        MicrophoneMuted = _recorder.MicrophoneMuted;
        SoundNote = _recorder.SoundNote;
    }

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

    public RecordingToolbarWindow(RecordingToolbarViewModel viewModel, AppTheme theme, PixelRect workArea)
    {
        InitializeComponent();
        DataContext = viewModel;
        _workArea = workArea;
        ApplyTheme(theme);
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => viewModel.Refresh(), Dispatcher);
        SourceInitialized += (_, _) =>
            User32.SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, User32.WdaExcludeFromCapture);
        Loaded += (_, _) =>
        {
            Place();
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
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
