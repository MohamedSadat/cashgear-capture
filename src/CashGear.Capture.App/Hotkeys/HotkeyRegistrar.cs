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
    private HotkeySettings? _applied;

    /// <summary>Raised on the UI thread after the registrations were (re)applied.</summary>
    public event EventHandler? RegistrationsChanged;

    /// <summary>The hotkeys currently registered, by action.</summary>
    public IReadOnlyDictionary<HotkeyAction, HotkeyGesture> Active => _active;

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
            if (settings.Current.Hotkeys != _applied)
            {
                Apply(settings.Current.Hotkeys);
            }
        });

    private void Apply(HotkeySettings config)
    {
        hotkeys.UnregisterAll();
        _active.Clear();
        _applied = config;

        var problems = new List<string>();
        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            var text = config.For(action);
            if (text is null)
            {
                continue; // disabled on purpose
            }

            if (!HotkeyGesture.TryParse(text, out var gesture))
            {
                problems.Add($"{Label(action)}: \"{text}\" is not a valid hotkey.");
                continue;
            }

            var owner = _active.Where(p => p.Value == gesture).Select(p => (HotkeyAction?)p.Key).FirstOrDefault();
            if (owner is { } other)
            {
                problems.Add($"{Label(action)}: {gesture} is already set for {Label(other)}.");
                continue;
            }

            if (hotkeys.TryRegister(action, gesture, out var error))
            {
                _active[action] = gesture;
            }
            else
            {
                problems.Add($"{Label(action)}: {error}");
            }
        }

        LogApplied(_active.Count, problems.Count);
        if (problems.Count > 0)
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
        _ => action.ToString(),
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Hotkeys applied: {Registered} registered, {Problems} problem(s)")]
    private partial void LogApplied(int registered, int problems);
}
