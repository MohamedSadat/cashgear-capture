using CashGear.Capture.App.Hosting;
using CashGear.Capture.App.Tray;
using CashGear.Capture.Core.Hotkeys;
using CashGear.Capture.Core.Settings;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.App.Hotkeys;

/// <summary>
/// Keeps the registered hotkeys in line with <see cref="AppSettings.Hotkeys"/>: applies them at startup and again
/// whenever the settings change (including hand edits of settings.json). Anything that cannot be registered —
/// invalid text, a key another app or Windows already owns, or the same key twice — is logged and reported in one
/// tray notification; the app keeps running without that hotkey.
/// </summary>
public sealed partial class HotkeyRegistrar(
    IHotkeyService hotkeys,
    ISettingsService settings,
    ITrayNotifier notifier,
    IUiDispatcher ui,
    ILogger<HotkeyRegistrar> logger) : IDisposable
{
    private readonly Dictionary<HotkeyAction, HotkeyGesture> _active = [];
    private readonly Dictionary<HotkeyAction, string> _problems = [];
    private HotkeySettings? _applied;
    private int _suspended;
    private bool _recording;

    /// <summary>Raised on the UI thread after the registrations were (re)applied.</summary>
    public event EventHandler? RegistrationsChanged;

    /// <summary>The hotkeys currently registered, by action.</summary>
    public IReadOnlyDictionary<HotkeyAction, HotkeyGesture> Active => _active;

    /// <summary>Why an action's configured hotkey is not registered (invalid, duplicate or taken), by action.</summary>
    public IReadOnlyDictionary<HotkeyAction, string> Problems => _problems;

    /// <summary>
    /// Releases every hotkey until the result is disposed — e.g. while the settings window records a new key, so
    /// pressing PrintScreen there records it instead of taking a screenshot. Nested calls are counted. UI thread.
    /// </summary>
    public IDisposable Suspend()
    {
        if (_suspended++ == 0)
        {
            hotkeys.UnregisterAll();
            LogSuspended();
        }

        return new Resumer(this);
    }

    /// <summary>Keys that are registered only while a recording runs (they would block the key in other apps otherwise).</summary>
    public static bool IsRecordingOnly(HotkeyAction action) => action == HotkeyAction.ToggleDrawing;

    /// <summary>A recording started or stopped: its keys are registered or released. UI thread.</summary>
    public void SetRecording(bool recording)
    {
        if (_recording == recording)
        {
            return;
        }

        _recording = recording;
        if (_suspended == 0)
        {
            Apply(settings.Current.Hotkeys, notify: false);
        }
    }

    /// <summary>Registers the configured hotkeys. Call on the UI thread.</summary>
    public void Start()
    {
        settings.Changed += OnSettingsChanged;
        Apply(settings.Current.Hotkeys);
    }

    public void Dispose()
    {
        settings.Changed -= OnSettingsChanged;
        hotkeys.UnregisterAll();
    }

    private void OnSettingsChanged(object? sender, EventArgs e) =>
        _ = ui.InvokeAsync(() =>
        {
            if (settings.Current.Hotkeys != _applied && _suspended == 0)
            {
                Apply(settings.Current.Hotkeys);
            }
        });

    private void Resume()
    {
        if (--_suspended == 0)
        {
            Apply(settings.Current.Hotkeys, notify: false);
        }
    }

    private void Apply(HotkeySettings config, bool notify = true)
    {
        hotkeys.UnregisterAll();
        _active.Clear();
        _problems.Clear();
        _applied = config;

        var problems = new List<string>();
        var reserved = new Dictionary<HotkeyAction, HotkeyGesture>();
        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            var text = config.For(action);
            if (text is null)
            {
                continue; // disabled on purpose
            }

            if (!HotkeyGesture.TryParse(text, out var gesture))
            {
                Problem(action, $"\"{text}\" is not a valid hotkey.");
                continue;
            }

            if (!gesture.IsSafeAsGlobalHotkey)
            {
                Problem(action, $"{gesture} on its own would block that key in every app; add Ctrl, Alt, Shift or Win.");
                continue;
            }

            var owner = _active.Concat(reserved).Where(p => p.Value == gesture).Select(p => (HotkeyAction?)p.Key).FirstOrDefault();
            if (owner is { } other)
            {
                Problem(action, $"{gesture} is already set for {Label(other)}.");
                continue;
            }

            if (IsRecordingOnly(action) && !_recording)
            {
                reserved[action] = gesture; // checked and kept for the next recording, not taken from other apps now
                continue;
            }

            if (hotkeys.TryRegister(action, gesture, out var error))
            {
                _active[action] = gesture;
            }
            else
            {
                Problem(action, error ?? $"{gesture} could not be registered.");
            }
        }

        void Problem(HotkeyAction action, string message)
        {
            _problems[action] = message;
            problems.Add($"{Label(action)}: {message}");
        }

        LogApplied(_active.Count, problems.Count);
        if (problems.Count > 0 && notify)
        {
            notifier.Notify(
                "Some hotkeys are not available",
                string.Join("\n", problems) + "\nChoose other keys in Settings.",
                NotificationKind.Warning);
        }

        RegistrationsChanged?.Invoke(this, EventArgs.Empty);
    }

    public static string Label(HotkeyAction action) => action switch
    {
        HotkeyAction.CaptureRegion => "Capture Region",
        HotkeyAction.CaptureActiveWindow => "Capture Active Window",
        HotkeyAction.CaptureFullScreen => "Capture Full Screen",
        HotkeyAction.CaptureScrolling => "Scrolling Capture",
        HotkeyAction.Record => "Record Screen (again to stop)",
        HotkeyAction.CaptureText => "Capture Text (OCR)",
        HotkeyAction.ToggleDrawing => "Draw on Screen (while recording)",
        HotkeyAction.RepeatLastCapture => "Repeat Last Capture",
        HotkeyAction.CaptureWindow => "Capture Window (pick one)",
        HotkeyAction.CaptureFixedRegion => "Capture Fixed-Size Region",
        HotkeyAction.CaptureFreehand => "Capture Freehand Region",
        HotkeyAction.CapturePolygon => "Capture Polygon Region",
        HotkeyAction.CaptureRoundedRegion => "Capture Rounded Region",
        HotkeyAction.CaptureObject => "Capture Object (part of a window)",
        HotkeyAction.CaptureMenu => "Capture Open Menu",
        HotkeyAction.ToggleToolbar => "Show or Hide the Capture Toolbar",
        _ => action.ToString(),
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Hotkeys applied: {Registered} registered, {Problems} problem(s)")]
    private partial void LogApplied(int registered, int problems);

    [LoggerMessage(Level = LogLevel.Information, Message = "Hotkeys suspended")]
    private partial void LogSuspended();

    private sealed class Resumer(HotkeyRegistrar owner) : IDisposable
    {
        private HotkeyRegistrar? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Resume();
    }
}
