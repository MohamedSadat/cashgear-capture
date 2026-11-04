using CashGear.Capture.App.Resources;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CashGear.Capture.App.Recording;

/// <summary>Something a recording can be set to record without asking.</summary>
public sealed record ScheduleTarget(string Label, RecordingAreaKind Kind, string? Monitor = null, string? WindowTitle = null, long WindowHandle = 0)
{
    public override string ToString() => Label;
}

/// <summary>A profile to record with, or the settings as they are (<see cref="Id"/> null).</summary>
public sealed record ScheduleProfile(string Label, string? Id)
{
    public override string ToString() => Label;
}

/// <summary>How a recording stops, as the window offers it.</summary>
public enum ScheduleStop
{
    ByHand,
    AfterLength,
    AtTime,
    WhenAppCloses,
}

/// <summary>
/// The window that sets a recording to start at a time and to stop by itself: after a length, at a time, or when an app
/// closes. What it records is chosen beforehand, as nobody may be there to pick an area when it starts.
/// </summary>
public sealed partial class ScheduleRecordingViewModel : ObservableObject
{
    private readonly RecordingController _recording;
    private readonly RecordingScheduler _scheduler;
    private readonly TimeProvider _time;

    public ScheduleRecordingViewModel(
        RecordingController recording,
        RecordingScheduler scheduler,
        IMonitorService monitors,
        IWindowService windows,
        ISettingsService settings,
        TimeProvider time,
        bool pro)
    {
        _recording = recording;
        _scheduler = scheduler;
        _time = time;
        var displays = monitors.GetMonitors().OrderBy(m => !m.IsPrimary).ToList();
        var targets = displays.Select((m, i) => new ScheduleTarget(RecordingText.TargetMonitor(i + 1, m.ShortName), RecordingAreaKind.Monitor, m.DeviceName)).ToList();
        if (pro && displays.Count > 1)
        {
            targets.Add(new ScheduleTarget(RecordingText.TargetAllMonitors, RecordingAreaKind.AllMonitors));
        }

        targets.AddRange(windows.GetVisibleWindows()
            .Where(w => w.ProcessId != Environment.ProcessId && w.Title.Length > 0 && !w.IsMinimized)
            .Select(w => new ScheduleTarget(RecordingText.TargetWindow(w.Title), RecordingAreaKind.Window, WindowTitle: w.Title, WindowHandle: w.Handle)));
        Targets = targets;
        _target = targets.FirstOrDefault() ?? new ScheduleTarget(string.Empty, RecordingAreaKind.Monitor);
        Profiles = [new ScheduleProfile(RecordingText.AsSettingsAre, null), .. pro ? (settings.Current.RecordingProfiles.Profiles ?? []).Select(p => new ScheduleProfile(p.ShownName, p.Id)) : []];
        _profile = Profiles[0];
        _startTime = time.GetLocalNow().AddMinutes(5).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        _stopTime = time.GetLocalNow().AddMinutes(35).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    }

    public IReadOnlyList<ScheduleTarget> Targets { get; }

    public IReadOnlyList<ScheduleProfile> Profiles { get; }

    public IReadOnlyList<ScheduleStop> Stops { get; } = Enum.GetValues<ScheduleStop>();

    /// <summary>Called when the recording is set or started, to close the window.</summary>
    public Action? Done { get; set; }

    [ObservableProperty]
    private ScheduleTarget _target;

    [ObservableProperty]
    private ScheduleProfile _profile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionText))]
    private bool _startLater = true;

    [ObservableProperty]
    private string _startTime;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StopsAfterLength))]
    [NotifyPropertyChangedFor(nameof(StopsAtTime))]
    [NotifyPropertyChangedFor(nameof(StopsWhenAppCloses))]
    private ScheduleStop _stop = ScheduleStop.AfterLength;

    [ObservableProperty]
    private string _length = "00:30:00";

    [ObservableProperty]
    private string _stopTime;

    [ObservableProperty]
    private string _app = string.Empty;

    /// <summary>What is wrong with what was typed, or null.</summary>
    [ObservableProperty]
    private string? _problem;

    public bool StopsAfterLength => Stop == ScheduleStop.AfterLength;

    public bool StopsAtTime => Stop == ScheduleStop.AtTime;

    public bool StopsWhenAppCloses => Stop == ScheduleStop.WhenAppCloses;

    /// <summary>The main button: it schedules, or it records now.</summary>
    public string ActionText => StartLater ? RecordingText.ScheduleButton : RecordingText.RecordNowButton;

    /// <summary>The plan as the window says, or null with <see cref="Problem"/> saying why.</summary>
    public RecordingPlan? MakePlan()
    {
        Problem = null;
        var now = _time.GetLocalNow();
        DateTimeOffset? start = null;
        if (StartLater)
        {
            if (!RecordingCommandLine.TryMoment(StartTime.Trim(), now, out var at))
            {
                Problem = RecordingText.BadTime(StartTime);
                return null;
            }

            start = at;
        }

        RecordingStop? stop = null;
        switch (Stop)
        {
            case ScheduleStop.AfterLength:
                if (!RecordingCommandLine.TryLength(Length.Trim(), out var length))
                {
                    Problem = RecordingText.BadLength(Length);
                    return null;
                }

                stop = new RecordingStop(After: length);
                break;
            case ScheduleStop.AtTime:
                if (!RecordingCommandLine.TryMoment(StopTime.Trim(), start ?? now, out var end))
                {
                    Problem = RecordingText.BadTime(StopTime);
                    return null;
                }

                stop = new RecordingStop(At: end);
                break;
            case ScheduleStop.WhenAppCloses:
                if (string.IsNullOrWhiteSpace(App))
                {
                    Problem = RecordingText.NoAppGiven;
                    return null;
                }

                stop = new RecordingStop(AppClosed: App.Trim());
                break;
        }

        return new RecordingPlan(Target.Kind, Profile.Id, start, stop, Target.Monitor, Target.WindowTitle, Target.WindowHandle);
    }

    [RelayCommand]
    private async Task GoAsync()
    {
        if (MakePlan() is not { } plan)
        {
            return;
        }

        Done?.Invoke();
        if (plan.StartAt is null)
        {
            await _recording.StartAsync(plan);
        }
        else
        {
            await _scheduler.ScheduleAsync(plan);
        }
    }
}
