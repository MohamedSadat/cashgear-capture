using System.ComponentModel;
using System.Globalization;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.Core.Overlays;

/// <summary>
/// The text a key types on the keyboard layout in use: <paramref name="virtualKey"/> with Shift held or not, and
/// with AltGr (right Alt) held or not. Null when it types nothing (a control or dead key).
/// </summary>
public delegate string? KeyCharacters(uint virtualKey, bool shift, bool altGr);

/// <summary>
/// Turns key presses into the captions of the keystroke overlay: <c>Ctrl + Shift + S</c> for a shortcut, the same
/// shortcut again within the hold time as <c>Ctrl + Z ×3</c>, and (in <see cref="KeystrokeMode.AllKeys"/>) typed
/// text gathered into one caption. Modifier keys alone show nothing. Fed every key press and release in order, from
/// one thread; it remembers which modifiers are held.
/// </summary>
// Key names stay English on purpose: they match the keycaps (docs\localization.md).
[Localizable(false)]
public sealed class KeystrokeFormatter(KeystrokeMode mode, TimeSpan hold, KeyCharacters? characters = null)
{
    public const int MaxTypedLength = 40;

    private const uint VkShift = 0x10;
    private const uint VkControl = 0x11;
    private const uint VkMenu = 0x12;
    private const uint VkLeftWin = 0x5B;
    private const uint VkRightWin = 0x5C;
    private const uint VkLeftShift = 0xA0;
    private const uint VkRightShift = 0xA1;
    private const uint VkLeftControl = 0xA2;
    private const uint VkRightControl = 0xA3;
    private const uint VkLeftMenu = 0xA4;
    private const uint VkRightMenu = 0xA5;

    private readonly HashSet<uint> _held = [];
    private int _nextId = 1;
    private Current? _current;

    public KeystrokeMode Mode => mode;

    /// <summary>A key went down or up at <paramref name="time"/>. Returns the caption it starts or changes, if any.</summary>
    public KeyCaption? Key(TimeSpan time, uint virtualKey, bool down)
    {
        if (IsModifier(virtualKey))
        {
            if (down)
            {
                _held.Add(virtualKey);
            }
            else
            {
                _held.Remove(virtualKey);
            }

            return null;
        }

        if (!down || mode == KeystrokeMode.Off)
        {
            return null;
        }

        var shift = Held(VkShift, VkLeftShift, VkRightShift);
        var control = Held(VkControl, VkLeftControl, VkRightControl);
        var alt = Held(VkMenu, VkLeftMenu, VkRightMenu);
        var win = Held(VkLeftWin, VkRightWin);

        // AltGr is right Alt plus a left Ctrl Windows adds: a key that types a character with it is typing, not a shortcut.
        var altGrText = _held.Contains(VkRightMenu) ? Printable(characters?.Invoke(virtualKey, shift, altGr: true)) : null;
        if (altGrText is null && (control || alt || win))
        {
            return Repeat(Kind.Shortcut, Combo(control, win, alt, shift) + KeyName(virtualKey), time);
        }

        if (virtualKey is 0x1B or (>= 0x70 and <= 0x87))
        {
            return Repeat(Kind.Shortcut, (shift ? "Shift + " : "") + KeyName(virtualKey), time);
        }

        if (mode != KeystrokeMode.AllKeys)
        {
            return null;
        }

        var typed = altGrText ?? Printable(characters?.Invoke(virtualKey, shift, altGr: false));
        if (typed is not null && virtualKey != 0x0D && virtualKey != 0x09)
        {
            return Type(typed, time);
        }

        return Repeat(Kind.Named, (shift ? "Shift + " : "") + KeyName(virtualKey), time);
    }

    /// <summary>Forgets the held modifiers and the caption being added to (e.g. after keys were left out for privacy).</summary>
    public void Reset()
    {
        _held.Clear();
        _current = null;
    }

    /// <summary>A key's name as shortcuts show it: <c>S</c>, <c>F5</c>, <c>Enter</c>, <c>←</c>, <c>/</c>.</summary>
    public string KeyName(uint virtualKey) => virtualKey switch
    {
        >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39 => ((char)virtualKey).ToString(),
        >= 0x70 and <= 0x87 => "F" + (virtualKey - 0x6F).ToString(CultureInfo.InvariantCulture),
        >= 0x60 and <= 0x69 => "Num " + (virtualKey - 0x60).ToString(CultureInfo.InvariantCulture),
        0x08 => "Backspace",
        0x09 => "Tab",
        0x0D => "Enter",
        0x13 => "Pause",
        0x14 => "Caps Lock",
        0x1B => "Esc",
        0x20 => "Space",
        0x21 => "Page Up",
        0x22 => "Page Down",
        0x23 => "End",
        0x24 => "Home",
        0x25 => "←",
        0x26 => "↑",
        0x27 => "→",
        0x28 => "↓",
        0x2C => "Print Screen",
        0x2D => "Insert",
        0x2E => "Delete",
        0x5D => "Menu",
        0x6A => "Num *",
        0x6B => "Num +",
        0x6D => "Num -",
        0x6E => "Num .",
        0x6F => "Num /",
        0x90 => "Num Lock",
        0x91 => "Scroll Lock",
        0xA6 => "Browser Back",
        0xA7 => "Browser Forward",
        0xA8 => "Browser Refresh",
        0xAD => "Mute",
        0xAE => "Volume Down",
        0xAF => "Volume Up",
        0xB0 => "Next Track",
        0xB1 => "Previous Track",
        0xB2 => "Stop",
        0xB3 => "Play/Pause",
        _ => Printable(characters?.Invoke(virtualKey, shift: false, altGr: false))?.ToUpper(CultureInfo.CurrentCulture)
            ?? "Key " + virtualKey.ToString("X2", CultureInfo.InvariantCulture),
    };

    /// <summary>Shift, Ctrl, Alt or Win (either side).</summary>
    public static bool IsModifierKey(uint key) => IsModifier(key);

    private static bool IsModifier(uint key) =>
        key is VkShift or VkControl or VkMenu or VkLeftWin or VkRightWin or (>= VkLeftShift and <= VkRightMenu);

    /// <summary>Text that shows something (not a control character); null otherwise.</summary>
    private static string? Printable(string? text) =>
        string.IsNullOrEmpty(text) || text.Any(char.IsControl) ? null : text;

    private static string Combo(bool control, bool win, bool alt, bool shift) =>
        (control ? "Ctrl + " : "") + (win ? "Win + " : "") + (alt ? "Alt + " : "") + (shift ? "Shift + " : "");

    private bool Held(params ReadOnlySpan<uint> keys)
    {
        foreach (var key in keys)
        {
            if (_held.Contains(key))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A shortcut or named key: pressed again soon after, the same caption counts it.</summary>
    private KeyCaption Repeat(Kind kind, string text, TimeSpan time)
    {
        if (_current is { } current && current.Kind == kind && current.Base == text && time - current.Last <= hold)
        {
            current.Count++;
            current.Last = time;
            return new KeyCaption(current.Id, time, $"{text} ×{current.Count}");
        }

        _current = new Current(_nextId++, kind, text, time);
        return new KeyCaption(_current.Id, time, text);
    }

    /// <summary>Typed text: added to the caption being typed, or a new one after a pause or a shortcut.</summary>
    private KeyCaption Type(string typed, TimeSpan time)
    {
        if (_current is { Kind: Kind.Typing } current && time - current.Last <= hold)
        {
            current.Base += typed;
            current.Last = time;
            if (current.Base.Length > MaxTypedLength * 4)
            {
                current.Base = current.Base[^MaxTypedLength..]; // only the end is ever shown
            }
        }
        else
        {
            current = _current = new Current(_nextId++, Kind.Typing, typed, time);
        }

        var text = current.Base.Length > MaxTypedLength ? "…" + current.Base[^(MaxTypedLength - 1)..] : current.Base;
        return new KeyCaption(current.Id, time, text);
    }

    private enum Kind
    {
        Shortcut,
        Named,
        Typing,
    }

    private sealed class Current(int id, Kind kind, string text, TimeSpan time)
    {
        public int Id { get; } = id;

        public Kind Kind { get; } = kind;

        public string Base { get; set; } = text;

        public int Count { get; set; } = 1;

        public TimeSpan Last { get; set; } = time;
    }
}
