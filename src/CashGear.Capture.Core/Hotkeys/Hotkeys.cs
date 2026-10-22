namespace CashGear.Capture.Core.Hotkeys;

/// <summary>Modifier keys. Values equal the Win32 <c>MOD_*</c> flags used by <c>RegisterHotKey</c>.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x1,
    Control = 0x2,
    Shift = 0x4,
    Win = 0x8,
}

/// <summary>
/// A global hotkey: modifiers plus a Win32 virtual-key code (e.g. <c>0x2C</c> = PrintScreen). Written in settings
/// as text such as <c>Ctrl+Shift+PrintScreen</c>; see <see cref="TryParse"/>.
/// </summary>
/// <remarks>Deliberately not WPF's <c>KeyGesture</c>: Core has no WPF dependency, and <c>KeyGesture</c> rejects a bare PrintScreen.</remarks>
public readonly record struct HotkeyGesture(HotkeyModifiers Modifiers, uint VirtualKey)
{
    public const uint VkSnapshot = 0x2C;

    private static readonly IReadOnlyDictionary<string, uint> KeysByName = BuildKeyNames();
    private static readonly IReadOnlyDictionary<uint, string> NamesByKey = KeysByName
        .GroupBy(p => p.Value)
        .ToDictionary(g => g.Key, g => g.First().Key);

    /// <summary>
    /// Parses <c>[Ctrl+][Alt+][Shift+][Win+]Key</c>, case-insensitive, any modifier order, with <c>Control</c>,
    /// <c>Windows</c>, <c>PrtSc</c> and similar aliases. Keys: letters, digits, F1–F24, PrintScreen, Pause,
    /// Insert, Delete, Home, End, PageUp, PageDown, arrows, Space, Tab, Escape, NumPad0–9, ScrollLock.
    /// A modifier on its own is not a hotkey.
    /// </summary>
    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Any(p => p.Length == 0))
        {
            return false; // "Ctrl++" or a trailing '+'
        }

        var modifiers = HotkeyModifiers.None;
        foreach (var part in parts[..^1])
        {
            var modifier = ParseModifier(part);
            if (modifier is null || modifiers.HasFlag(modifier.Value))
            {
                return false; // unknown or repeated modifier
            }

            modifiers |= modifier.Value;
        }

        if (!KeysByName.TryGetValue(parts[^1], out var key))
        {
            return false;
        }

        gesture = new HotkeyGesture(modifiers, key);
        return true;
    }

    public static HotkeyGesture Parse(string text) =>
        TryParse(text, out var gesture) ? gesture : throw new FormatException($"'{text}' is not a valid hotkey.");

    /// <summary>
    /// False for a single typing key such as <c>A</c> or <c>Delete</c>: registered globally it would stop working in
    /// every other app. Keys apps don't type with (PrintScreen, Pause, Scroll Lock, F1–F24) may stand alone.
    /// </summary>
    public bool IsSafeAsGlobalHotkey =>
        Modifiers != HotkeyModifiers.None || VirtualKey is VkSnapshot or 0x13 or 0x91 or (>= 0x70 and <= 0x87);

    /// <summary>Canonical text, e.g. <c>Ctrl+Shift+PrintScreen</c>; round-trips through <see cref="TryParse"/>.</summary>
    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(NamesByKey.TryGetValue(VirtualKey, out var name) ? name : $"VK 0x{VirtualKey:X2}");
        return string.Join('+', parts);
    }

    private static HotkeyModifiers? ParseModifier(string text) => text.ToUpperInvariant() switch
    {
        "CTRL" or "CONTROL" or "CTL" => HotkeyModifiers.Control,
        "ALT" => HotkeyModifiers.Alt,
        "SHIFT" => HotkeyModifiers.Shift,
        "WIN" or "WINDOWS" or "META" => HotkeyModifiers.Win,
        _ => null,
    };

    private static Dictionary<string, uint> BuildKeyNames()
    {
        // First name per key is the canonical one used by ToString().
        var names = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            ["PrintScreen"] = VkSnapshot,
            ["PrtSc"] = VkSnapshot,
            ["PrtScn"] = VkSnapshot,
            ["PrintScrn"] = VkSnapshot,
            ["Snapshot"] = VkSnapshot,
            ["Pause"] = 0x13,
            ["Space"] = 0x20,
            ["Tab"] = 0x09,
            ["Escape"] = 0x1B,
            ["Esc"] = 0x1B,
            ["PageUp"] = 0x21,
            ["PgUp"] = 0x21,
            ["PageDown"] = 0x22,
            ["PgDn"] = 0x22,
            ["End"] = 0x23,
            ["Home"] = 0x24,
            ["Left"] = 0x25,
            ["Up"] = 0x26,
            ["Right"] = 0x27,
            ["Down"] = 0x28,
            ["Insert"] = 0x2D,
            ["Ins"] = 0x2D,
            ["Delete"] = 0x2E,
            ["Del"] = 0x2E,
            ["ScrollLock"] = 0x91,
        };

        for (var c = 'A'; c <= 'Z'; c++)
        {
            names[c.ToString()] = c;
        }

        for (var d = 0; d <= 9; d++)
        {
            names[d.ToString(System.Globalization.CultureInfo.InvariantCulture)] = (uint)('0' + d);
            names[$"NumPad{d}"] = (uint)(0x60 + d);
        }

        for (var f = 1; f <= 24; f++)
        {
            names[$"F{f}"] = (uint)(0x70 + f - 1);
        }

        return names;
    }
}

public enum HotkeyAction
{
    CaptureRegion,
    CaptureActiveWindow,
    CaptureFullScreen,
    CaptureScrolling,

    /// <summary>Start a screen recording, or stop the one running.</summary>
    Record,

    /// <summary>Select an area and copy the text in it (OCR).</summary>
    CaptureText,

    /// <summary>Turn the pen for drawing on the screen on or off; only while recording.</summary>
    ToggleDrawing,

    /// <summary>The last capture again: the same kind, the same area (or window).</summary>
    RepeatLastCapture,

    /// <summary>Pick a window on the frozen screen, then capture its own content.</summary>
    CaptureWindow,

    CaptureFixedRegion,

    CaptureFreehand,

    CapturePolygon,

    CaptureRoundedRegion,

    /// <summary>A part of a window under the pointer (UI Automation).</summary>
    CaptureObject,

    /// <summary>The menus open when the delay ends.</summary>
    CaptureMenu,

    /// <summary>Show or hide the floating capture toolbar.</summary>
    ToggleToolbar,

    /// <summary>The screen magnifier (a lens following the pointer).</summary>
    Magnifier,

    /// <summary>Pick a colour anywhere on the screen.</summary>
    ColorPicker,

    /// <summary>Show or hide the screen ruler.</summary>
    Ruler,

    /// <summary>Show or hide the crosshair with the pointer's coordinates.</summary>
    Crosshair,

    /// <summary>
    /// The keyboard goes to the recording toolbar while recording, otherwise to the capture toolbar (shown first if
    /// hidden): Tab, the arrows, Enter and Space work there, and Esc gives the focus back.
    /// </summary>
    UseToolbar,
}

/// <summary>System-wide hotkeys. Registration and presses happen on the thread that owns the service (the UI thread).</summary>
public interface IHotkeyService
{
    /// <summary>Raised when a registered hotkey is pressed.</summary>
    event EventHandler<HotkeyAction>? Pressed;

    /// <summary>
    /// Registers <paramref name="gesture"/> for <paramref name="action"/>, replacing any earlier gesture for that action.
    /// Returns false with a user-facing reason when the key is already taken (by another app or Windows) or invalid.
    /// </summary>
    bool TryRegister(HotkeyAction action, HotkeyGesture gesture, out string? error);

    void UnregisterAll();
}
