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
        });
        _notifier.Messages.Should().BeEmpty();
    }

    [Fact]
    public void A_taken_key_is_reported_once_and_the_rest_still_work()
    {
        _hotkeys.Taken.Add(HotkeyGesture.Parse("Ctrl+PrintScreen"));

        using var registrar = Start();

        registrar.Active.Should().NotContainKey(HotkeyAction.CaptureFullScreen);
        registrar.Active.Should().HaveCount(3);
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
                CaptureScrolling: null),
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

    private sealed class FakeHotkeyService : IHotkeyService
    {
        public event EventHandler<HotkeyAction>? Pressed;

        public HashSet<HotkeyGesture> Taken { get; } = [];

        public Dictionary<HotkeyAction, HotkeyGesture> Registered { get; } = [];

        public int RegisterCalls { get; private set; }

        public bool TryRegister(HotkeyAction action, HotkeyGesture gesture, out string? error)
        {
            RegisterCalls++;
            if (Taken.Contains(gesture))
            {
                error = $"{gesture} is already used by another app or by Windows.";
                return false;
            }

            Registered[action] = gesture;
            error = null;
            return true;
        }

        public void UnregisterAll() => Registered.Clear();

        public void Press(HotkeyAction action) => Pressed?.Invoke(this, action);
    }

    private sealed class FakeNotifier : ITrayNotifier
    {
        public List<(string Title, string Text, NotificationKind Kind)> Messages { get; } = [];

        public void Notify(string title, string message, NotificationKind kind = NotificationKind.Info) =>
            Messages.Add((title, message, kind));
    }

    private sealed class MutableSettings : ISettingsService
    {
        public event EventHandler? Changed;

        public AppSettings Current { get; set; } = new();

        public void Update(Func<AppSettings, AppSettings> update)
        {
            Current = update(Current).Normalize();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task UpdateAsync(Func<AppSettings, AppSettings> update, CancellationToken ct = default)
        {
            Update(update);
            return Task.CompletedTask;
        }
    }

    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;

        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());

        public Task InvokeAsync(Func<Task> func) => func();

        public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();
    }
}
