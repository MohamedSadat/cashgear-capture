using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CashGear.Capture.Interop.Native;
using CashGear.Capture.Overlays.Resources;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.Overlays.Input;

public enum RawInputKind
{
    Move,
    ButtonDown,
    ButtonUp,
    Wheel,
    KeyDown,
    KeyUp,

    /// <summary>Another window came to the foreground (<see cref="RawInput.Window"/>).</summary>
    Foreground,

    /// <summary>The keyboard focus moved, inside any app (<see cref="RawInput.Window"/> holds it).</summary>
    Focus,
}

/// <summary>
/// One input event as the hooks saw it: when (a <see cref="Stopwatch"/> timestamp, the recording's clock), the
/// pointer position (physical screen pixels), and for a button the button (0 left, 1 right, 2 middle, 3–4 side), for
/// a key its virtual-key code, for the wheel its delta.
/// </summary>
public readonly record struct RawInput(RawInputKind Kind, long Timestamp, int X, int Y, uint Code, bool Injected, nint Window = 0);

/// <summary>
/// System-wide low-level mouse and keyboard hooks (<c>WH_MOUSE_LL</c>, <c>WH_KEYBOARD_LL</c>) and the foreground and
/// focus WinEvents, on a thread of their own with its own message loop ("Overlay input hooks"). The callbacks only
/// stamp the time, queue the event and return, so Windows never finds them slow and removes them; everything else
/// happens on the reader's thread (<see cref="TryRead"/>). Only one set of hooks runs at a time (the callbacks are
/// static). Nothing here is written anywhere: the reader decides what is kept.
/// </summary>
public sealed unsafe partial class InputHooks : IDisposable
{
    /// <summary>Events waiting beyond this are dropped (the reader is far behind); moves first.</summary>
    private const int MaxQueued = 20_000;

    private static InputHooks? _active;

    private readonly ConcurrentQueue<RawInput> _queue = new();
    private readonly AutoResetEvent _arrived = new(false);
    private readonly ManualResetEventSlim _ready = new();
    private readonly bool _keyboard;
    private readonly ILogger _logger;
    private readonly Thread _thread;
    private uint _threadId;
    private Exception? _failure;
    private long _dropped;
    private int _disposed;

    [Localizable(false)] // the thread's name, for debuggers
    private InputHooks(bool keyboard, ILogger logger)
    {
        _keyboard = keyboard;
        _logger = logger;
        _thread = new Thread(Run) { IsBackground = true, Name = "Overlay input hooks", Priority = ThreadPriority.Highest };
    }

    /// <summary>Events dropped because the reader fell far behind.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Installs the hooks (the keyboard hook only when <paramref name="keyboard"/>) and returns once they work.</summary>
    /// <exception cref="InvalidOperationException">Hooks are running already, or Windows refused them.</exception>
    public static InputHooks Start(bool keyboard, ILogger logger)
    {
        var hooks = new InputHooks(keyboard, logger);
        if (Interlocked.CompareExchange(ref _active, hooks, null) is not null)
        {
            throw new InvalidOperationException("Input hooks are running already.");
        }

        hooks._thread.Start();
        hooks._ready.Wait();
        if (hooks._failure is { } failure)
        {
            hooks.Dispose();
            throw new InvalidOperationException(OverlaysStrings.InputHooksRefused(failure.Message), failure);
        }

        return hooks;
    }

    /// <summary>The next event, in the order they happened.</summary>
    public bool TryRead(out RawInput input) => _queue.TryDequeue(out input);

    /// <summary>Waits until an event may be there (or <paramref name="timeout"/> passed).</summary>
    public bool Wait(TimeSpan timeout) => !_queue.IsEmpty || _arrived.WaitOne(timeout);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        if (_threadId != 0)
        {
            User32.PostThreadMessageW(_threadId, User32.WmQuit, 0, 0);
        }

        if (_thread.IsAlive)
        {
            _thread.Join(TimeSpan.FromSeconds(5));
        }

        Interlocked.CompareExchange(ref _active, null, this);
        _arrived.Dispose();
        _ready.Dispose();
    }

    private void Run()
    {
        nint mouse = 0, keyboard = 0, foreground = 0, focus = 0;
        try
        {
            _threadId = Kernel32.GetCurrentThreadId();
            var module = Kernel32.GetModuleHandleW(null);
            mouse = User32.SetWindowsHookExW(User32.WhMouseLl, &MouseProc, module, 0);
            if (mouse == 0)
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            }

            if (_keyboard)
            {
                keyboard = User32.SetWindowsHookExW(User32.WhKeyboardLl, &KeyboardProc, module, 0);
                if (keyboard == 0)
                {
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
                }
            }

            foreground = User32.SetWinEventHook(User32.EventSystemForeground, User32.EventSystemForeground, 0, &WinEventProc, 0, 0, User32.WinEventOutOfContext);
            if (_keyboard)
            {
                focus = User32.SetWinEventHook(User32.EventObjectFocus, User32.EventObjectFocus, 0, &WinEventProc, 0, 0, User32.WinEventOutOfContext);
            }

            LogStarted(_keyboard);
        }
        catch (Exception e)
        {
            _failure = e;
        }
        finally
        {
            _ready.Set();
        }

        if (_failure is null)
        {
            MSG message;
            while (User32.GetMessageW(&message, 0, 0, 0) > 0)
            {
                User32.TranslateMessage(&message);
                User32.DispatchMessageW(&message);
            }
        }

        if (focus != 0)
        {
            User32.UnhookWinEvent(focus);
        }

        if (foreground != 0)
        {
            User32.UnhookWinEvent(foreground);
        }

        if (keyboard != 0)
        {
            User32.UnhookWindowsHookEx(keyboard);
        }

        if (mouse != 0)
        {
            User32.UnhookWindowsHookEx(mouse);
        }

        LogStopped(Dropped);
    }

    private void Enqueue(in RawInput input)
    {
        if (_queue.Count >= MaxQueued && input.Kind == RawInputKind.Move)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }

        _queue.Enqueue(input);
        _arrived.Set();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint MouseProc(int code, nint wParam, nint lParam)
    {
        if (code == User32.HcAction && _active is { } hooks)
        {
            var now = Stopwatch.GetTimestamp();
            var info = (MSLLHOOKSTRUCT*)lParam;
            var injected = (info->flags & User32.LlmhfInjected) != 0;
            var (kind, button) = (int)wParam switch
            {
                User32.WmMouseMove => (RawInputKind.Move, 0u),
                User32.WmLButtonDown => (RawInputKind.ButtonDown, 0u),
                User32.WmLButtonUp => (RawInputKind.ButtonUp, 0u),
                User32.WmRButtonDown => (RawInputKind.ButtonDown, 1u),
                User32.WmRButtonUp => (RawInputKind.ButtonUp, 1u),
                User32.WmMButtonDown => (RawInputKind.ButtonDown, 2u),
                User32.WmMButtonUp => (RawInputKind.ButtonUp, 2u),
                User32.WmXButtonDown => (RawInputKind.ButtonDown, (info->mouseData >> 16) == 2 ? 4u : 3u),
                User32.WmXButtonUp => (RawInputKind.ButtonUp, (info->mouseData >> 16) == 2 ? 4u : 3u),
                User32.WmMouseWheel => (RawInputKind.Wheel, (uint)(short)(info->mouseData >> 16)),
                _ => ((RawInputKind)(-1), 0u),
            };

            if (kind >= 0)
            {
                hooks.Enqueue(new RawInput(kind, now, info->pt.X, info->pt.Y, button, injected));
            }
        }

        return User32.CallNextHookEx(0, code, wParam, lParam);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint KeyboardProc(int code, nint wParam, nint lParam)
    {
        if (code == User32.HcAction && _active is { } hooks)
        {
            var now = Stopwatch.GetTimestamp();
            var info = (KBDLLHOOKSTRUCT*)lParam;
            var down = (int)wParam is User32.WmKeyDown or User32.WmSysKeyDown;
            hooks.Enqueue(new RawInput(down ? RawInputKind.KeyDown : RawInputKind.KeyUp, now, 0, 0, info->vkCode, (info->flags & User32.LlkhfInjected) != 0));
        }

        return User32.CallNextHookEx(0, code, wParam, lParam);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void WinEventProc(nint hook, uint winEvent, nint hwnd, int objectId, int childId, uint thread, uint time)
    {
        if (_active is { } hooks)
        {
            var kind = winEvent == User32.EventSystemForeground ? RawInputKind.Foreground : RawInputKind.Focus;
            hooks.Enqueue(new RawInput(kind, Stopwatch.GetTimestamp(), 0, 0, 0, false, hwnd));
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Input hooks installed (keyboard {Keyboard})")]
    private partial void LogStarted(bool keyboard);

    [LoggerMessage(Level = LogLevel.Information, Message = "Input hooks removed ({Dropped} events dropped)")]
    private partial void LogStopped(long dropped);
}
