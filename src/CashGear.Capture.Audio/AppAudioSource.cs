using CashGear.Capture.Core.Recording;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.Audio;

/// <summary>
/// One app's sound by itself, or everything but it, through WASAPI process loopback (the app's process tree, on any
/// output device). The app is found by its program file name and looked for again every second: one that wasn't
/// running at the start is recorded from when it starts, and one that is restarted is followed to its new process.
/// Leaving an app out while it isn't running leaves out this app's own process instead, which plays nothing, so
/// everything is recorded until the app starts.
/// </summary>
internal sealed partial class AppAudioSource : IAudioSource
{
    private readonly string _exe;
    private readonly string _appName;
    private readonly bool _exclude;
    private readonly Func<int, bool, EndpointOpened> _open;
    private readonly Func<string, int?> _find;
    private readonly IAudioDeviceEvents _events;
    private readonly ILogger _logger;
    private readonly TimeSpan _pollInterval;
    private readonly int _ownProcess = Environment.ProcessId;
    private readonly Lock _gate = new();
    private IAudioEndpoint? _endpoint;
    private int _target;
    private int _generation;
    private volatile bool _discontinuity;
    private AudioDataHandler? _data;
    private AudioStatusHandler? _status;
    private Timer? _poll;
    private bool _disposed;
    private bool _lostReported;

    /// <param name="exe">The app's program file name, e.g. <c>spotify.exe</c>.</param>
    /// <param name="appName">Its name for people, e.g. "Spotify".</param>
    /// <param name="exclude">Record everything except the app.</param>
    /// <param name="open">Opens process loopback for a process tree (true: everything except it).</param>
    /// <param name="find">The process at the top of the app's tree, or null when it isn't running.</param>
    /// <param name="first">What was opened for the start (opened by the factory, so a failure is known before recording).</param>
    /// <param name="firstTarget">The process <paramref name="first"/> is for (0 if none).</param>
    /// <param name="pollInterval">How often the app is looked for; <see cref="Timeout.InfiniteTimeSpan"/> for never (tests call <see cref="Poll"/>).</param>
    public AppAudioSource(
        string exe,
        string appName,
        bool exclude,
        Func<int, bool, EndpointOpened> open,
        Func<string, int?> find,
        IAudioDeviceEvents events,
        EndpointOpened first,
        int firstTarget,
        ILogger logger,
        TimeSpan? pollInterval = null)
    {
        _exe = exe;
        _appName = appName;
        _exclude = exclude;
        _open = open;
        _find = find;
        _events = events;
        _logger = logger;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
        _endpoint = first.Endpoint;
        _target = first.Endpoint is null ? 0 : firstTarget;
        Name = exclude ? $"system audio except {appName}" : $"{appName}'s sound";
    }

    public string Name { get; }

    public RecordingSound Kind => RecordingSound.System;

    /// <summary>The process whose tree is recorded (or left out) now; 0 when none (for tests).</summary>
    internal int Target
    {
        get
        {
            lock (_gate)
            {
                return _endpoint is null ? 0 : _target;
            }
        }
    }

    public void Start(AudioDataHandler data, AudioStatusHandler status)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _data = data;
            _status = status;
            if (_endpoint is not null)
            {
                StartEndpoint();
            }

            if (_pollInterval != Timeout.InfiniteTimeSpan)
            {
                _poll = new Timer(_ => _events.Post(Poll), null, _pollInterval, _pollInterval);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _generation++;
            _poll?.Dispose();
            _poll = null;
            _endpoint?.Dispose();
            _endpoint = null;
        }
    }

    /// <summary>Looks for the app again and follows it to a new process (on the watcher's thread, or from tests).</summary>
    internal void Poll()
    {
        lock (_gate)
        {
            if (_disposed || _data is null)
            {
                return;
            }

            var found = _find(_exe);
            var target = found ?? (_exclude ? _ownProcess : 0);
            if (_endpoint is not null && (target == _target || (target == 0 && !_exclude)))
            {
                return; // still the same process (an app that quit keeps its silent recording until it starts again)
            }

            if (target == 0)
            {
                return; // not running: nothing to record until it starts
            }

            var replacing = _endpoint is not null;
            Close();
            var opened = _open(target, _exclude);
            if (opened.Endpoint is not { } endpoint)
            {
                LogOpenFailed(_appName, target, opened.Problem ?? opened.Missing ?? "");
                if (!_lostReported)
                {
                    _lostReported = true;
                    Report(AudioSourceStatus.Lost, $"{Name} couldn't be recorded ({opened.Problem ?? opened.Missing}). It is silent in the recording until it can be.");
                }

                return;
            }

            var wasLost = _lostReported;
            _endpoint = endpoint;
            _target = target;
            _lostReported = false;
            _discontinuity = true;
            StartEndpoint();
            LogFollowed(_appName, target, _exclude);
            if (_exclude)
            {
                if (found is not null && replacing)
                {
                    Report(AudioSourceStatus.Switched, $"{_appName} is running now, so its sound is left out of the recording.");
                }
                else if (wasLost)
                {
                    Report(AudioSourceStatus.Restored, $"{Name} is being recorded again.");
                }
            }
            else
            {
                Report(AudioSourceStatus.Restored, replacing ? $"{_appName} was started again; its sound is being recorded again."
                    : wasLost ? $"{Name} is being recorded again."
                    : $"{_appName} started; its sound is being recorded.");
            }
        }
    }

    /// <summary>Starts the current endpoint; under the lock.</summary>
    private void StartEndpoint()
    {
        var generation = ++_generation;
        _endpoint!.Start(
            (samples, start, discontinuity) =>
            {
                if (generation != Volatile.Read(ref _generation))
                {
                    return;
                }

                if (_discontinuity)
                {
                    _discontinuity = false;
                    discontinuity = true;
                }

                _data!(samples, start, discontinuity);
            },
            ex => _events.Post(() => OnFailed(generation, ex)));
    }

    private void OnFailed(int generation, Exception ex)
    {
        lock (_gate)
        {
            if (_disposed || generation != _generation)
            {
                return;
            }

            Close();
            _target = 0;
            _lostReported = true;
            Report(AudioSourceStatus.Lost, $"{Name} stopped ({ex.Message.Trim()}). It is silent in the recording until it can be recorded again.");
        }
    }

    private void Close()
    {
        _generation++;
        var endpoint = _endpoint;
        _endpoint = null;
        endpoint?.Dispose();
    }

    private void Report(AudioSourceStatus status, string message)
    {
        LogStatus(status, message);
        _status?.Invoke(status, message);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Recording the sound of {App}: process {Process}, leaving it out {Exclude}")]
    private partial void LogFollowed(string app, int process, bool exclude);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not record the sound of {App} (process {Process}): {Reason}")]
    private partial void LogOpenFailed(string app, int process, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "App sound {Status}: {Message}")]
    private partial void LogStatus(AudioSourceStatus status, string message);
}
