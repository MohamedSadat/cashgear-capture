using System.IO;
using CashGear.Capture.App.Resources;
using CashGear.Capture.App.Tray;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.App.Recording;

/// <summary>
/// The recording waiting for its time: one at a time, kept through a restart. It looks at the clock every 200 ms rather
/// than sleeping until then, so a PC that was asleep or a clock that was changed doesn't throw it off. Everything is
/// made ready <see cref="RecordingPlan.LeadTime"/> ahead, and the recorder starts its clock on the second. Found more
/// than <see cref="RecordingPlan.TooLate"/> late, it isn't made, and the user is told.
/// </summary>
public sealed partial class RecordingScheduler(
    RecordingController recording,
    IAppStateStore state,
    ITrayNotifier notifier,
    TimeProvider time,
    ILogger<RecordingScheduler> logger) : IDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(200);

    private readonly object _gate = new();
    private CancellationTokenSource? _waiting;

    /// <summary>What is waiting, or null.</summary>
    public RecordingPlan? Pending { get; private set; }

    /// <summary>A recording was scheduled, cancelled or started (raised on any thread).</summary>
    public event EventHandler? Changed;

    /// <summary>Picks up what was waiting when the app last closed.</summary>
    public void Resume()
    {
        if (state.Current.ScheduledRecording is not { } plan)
        {
            return;
        }

        if (plan.StartAt is not { } at || RecordingPlan.IsMissed(at, time.GetLocalNow()))
        {
            Forget();
            if (plan.StartAt is { } missed)
            {
                notifier.Notify(RecordingText.ScheduleTitle, RecordingText.ScheduleMissed(missed.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture)), NotificationKind.Warning);
            }

            return;
        }

        Begin(plan);
    }

    /// <summary>Waits for <paramref name="plan"/>'s time in place of anything that was waiting.</summary>
    public async Task ScheduleAsync(RecordingPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        await state.UpdateAsync(s => s with { ScheduledRecording = plan }).ConfigureAwait(false);
        Begin(plan);
        LogScheduled(plan.StartAt);
    }

    /// <summary>Lets go of what was waiting.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            _waiting?.Cancel();
            _waiting = null;
            Pending = null;
        }

        Forget();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _waiting?.Cancel();
            _waiting = null;
        }
    }

    private void Begin(RecordingPlan plan)
    {
        var waiting = new CancellationTokenSource();
        lock (_gate)
        {
            _waiting?.Cancel();
            _waiting = waiting;
            Pending = plan;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        _ = Task.Run(() => WaitAsync(plan, waiting.Token));
    }

    private async Task WaitAsync(RecordingPlan plan, CancellationToken ct)
    {
        try
        {
            var at = plan.StartAt ?? time.GetLocalNow();
            while (at - time.GetLocalNow() > RecordingPlan.LeadTime)
            {
                await Task.Delay(Tick, time, ct).ConfigureAwait(false);
            }

            var now = time.GetLocalNow();
            lock (_gate)
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                _waiting = null;
                Pending = null;
            }

            Forget();
            Changed?.Invoke(this, EventArgs.Empty);
            if (RecordingPlan.IsMissed(at, now))
            {
                notifier.Notify(RecordingText.ScheduleTitle, RecordingText.ScheduleMissed(at.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture)), NotificationKind.Warning);
                return;
            }

            if (recording.IsRecording || recording.IsSaving)
            {
                notifier.Notify(RecordingText.ScheduleTitle, RecordingText.ScheduleBusy, NotificationKind.Warning);
                return;
            }

            await recording.StartAsync(plan, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // cancelled, or the app is closing and it stays in the state for next time
        }
        catch (Exception e) when (e is InvalidOperationException or IOException)
        {
            LogFailed(e);
            notifier.Notify(RecordingText.CouldNotStart, e.Message, NotificationKind.Error);
        }
    }

    private void Forget() => _ = state.UpdateAsync(s => s with { ScheduledRecording = null });

    [LoggerMessage(Level = LogLevel.Information, Message = "A recording is scheduled for {At}")]
    private partial void LogScheduled(DateTimeOffset? at);

    [LoggerMessage(Level = LogLevel.Error, Message = "A scheduled recording could not start")]
    private partial void LogFailed(Exception e);
}
