using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using CashGear.Capture.Interop.Com;
using CashGear.Capture.Interop.Native;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.Overlays.Input;

/// <summary>Whether a key may be shown.</summary>
public enum KeyVerdict
{
    Show,

    /// <summary>Typed into a password field or an excluded app (or that couldn't be ruled out): never shown or kept.</summary>
    Hide,

    /// <summary>Not known yet: ask again a little later.</summary>
    Pending,
}

/// <summary>What has the keyboard focus, as far as privacy goes.</summary>
/// <param name="Private">A password field, an excluded app, or something that couldn't be checked.</param>
/// <param name="App">The foreground program's file name, if known.</param>
/// <param name="Reason">Why it is private (for the log), or null.</param>
public readonly record struct FocusCheck(bool Private, string? App, [Localizable(false)] string? Reason); // Reason: for the log

/// <summary>Looks at what has the keyboard focus now.</summary>
public interface IFocusProbe : IDisposable
{
    FocusCheck Check();
}

/// <summary>
/// Decides, for each key, whether it may be shown: not when it was typed into a password field (UI Automation's
/// <c>IsPassword</c> of the focused element, which browsers report for <c>&lt;input type=password&gt;</c>) or into an
/// excluded app. Every focus change (a window coming to the front, the focus moving inside an app, a click) is a
/// trigger; a check runs on the guard's own thread after each. A key at time t is decided by the checks made after
/// the last trigger before it and after any trigger shortly after it (<see cref="Grace"/>: the focus event of a
/// click can arrive after a quickly typed key). Until those checks are done the key waits; after
/// <see cref="GiveUp"/> it is hidden, as is anything a check couldn't read (fail closed).
/// </summary>
public sealed partial class FocusGuard : IDisposable
{
    public static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(40);
    public static readonly TimeSpan GiveUp = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan Keep = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly List<long> _triggers = [];
    private readonly List<(long Start, FocusCheck Check)> _checks = [];
    private readonly AutoResetEvent _wake = new(false);
    private readonly Func<IFocusProbe> _createProbe;
    private readonly ILogger _logger;
    private readonly Thread _thread;
    private volatile bool _stopping;
    private long _checked;

    /// <param name="probe">Creates the probe on the guard's thread (UI Automation lives there).</param>
    [Localizable(false)] // the thread's name, for debuggers
    public FocusGuard(Func<IFocusProbe> probe, ILogger logger)
    {
        _createProbe = probe;
        _logger = logger;
        _thread = new Thread(Run) { IsBackground = true, Name = "Keystroke privacy" };
        _thread.SetApartmentState(ApartmentState.MTA);
        Trigger(Stopwatch.GetTimestamp());
        _thread.Start();
    }

    /// <summary>The last check's result.</summary>
    public FocusCheck? Last
    {
        get
        {
            lock (_gate)
            {
                return _checks.Count == 0 ? null : _checks[^1].Check;
            }
        }
    }

    /// <summary>Checks made so far.</summary>
    public long Checked => Interlocked.Read(ref _checked);

    /// <summary>The focus may have moved at <paramref name="timestamp"/>: check again.</summary>
    public void Trigger(long timestamp)
    {
        lock (_gate)
        {
            var at = _triggers.Count;
            while (at > 0 && _triggers[at - 1] > timestamp)
            {
                at--;
            }

            _triggers.Insert(at, timestamp);
        }

        _wake.Set();
    }

    /// <summary>Whether a key pressed at <paramref name="key"/> may be shown, at time <paramref name="now"/> (timestamps).</summary>
    public KeyVerdict Decide(long key, long now)
    {
        if (Stopwatch.GetElapsedTime(key, now) < Grace)
        {
            return KeyVerdict.Pending;
        }

        var late = Stopwatch.GetElapsedTime(key, now) > GiveUp;
        var until = key + (long)(Grace.TotalSeconds * Stopwatch.Frequency);
        lock (_gate)
        {
            var any = false;
            for (var i = _triggers.Count - 1; i >= 0; i--)
            {
                var trigger = _triggers[i];
                if (trigger > until)
                {
                    continue;
                }

                any = true;
                var check = _checks.FindIndex(c => c.Start >= trigger);
                if (check < 0)
                {
                    return late ? KeyVerdict.Hide : KeyVerdict.Pending;
                }

                if (_checks[check].Check.Private)
                {
                    return KeyVerdict.Hide;
                }

                if (trigger <= key)
                {
                    break; // the last trigger before the key: what came before it doesn't matter
                }
            }

            return any ? KeyVerdict.Show : KeyVerdict.Hide;
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _wake.Set();
        if (_thread.IsAlive && !_thread.Join(TimeSpan.FromSeconds(5)))
        {
            LogStuck();
        }

        _wake.Dispose();
    }

    private void Run()
    {
        IFocusProbe? probe = null;
        try
        {
            probe = _createProbe();
        }
        catch (Exception e) when (e is COMException or InvalidOperationException)
        {
            LogNoProbe(e);
        }

        long checkedUpTo = long.MinValue;
        while (!_stopping)
        {
            long newest;
            lock (_gate)
            {
                newest = _triggers.Count == 0 ? long.MinValue : _triggers[^1];
            }

            if (newest <= checkedUpTo)
            {
                _wake.WaitOne(TimeSpan.FromSeconds(1));
                continue;
            }

            var start = Stopwatch.GetTimestamp();
            FocusCheck result;
            try
            {
                result = probe?.Check() ?? new FocusCheck(true, null, "UI Automation is not available");
            }
            catch (Exception e) when (e is COMException or InvalidCastException or InvalidOperationException)
            {
                result = new FocusCheck(true, null, e.Message);
            }

            lock (_gate)
            {
                var changed = _checks.Count == 0 || _checks[^1].Check != result;
                _checks.Add((start, result));
                Prune(start);
                if (changed)
                {
                    LogCheck(result.Private, result.App ?? "?", result.Reason ?? "");
                }
            }

            Interlocked.Increment(ref _checked);
            checkedUpTo = newest;
        }

        probe?.Dispose();
    }

    /// <summary>Forgets triggers and checks older than <see cref="Keep"/>, keeping the newest of each from before.</summary>
    private void Prune(long now)
    {
        var cutoff = now - (long)(Keep.TotalSeconds * Stopwatch.Frequency);
        var oldTriggers = _triggers.FindLastIndex(t => t < cutoff);
        if (oldTriggers > 0)
        {
            _triggers.RemoveRange(0, oldTriggers);
        }

        var first = _triggers.Count == 0 ? cutoff : _triggers[0];
        var oldChecks = _checks.FindLastIndex(c => c.Start < first);
        if (oldChecks > 0)
        {
            _checks.RemoveRange(0, oldChecks);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Keystroke privacy: focus private {Private} in {App} {Reason}")]
    private partial void LogCheck(bool @private, string app, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Keystroke privacy: UI Automation could not be started; keys will not be shown")]
    private partial void LogNoProbe(Exception e);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Keystroke privacy: the focus check did not stop")]
    private partial void LogStuck();
}

/// <summary>
/// The real check: the foreground window's program against the excluded apps, then UI Automation's focused element
/// and its <c>IsPassword</c>. Create and use it on one MTA thread.
/// </summary>
public sealed partial class UiAutomationProbe : IFocusProbe
{
    private static readonly StrategyBasedComWrappers Wrappers = new();

    private readonly Func<string?, bool> _excluded;
    private readonly IUIAutomation _automation;
    private readonly bool _uninitialize;

    /// <param name="excluded">Whether a program (file name) is excluded.</param>
    public UiAutomationProbe(Func<string?, bool> excluded)
    {
        _excluded = excluded;
        var init = Ole32.CoInitializeEx(0, Ole32.CoinitMultithreaded);
        _uninitialize = init >= 0;
        var clsid = UiAutomationGuids.CUIAutomation;
        var iid = UiAutomationGuids.IUIAutomation;
        var hr = Ole32.CoCreateInstance(in clsid, 0, Ole32.ClsctxInprocServer, in iid, out var pointer);
        if (hr < 0)
        {
            throw new COMException("UI Automation could not be created.", hr);
        }

        try
        {
            _automation = (IUIAutomation)Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    public FocusCheck Check()
    {
        var app = WindowFacts.Program(User32.GetForegroundWindow());
        if (_excluded(app))
        {
            return new FocusCheck(true, app, "excluded app");
        }

        var element = Focused(out var hr);
        if (element is null)
        {
            return new FocusCheck(true, app, $"no focused element (0x{hr:X8})");
        }

        try
        {
            hr = element.GetCurrentIsPassword(out var password);
            return hr < 0 ? new FocusCheck(true, app, $"IsPassword failed (0x{hr:X8})")
                : password != 0 ? new FocusCheck(true, app, "password field")
                : new FocusCheck(false, app, null);
        }
        finally
        {
            ((ComObject)(object)element).FinalRelease();
        }
    }

    /// <summary>The focused element's process and class name (for tests that check the interface is read right).</summary>
    public (int ProcessId, string? ClassName, bool IsPassword)? Describe()
    {
        var element = Focused(out _);
        if (element is null)
        {
            return null;
        }

        try
        {
            element.GetCurrentProcessId(out var processId);
            element.GetCurrentClassName(out var bstr);
            var name = bstr == 0 ? null : Marshal.PtrToStringBSTR(bstr);
            if (bstr != 0)
            {
                Marshal.FreeBSTR(bstr);
            }

            element.GetCurrentIsPassword(out var password);
            return (processId, name, password != 0);
        }
        finally
        {
            ((ComObject)(object)element).FinalRelease();
        }
    }

    public void Dispose()
    {
        ((ComObject)(object)_automation).FinalRelease();
        if (_uninitialize)
        {
            Ole32.CoUninitialize();
        }
    }

    private IUIAutomationElement? Focused(out int hr)
    {
        hr = _automation.GetFocusedElement(out var pointer);
        if (hr < 0 || pointer == 0)
        {
            return null;
        }

        try
        {
            return (IUIAutomationElement)Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }
}
