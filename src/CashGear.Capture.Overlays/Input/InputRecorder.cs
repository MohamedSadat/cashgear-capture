using System.ComponentModel;
using System.Diagnostics;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Overlays;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Interop.Native;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.Overlays.Input;

/// <summary>
/// The input of one recording ("Recording input" thread): reads the hooks, puts each event on the recording clock
/// (events from a pause are left out), and fills the <see cref="InputTimeline"/> the overlays are drawn from and the
/// metadata journal. Keys wait for the <see cref="FocusGuard"/>'s verdict and, if they may be shown, become captions
/// through the <see cref="KeystrokeFormatter"/>; hidden keys go nowhere. The pointer's shape is read on moves (at most
/// every 8 ms) and every 100 ms. Without keystrokes there is no keyboard hook at all.
/// </summary>
public sealed partial class InputRecorder : IDisposable
{
    private static readonly long ShapeEvery = Stopwatch.Frequency / 10;
    private static readonly long ShapeAtMost = Stopwatch.Frequency * 8 / 1000;
    private static readonly long PruneEvery = Stopwatch.Frequency * 5;
    private static readonly TimeSpan KeepLive = TimeSpan.FromSeconds(10);

    private readonly RecordingClock _clock;
    private readonly InputTimeline _timeline;
    private readonly MetadataJournal? _journal;
    private readonly KeystrokeOverlaySettings _keys;
    private readonly CursorShapes _shapes;
    private readonly Action<TimeSpan, PointerButton>? _pressed;
    private readonly bool _live;
    private readonly ILogger _logger;
    private readonly InputHooks _hooks;
    private readonly FocusGuard? _guard;
    private readonly KeystrokeFormatter _formatter;
    private readonly KeyboardLayout? _layout;
    private readonly Queue<RawInput> _pendingKeys = new();
    private readonly Thread _thread;
    private volatile bool _stopping;
    private long _lastShape;
    private long _lastPrune;
    private (int Shape, bool Visible) _shape = (-1, true);
    private long _shown;
    private long _hidden;

    [Localizable(false)] // the thread's name, for debuggers
    private InputRecorder(
        RecordingClock clock,
        InputTimeline timeline,
        MetadataJournal? journal,
        KeystrokeOverlaySettings keys,
        CursorLibrary cursors,
        Action<TimeSpan, PointerButton>? pressed,
        bool live,
        Func<IFocusProbe>? probe,
        ILoggerFactory loggers)
    {
        _clock = clock;
        _timeline = timeline;
        _journal = journal;
        _keys = keys;
        _shapes = new CursorShapes(cursors);
        _pressed = pressed;
        _live = live;
        _logger = loggers.CreateLogger<InputRecorder>();
        var keyboard = keys.Mode != KeystrokeMode.Off;
        _formatter = new KeystrokeFormatter(keys.Mode, TimeSpan.FromSeconds(keys.FadeSeconds), keyboard ? Characters : null);
        _hooks = InputHooks.Start(keyboard, loggers.CreateLogger<InputHooks>());
        if (keyboard)
        {
            _layout = new KeyboardLayout();
            _guard = new FocusGuard(probe ?? (() => new UiAutomationProbe(keys.Excludes)), loggers.CreateLogger<FocusGuard>());
        }

        _thread = new Thread(Run) { IsBackground = true, Name = "Recording input", Priority = ThreadPriority.AboveNormal };
    }

    /// <summary>While true, keys aren't shown or kept (the toolbar's privacy switch).</summary>
    public bool KeystrokesPaused { get; set; }

    /// <summary>Keys that became (part of) a caption so far.</summary>
    public long KeysShown => Interlocked.Read(ref _shown);

    /// <summary>Keys left out for privacy so far.</summary>
    public long KeysHidden => Interlocked.Read(ref _hidden);

    public FocusGuard? Guard => _guard;

    /// <summary>Installs the hooks and starts reading them.</summary>
    /// <param name="pressed">Called (on the input thread) with the recording time of each button press, e.g. for a click sound.</param>
    /// <param name="live">Forget what is long past (the overlays only look back seconds; the journal keeps everything).</param>
    /// <param name="probe">The focus check for keys (tests); UI Automation by default.</param>
    public static InputRecorder Start(
        RecordingClock clock,
        InputTimeline timeline,
        MetadataJournal? journal,
        KeystrokeOverlaySettings keys,
        CursorLibrary cursors,
        ILoggerFactory loggers,
        Action<TimeSpan, PointerButton>? pressed = null,
        bool live = true,
        Func<IFocusProbe>? probe = null)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var recorder = new InputRecorder(clock, timeline, journal, keys, cursors, pressed, live, probe, loggers);
        recorder._thread.Start();
        return recorder;
    }

    /// <summary>The pointer as it is now, for the timeline's start (before any move).</summary>
    public static unsafe PixelPoint PointerNow()
    {
        POINT p;
        return User32.GetCursorPos(&p) ? new PixelPoint(p.X, p.Y) : default;
    }

    /// <summary>Drawing on the screen started or stopped now: clicks and keys made meanwhile aren't overlays.</summary>
    public void SetDrawing(bool on)
    {
        var time = _clock.Elapsed;
        _timeline.Drawing(time, on);
        _journal?.Drawing(time, on);
    }

    public void Dispose()
    {
        if (_stopping)
        {
            return;
        }

        _stopping = true;
        _thread.Join(TimeSpan.FromSeconds(5));
        _hooks.Dispose();
        _guard?.Dispose();
        _journal?.Flush();
        LogStopped(KeysShown, KeysHidden, _shapes.Library.Count);
    }

    private void Run()
    {
        try
        {
            var now = Stopwatch.GetTimestamp();
            Foreground(User32.GetForegroundWindow(), now, atStart: true);
            ReadShape(now);
            while (!_stopping)
            {
                _hooks.Wait(TimeSpan.FromMilliseconds(20));
                Drain();
            }

            Drain();
            ReleaseKeys(Stopwatch.GetTimestamp(), final: true);
        }
        catch (Exception e)
        {
            LogFailed(e); // overlays stop; the recording goes on
        }
    }

    private void Drain()
    {
        while (_hooks.TryRead(out var input))
        {
            Handle(input);
        }

        var now = Stopwatch.GetTimestamp();
        ReleaseKeys(now, final: false);
        if (now - _lastShape >= ShapeEvery)
        {
            ReadShape(now);
        }

        if (_live && now - _lastPrune >= PruneEvery)
        {
            _lastPrune = now;
            _timeline.Prune(_clock.Elapsed - KeepLive);
        }
    }

    private void Handle(in RawInput input)
    {
        switch (input.Kind)
        {
            case RawInputKind.Move when _clock.TryMap(input.Timestamp, out var time):
                _timeline.Move(time, new PixelPoint(input.X, input.Y));
                if (input.Timestamp - _lastShape >= ShapeAtMost)
                {
                    ReadShape(input.Timestamp);
                }

                break;
            case RawInputKind.ButtonDown:
                _guard?.Trigger(input.Timestamp);
                if (_clock.TryMap(input.Timestamp, out var down))
                {
                    var button = (PointerButton)input.Code;
                    var at = new PixelPoint(input.X, input.Y);
                    _timeline.Move(down, at);
                    _timeline.ButtonDown(down, button, at);
                    _journal?.ButtonDown(down, button, at);
                    _pressed?.Invoke(down, button);
                }

                break;
            case RawInputKind.ButtonUp:
                _guard?.Trigger(input.Timestamp);
                {
                    // A release during a pause ends the press when recording resumes.
                    var up = _clock.TryMap(input.Timestamp, out var mapped) ? mapped : _clock.MapInstant(input.Timestamp);
                    _timeline.ButtonUp(up, (PointerButton)input.Code);
                    _journal?.ButtonUp(up, (PointerButton)input.Code);
                }

                break;
            case RawInputKind.KeyDown or RawInputKind.KeyUp:
                if (input.Kind == RawInputKind.KeyDown)
                {
                    _layout?.Pressed(input.Code);
                }

                _pendingKeys.Enqueue(input);
                break;
            case RawInputKind.Foreground:
                Foreground(input.Window, input.Timestamp, atStart: false);
                break;
            case RawInputKind.Focus:
                _guard?.Trigger(input.Timestamp);
                break;
        }
    }

    /// <summary>A window came to the front: the keyboard layout follows it, and the metadata notes the app.</summary>
    private void Foreground(nint window, long timestamp, bool atStart)
    {
        _guard?.Trigger(timestamp);
        _layout?.Follow(window);
        if (!_clock.TryMap(timestamp, out var time))
        {
            if (!atStart)
            {
                return;
            }

            time = TimeSpan.Zero;
        }

        var app = WindowFacts.Program(window);
        var change = new FocusChange(time, app, _keys.Excludes(app) ? null : WindowFacts.Title(window));
        _timeline.Focus(change);
        _journal?.Focus(change);
    }

    /// <summary>Hands the keys whose verdict is known to the formatter, in order; a key still undecided holds those after it.</summary>
    private void ReleaseKeys(long now, bool final)
    {
        while (_pendingKeys.TryPeek(out var key))
        {
            var down = key.Kind == RawInputKind.KeyDown;
            if (down && !KeystrokeFormatter.IsModifierKey(key.Code))
            {
                var verdict = KeystrokesPaused ? KeyVerdict.Hide : _guard?.Decide(key.Timestamp, now) ?? KeyVerdict.Hide;
                if (verdict == KeyVerdict.Pending && !final)
                {
                    return;
                }

                if (verdict != KeyVerdict.Show)
                {
                    _pendingKeys.Dequeue();
                    Interlocked.Increment(ref _hidden);
                    continue;
                }
            }

            _pendingKeys.Dequeue();
            var time = _clock.TryMap(key.Timestamp, out var mapped) ? mapped : (TimeSpan?)null;
            if (time is null && !KeystrokeFormatter.IsModifierKey(key.Code))
            {
                continue; // pressed during a pause
            }

            // Modifiers are always followed, so a hidden or paused key never leaves Ctrl "held".
            if (_formatter.Key(time ?? _clock.Elapsed, key.Code, down) is { } caption)
            {
                Interlocked.Increment(ref _shown);
                _timeline.Caption(caption);
                _journal?.Key(caption);
            }
        }
    }

    private void ReadShape(long now)
    {
        _lastShape = now;
        var shape = _shapes.Read(out var added);
        if (added is not null)
        {
            _journal?.Cursor(added.ToMetadata());
        }

        if (shape != _shape && _clock.TryMap(now, out var time))
        {
            _shape = shape;
            _timeline.Shape(new PointerShapeChange(time, shape.Shape, shape.Visible));
        }
    }

    private string? Characters(uint virtualKey, bool shift, bool altGr) => _layout?.Characters(virtualKey, shift, altGr);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recording input stopped: {Shown} keys shown, {Hidden} left out for privacy, {Shapes} pointer shapes")]
    private partial void LogStopped(long shown, long hidden, int shapes);

    [LoggerMessage(Level = LogLevel.Error, Message = "Recording input failed; overlays stop here")]
    private partial void LogFailed(Exception e);
}
