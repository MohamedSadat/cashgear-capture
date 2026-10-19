using CashGear.Capture.App.Hosting;
using CashGear.Capture.App.Hotkeys;
using CashGear.Capture.App.Tray;
using CashGear.Capture.Core.Hotkeys;
using CashGear.Capture.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.App.Tests.Hotkeys;

public sealed class HotkeyRegistrarTests
{
    private readonly FakeHotkeyService _hotkeys = new();
    private readonly FakeNotifier _notifier = new();
    private readonly MutableSettings _settings = new();

    [Fact]
    public void Registers_the_default_spec_hotkeys()
    {
        using var registrar = Start();

        registrar.Active.Should().BeEquivalentTo(new Dictionary<HotkeyAction, HotkeyGesture>
        {
            [HotkeyAction.CaptureRegion] = HotkeyGesture.Parse("PrintScreen"),
            [HotkeyAction.CaptureActiveWindow] = HotkeyGesture.Parse("Alt+PrintScreen"),
            [HotkeyAction.CaptureFullScreen] = HotkeyGesture.Parse("Ctrl+PrintScreen"),
            [HotkeyAction.CaptureScrolling] = HotkeyGesture.Parse("Ctrl+Shift+PrintScreen"),
            [HotkeyAction.Record] = HotkeyGesture.Parse("Shift+PrintScreen"),
            [HotkeyAction.CaptureText] = HotkeyGesture.Parse("Ctrl+Alt+PrintScreen"),
            [HotkeyAction.RepeatLastCapture] = HotkeyGesture.Parse("Ctrl+Alt+Shift+PrintScreen"),
        });
        _notifier.Messages.Should().BeEmpty();
    }

    [Fact]
    public void A_taken_key_is_reported_once_and_the_rest_still_work()
    {
        _hotkeys.Taken.Add(HotkeyGesture.Parse("Ctrl+PrintScreen"));

        using var registrar = Start();

        registrar.Active.Should().NotContainKey(HotkeyAction.CaptureFullScreen);
        registrar.Active.Should().HaveCount(6);
        var message = _notifier.Messages.Should().ContainSingle().Subject;
        message.Kind.Should().Be(NotificationKind.Warning);
        message.Text.Should().Contain("Capture Full Screen: Ctrl+PrintScreen is already used");
    }

    [Fact]
    public void Invalid_duplicate_and_disabled_entries()
    {
        _settings.Current = new AppSettings
        {
            Hotkeys = new HotkeySettings(
                CaptureRegion: "Ctrl+Banana",
                CaptureActiveWindow: "Ctrl+F9",
                CaptureFullScreen: "ctrl+f9",
                CaptureScrolling: null,
                Record: null,
                CaptureText: null,
                RepeatLastCapture: null),
        };

        using var registrar = Start();

        registrar.Active.Should().BeEquivalentTo(new Dictionary<HotkeyAction, HotkeyGesture>
        {
            [HotkeyAction.CaptureActiveWindow] = HotkeyGesture.Parse("Ctrl+F9"),
        });
        var text = _notifier.Messages.Should().ContainSingle().Subject.Text;
        text.Should().Contain("Capture Region: \"Ctrl+Banana\" is not a valid hotkey.");
        text.Should().Contain("Capture Full Screen: Ctrl+F9 is already set for Capture Active Window.");
        text.Should().NotContain("Scrolling", "a disabled hotkey is not a problem");
    }

    [Fact]
    public void Settings_changes_re_register_and_raise_RegistrationsChanged()
    {
        using var registrar = Start();
        var raised = 0;
        registrar.RegistrationsChanged += (_, _) => raised++;

        _settings.Update(s => s with { Hotkeys = s.Hotkeys with { CaptureRegion = "Win+Shift+S" } });

        registrar.Active[HotkeyAction.CaptureRegion].Should().Be(HotkeyGesture.Parse("Win+Shift+S"));
        _hotkeys.Registered[HotkeyAction.CaptureRegion].Should().Be(HotkeyGesture.Parse("Win+Shift+S"));
        raised.Should().Be(1);
    }

    [Fact]
    public void Unrelated_settings_changes_leave_hotkeys_alone()
    {
        using var registrar = Start();
        var registrations = _hotkeys.RegisterCalls;

        _settings.Update(s => s with { Capture = s.Capture with { HideDelayMs = 50 } });

        _hotkeys.RegisterCalls.Should().Be(registrations);
    }

    [Fact]
    public void Dispose_unregisters_everything()
    {
        var registrar = Start();

        registrar.Dispose();

        _hotkeys.Registered.Should().BeEmpty();
    }

    private HotkeyRegistrar Start()
    {
        var registrar = new HotkeyRegistrar(_hotkeys, _settings, _notifier, new InlineUiDispatcher(), NullLogger<HotkeyRegistrar>.Instance);
        registrar.Start();
        return registrar;
    }
}

public sealed class HotkeyRegistrarSuspendTests
{
    private readonly FakeHotkeyService _hotkeys = new();
    private readonly MutableSettings _settings = new();

    [Fact]
    public void Suspend_releases_keys_and_resume_brings_back_the_latest_settings()
    {
        using var registrar = Start();

        var suspension = registrar.Suspend();
        _hotkeys.Registered.Should().BeEmpty();

        // A save while suspended is applied on resume, not before.
        _settings.Update(s => s with { Hotkeys = s.Hotkeys with { CaptureRegion = "Ctrl+F9" } });
        _hotkeys.Registered.Should().BeEmpty();

        suspension.Dispose();
        _hotkeys.Registered[HotkeyAction.CaptureRegion].Should().Be(HotkeyGesture.Parse("Ctrl+F9"));
        suspension.Dispose(); // idempotent
        _hotkeys.Registered.Should().HaveCount(7);
    }

    [Fact]
    public void Nested_suspensions_resume_only_at_the_end()
    {
        using var registrar = Start();

        var outer = registrar.Suspend();
        var inner = registrar.Suspend();
        inner.Dispose();
        _hotkeys.Registered.Should().BeEmpty();

        outer.Dispose();
        _hotkeys.Registered.Should().HaveCount(7);
    }

    [Fact]
    public void The_drawing_key_is_taken_only_while_recording()
    {
        using var registrar = Start();
        var drawing = HotkeyGesture.Parse(HotkeySettings.DefaultToggleDrawing);

        registrar.Active.Should().NotContainKey(HotkeyAction.ToggleDrawing, "other apps keep Ctrl+Shift+D until a recording starts");
        registrar.Problems.Should().BeEmpty();
        registrar.SetRecording(true);
        registrar.Active[HotkeyAction.ToggleDrawing].Should().Be(drawing);
        _hotkeys.Registered[HotkeyAction.ToggleDrawing].Should().Be(drawing);
        _hotkeys.Registered.Should().HaveCount(8, "the others stay");
        registrar.SetRecording(false);
        _hotkeys.Registered.Should().NotContainKey(HotkeyAction.ToggleDrawing);
        HotkeyRegistrar.Label(HotkeyAction.ToggleDrawing).Should().Be("Draw on Screen (while recording)");
    }

    [Fact]
    public void A_drawing_key_that_another_action_has_is_a_problem_even_before_recording()
    {
        _settings.Current = new AppSettings { Hotkeys = new HotkeySettings() with { ToggleDrawing = "Shift+PrintScreen" } };

        using var registrar = Start();

        registrar.Problems[HotkeyAction.ToggleDrawing].Should().Contain("already set for Record Screen");
    }

    [Fact]
    public void A_single_typing_key_is_refused()
    {
        _settings.Current = new AppSettings { Hotkeys = new HotkeySettings(CaptureRegion: "Q") };

        using var registrar = Start();

        registrar.Active.Should().NotContainKey(HotkeyAction.CaptureRegion);
        registrar.Problems[HotkeyAction.CaptureRegion].Should().Contain("would block that key in every app");
    }

    private HotkeyRegistrar Start()
    {
        var registrar = new HotkeyRegistrar(_hotkeys, _settings, new FakeNotifier(), new InlineUiDispatcher(), NullLogger<HotkeyRegistrar>.Instance);
        registrar.Start();
        return registrar;
    }
}
