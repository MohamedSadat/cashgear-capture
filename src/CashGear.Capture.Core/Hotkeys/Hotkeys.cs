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

/// <summary>A global hotkey: modifiers plus a Win32 virtual-key code (e.g. <c>0x2C</c> = PrintScreen).</summary>
/// <remarks>Deliberately not WPF's <c>KeyGesture</c>: Core has no WPF dependency, and <c>KeyGesture</c> rejects a bare PrintScreen.</remarks>
public readonly record struct HotkeyGesture(HotkeyModifiers Modifiers, uint VirtualKey)
{
    public const uint VkSnapshot = 0x2C;

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(VirtualKey == VkSnapshot ? "PrintScreen" : $"VK 0x{VirtualKey:X2}");
        return string.Join('+', parts);
    }
}

public enum HotkeyAction
{
    CaptureRegion,
    CaptureActiveWindow,
    CaptureFullScreen,
    CaptureScrolling,
}

/// <summary>Global hotkey registration (implemented in Phase 3).</summary>
public interface IHotkeyService
{
    /// <summary>Registers a hotkey. Returns false with a user-facing reason when the key is already taken.</summary>
    bool TryRegister(HotkeyAction action, HotkeyGesture gesture, out string? error);

    void UnregisterAll();
}
