using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Threading;
using CashGear.Capture.App.Recording;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.App.Tests.Recording;

/// <summary>What a screen reader gets from the recording toolbar: the state shown only by colour, and the clock.</summary>
public sealed class RecordingToolbarAccessibilityTests
{
    [Fact]
    public void The_clock_says_whether_it_is_recording_or_paused()
    {
        var recorder = new FakeRecorder(RecordingSound.None) { Elapsed = TimeSpan.FromSeconds(83) };
        var toolbar = new RecordingToolbarViewModel(recorder, () => recorder.IsPaused = !recorder.IsPaused, () => { });
        var changed = new List<string?>();
        toolbar.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        (toolbar.StateText, toolbar.ElapsedName).Should().Be(("Recording", "Recording, 1:23"));
        toolbar.PauseCommand.Execute(null);
        (toolbar.StateText, toolbar.ElapsedName).Should().Be(("Paused", "Paused, 1:23"));
        changed.Should().Contain([nameof(RecordingToolbarViewModel.StateText), nameof(RecordingToolbarViewModel.ElapsedName)]);
    }

    [Fact]
    public Task The_clock_is_named_with_the_state_as_its_status_and_is_a_live_region() => StaDispatcher.RunAsync(async () =>
    {
        var recorder = new FakeRecorder(RecordingSound.System) { Elapsed = TimeSpan.FromSeconds(5), IsPaused = true };
        var viewModel = new RecordingToolbarViewModel(recorder, () => { }, () => { });
        var window = new RecordingToolbarWindow(viewModel, AppTheme.Light, new PixelRect(0, 0, 1920, 1040));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); // the bindings (it is never shown)

        var clock = UIElementAutomationPeer.CreatePeerForElement(window.Elapsed);
        clock.GetName().Should().Be("Paused, 0:05");
        clock.GetItemStatus().Should().Be("Paused");
        clock.GetLiveSetting().Should().Be(AutomationLiveSetting.Polite);
        window.UsesKeyboard.Should().BeFalse("it never takes the focus until the hotkey");
        window.Close();
    });
}
