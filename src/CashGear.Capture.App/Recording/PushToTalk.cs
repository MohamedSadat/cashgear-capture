using CashGear.Capture.Core.Audio;
using CashGear.Capture.Core.Hotkeys;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Interop.Native;

namespace CashGear.Capture.App.Recording;

/// <summary>Whether a key combination is held down now, anywhere (behind an interface for tests).</summary>
public interface IKeyboardState
{
    bool IsHeld(HotkeyGesture gesture);
}

/// <summary>Reads the keyboard with <c>GetAsyncKeyState</c>: the key and every modifier it names must be down.</summary>
internal sealed class Win32KeyboardState : IKeyboardState
{
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkLeftWin = 0x5B;
    private const int VkRightWin = 0x5C;

    public bool IsHeld(HotkeyGesture gesture) =>
        Down((int)gesture.VirtualKey)
        && (!gesture.Modifiers.HasFlag(HotkeyModifiers.Control) || Down(VkControl))
        && (!gesture.Modifiers.HasFlag(HotkeyModifiers.Alt) || Down(VkMenu))
        && (!gesture.Modifiers.HasFlag(HotkeyModifiers.Shift) || Down(VkShift))
        && (!gesture.Modifiers.HasFlag(HotkeyModifiers.Win) || Down(VkLeftWin) || Down(VkRightWin));

    private static bool Down(int key) => (User32.GetAsyncKeyState(key) & 0x8000) != 0;
}

/// <summary>
/// Push-to-talk for one recording: the microphone is silent (<see cref="IScreenRecorder.MicrophoneGated"/>) except
/// while the key is held (and a moment after, see <see cref="PushToTalkGate"/>). The key is read every 10 ms rather
/// than registered as a hotkey, so it still reaches the app in front: a key that does nothing there (F13, Scroll
/// Lock, a mouse-wheel key mapped to F-keys) works best.
/// </summary>
public sealed class PushToTalk : IDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);

    private readonly IScreenRecorder _recorder;
    private readonly IKeyboardState _keyboard;
    private readonly TimeProvider _time;
    private readonly PushToTalkGate _gate = new();
    private readonly long _started;
    private readonly ITimer? _timer;
    private volatile bool _disposed;

    /// <param name="poll">Read the key on a timer (false: the caller calls <see cref="Poll"/>, for tests).</param>
    public PushToTalk(IScreenRecorder recorder, HotkeyGesture key, IKeyboardState keyboard, TimeProvider time, bool poll = true)
    {
        _recorder = recorder;
        _keyboard = keyboard;
        _time = time;
        Key = key;
        _started = time.GetTimestamp();
        recorder.MicrophoneGated = true;
        _timer = poll ? time.CreateTimer(_ => Poll(), null, PollInterval, PollInterval) : null;
    }

    public HotkeyGesture Key { get; }

    /// <summary>The microphone is heard now.</summary>
    public bool IsTalking => _gate.IsOpen;

    /// <summary>Reads the key and opens or closes the microphone.</summary>
    public void Poll()
    {
        if (_disposed)
        {
            return;
        }

        var open = _gate.Update(_keyboard.IsHeld(Key), _time.GetElapsedTime(_started));
        _recorder.MicrophoneGated = !open;
    }

    public void Dispose()
    {
        _disposed = true;
        _timer?.Dispose();
    }
}
