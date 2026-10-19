using CashGear.Capture.Interop.Native;

namespace CashGear.Capture.Overlays.Input;

/// <summary>Who owns a window: its program file name (<c>chrome.exe</c>, lower case) and its title.</summary>
internal static unsafe class WindowFacts
{
    public static string? Title(nint hwnd)
    {
        if (hwnd == 0)
        {
            return null;
        }

        var length = User32.GetWindowTextLengthW(hwnd);
        if (length <= 0)
        {
            return null;
        }

        var buffer = new char[Math.Min(length + 1, 1024)];
        fixed (char* text = buffer)
        {
            var written = User32.GetWindowTextW(hwnd, text, buffer.Length);
            return written <= 0 ? null : new string(text, 0, written);
        }
    }

    /// <summary>The program file of the process that owns <paramref name="hwnd"/>, or null if it can't be read.</summary>
    public static string? Program(nint hwnd)
    {
        if (hwnd == 0)
        {
            return null;
        }

        uint processId;
        User32.GetWindowThreadProcessId(hwnd, &processId);
        return processId == 0 ? null : ProgramOf(processId);
    }

    public static string? ProgramOf(uint processId)
    {
        var process = Kernel32.OpenProcess(Kernel32.ProcessQueryLimitedInformation, 0, processId);
        if (process == 0)
        {
            return null;
        }

        try
        {
            var buffer = stackalloc char[1024];
            var size = 1024u;
            return Kernel32.QueryFullProcessImageNameW(process, 0, buffer, &size) == 0
                ? null
                : Path.GetFileName(new string(buffer, 0, (int)size)).ToLowerInvariant();
        }
        finally
        {
            Kernel32.CloseHandle(process);
        }
    }

    /// <summary>The thread that owns <paramref name="hwnd"/> (for its keyboard layout).</summary>
    public static uint Thread(nint hwnd) => hwnd == 0 ? 0 : User32.GetWindowThreadProcessId(hwnd, null);
}

/// <summary>
/// What keys type on the keyboard layout of the window in front (<c>ToUnicodeEx</c> with "don't change the
/// keyboard state", so dead keys still work in the app being typed into). Caps Lock is followed from the key
/// presses it sees. Used on one thread.
/// </summary>
internal sealed unsafe class KeyboardLayout
{
    private readonly byte[] _state = new byte[256];
    private nint _layout = User32.GetKeyboardLayout(0);
    private bool _capsLock = (User32.GetKeyState(User32.VkCapital) & 1) != 0;

    /// <summary>The window in front changed: its keyboard layout is used from now on.</summary>
    public void Follow(nint foreground)
    {
        var thread = WindowFacts.Thread(foreground);
        if (thread != 0)
        {
            _layout = User32.GetKeyboardLayout(thread);
        }
    }

    /// <summary>A key went down: Caps Lock toggles.</summary>
    public void Pressed(uint virtualKey)
    {
        if (virtualKey == User32.VkCapital)
        {
            _capsLock = !_capsLock;
        }
    }

    public string? Characters(uint virtualKey, bool shift, bool altGr)
    {
        Array.Clear(_state);
        if (shift)
        {
            _state[0x10] = 0x80;
            _state[0xA0] = 0x80;
        }

        if (altGr)
        {
            _state[0x11] = 0x80;
            _state[0xA2] = 0x80;
            _state[0x12] = 0x80;
            _state[0xA5] = 0x80;
        }

        if (_capsLock)
        {
            _state[User32.VkCapital] = 0x01;
        }

        var scan = User32.MapVirtualKeyExW(virtualKey, User32.MapVkToVsc, _layout);
        var buffer = stackalloc char[8];
        fixed (byte* state = _state)
        {
            var written = User32.ToUnicodeEx(virtualKey, scan, state, buffer, 8, User32.ToUnicodeNoStateChange, _layout);
            return written > 0 ? new string(buffer, 0, written) : null;
        }
    }
}
