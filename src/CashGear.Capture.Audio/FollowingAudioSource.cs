using CashGear.Capture.Audio.Resources;
using CashGear.Capture.Core.Recording;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;

namespace CashGear.Capture.Audio;

/// <summary>One sound device opened for capture, delivering packets on its own thread (see <see cref="WasapiAudioSource"/>).</summary>
internal interface IAudioEndpoint : IDisposable
{
    /// <summary>E.g. "system audio (Headphones (H5))" or "Microphone (H5)".</summary>
    string Name { get; }

    /// <summary>Starts capturing. <paramref name="failed"/> is called (once, on the capture thread) if the device stops working.</summary>
    void Start(AudioDataHandler data, Action<Exception> failed);
}

/// <param name="Endpoint">The device, opened; null when it isn't there or can't be used.</param>
/// <param name="DeviceId">The device's ID (for a default, the device that is the default now).</param>
/// <param name="Missing">Why there is no device to open (none connected, the chosen one unplugged): it may come later.</param>
/// <param name="Problem">Why the device can't be used (privacy settings, exclusive use): it won't by itself.</param>
internal sealed record EndpointOpened(IAudioEndpoint? Endpoint, string? DeviceId, string? Missing = null, string? Problem = null);

/// <summary>
/// A recording's system audio or microphone that keeps going while devices come and go. It records either Windows'
/// default device, following it when the default changes (another device chosen in Sound settings, the default
/// unplugged), or one chosen device, which it waits for while it is unplugged and records again once it is back.
/// <para>
/// While no device is recorded nothing is delivered, so the recording has silence there. The first packet from a
/// device opened after a change is marked as a discontinuity, so it lands where its timestamp says. Changes are
/// handled on the device watcher's thread; a lost device is tried again every second, so one that fails without
/// any device change (the audio service restarting) comes back too.
/// </para>
/// </summary>
internal sealed partial class FollowingAudioSource : IAudioSource
{
    private readonly DataFlow _flow;
    private readonly string? _wanted;
    private readonly Func<string?, EndpointOpened> _open;
    private readonly IAudioDeviceEvents _events;
    private readonly ILogger _logger;
    private readonly TimeSpan _retryInterval;
    private readonly Lock _gate = new();
    private IAudioEndpoint? _endpoint;
    private string? _deviceId;
    private string _name;
    private int _generation;
    private volatile bool _discontinuity;
    private AudioDataHandler? _data;
    private AudioStatusHandler? _status;
    private Timer? _retry;
    private bool _started;
    private bool _disposed;
    private bool _lostReported;
    private bool _everRecorded;
    private bool _restoredFirst;

    /// <param name="kind">System audio or a microphone.</param>
    /// <param name="flow">Output devices (system audio is their loopback) or inputs.</param>
    /// <param name="wanted">The device ID to record, or null for whichever is Windows' default.</param>
    /// <param name="open">Opens a device by ID, or (null) the default one now.</param>
    /// <param name="first">The device opened for the start of the recording (or why there is none yet).</param>
    /// <param name="waitingName">The name used while no device is recorded, e.g. "Microphone".</param>
    /// <param name="retryInterval">How often a lost device is tried again; <see cref="Timeout.InfiniteTimeSpan"/> for never (tests call <see cref="Retry"/>).</param>
    public FollowingAudioSource(
        RecordingSound kind,
        DataFlow flow,
        string? wanted,
        Func<string?, EndpointOpened> open,
        IAudioDeviceEvents events,
        EndpointOpened first,
        string waitingName,
        ILogger logger,
        TimeSpan? retryInterval = null)
    {
        Kind = kind;
        _flow = flow;
        _wanted = wanted;
        _open = open;
        _events = events;
        _logger = logger;
        _retryInterval = retryInterval ?? TimeSpan.FromSeconds(1);
        _endpoint = first.Endpoint;
        _deviceId = first.DeviceId;
        _name = first.Endpoint?.Name ?? waitingName;
        _everRecorded = first.Endpoint is not null;
        _lostReported = first.Endpoint is null; // a device missing at the start is in the recording's note already
    }

    public string Name
    {
        get
        {
            lock (_gate)
            {
                return _name;
            }
        }
    }

    public RecordingSound Kind { get; }

    /// <summary>True while a device is being recorded (for tests).</summary>
    internal bool IsRecordingDevice
    {
        get
        {
            lock (_gate)
            {
                return _endpoint is not null;
            }
        }
    }

    private bool FollowsDefault => _wanted is null;

    public void Start(AudioDataHandler data, AudioStatusHandler status)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _data = data;
            _status = status;
            _started = true;
            _events.DefaultChanged += OnDefaultChanged;
            _events.Arrived += OnArrived;
            _events.Departed += OnDeparted;
            if (_endpoint is null)
            {
                StartRetrying();
            }
            else
            {
                StartEndpoint();
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
            _events.DefaultChanged -= OnDefaultChanged;
            _events.Arrived -= OnArrived;
            _events.Departed -= OnDeparted;
            _retry?.Dispose();
            _retry = null;
            _endpoint?.Dispose();
            _endpoint = null;
        }
    }

    /// <summary>Tries to record a device again while none is (on the watcher's thread, or directly from tests).</summary>
    internal void Retry()
    {
        lock (_gate)
        {
            if (_disposed || !_started || _endpoint is not null)
            {
                return;
            }

            if (!TryOpen(_wanted, out var how) && !_lostReported)
            {
                // Following the default: Windows names a new default a moment after the old one goes, so the loss is
                // only reported if nothing took over by the first retry.
                _lostReported = true;
                Report(AudioSourceStatus.Lost, Kind == RecordingSound.Microphone ? AudioStrings.NoOtherMicrophone(_name) : AudioStrings.NoOtherOutputDevice(_name));
            }
            else if (how is { } status)
            {
                Report(status, Message(status));
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
                    return; // a device already replaced
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

    /// <summary>Opens <paramref name="id"/> (null: the default now) and records it; under the lock. <paramref name="how"/> says what to tell the user.</summary>
    private bool TryOpen(string? id, out AudioSourceStatus? how)
    {
        how = null;
        var opened = _open(id);
        if (opened.Endpoint is not { } endpoint)
        {
            LogStillMissing(_name, opened.Missing ?? opened.Problem ?? "");
            return false;
        }

        var switched = FollowsDefault && _deviceId is not null && !string.Equals(opened.DeviceId, _deviceId, StringComparison.OrdinalIgnoreCase);
        var wasLost = _lostReported;
        _endpoint = endpoint;
        _deviceId = opened.DeviceId;
        _name = endpoint.Name;
        var first = !_everRecorded;
        _everRecorded = true;
        _restoredFirst = first;
        _discontinuity = true;
        _lostReported = false;
        _retry?.Dispose();
        _retry = null;
        StartEndpoint();
        LogOpened(_name, opened.DeviceId ?? "");
        how = switched ? AudioSourceStatus.Switched : wasLost ? AudioSourceStatus.Restored : null;
        return true;
    }

    private string Message(AudioSourceStatus status) => status switch
    {
        AudioSourceStatus.Switched => Kind == RecordingSound.Microphone
            ? AudioStrings.NewDefaultMicrophone(_name)
            : AudioStrings.NewDefaultOutputDevice(DeviceName(_name)),
        _ => _restoredFirst ? AudioStrings.DeviceConnected(_name) : AudioStrings.DeviceWorksAgain(_name),
    };

    /// <summary>"Headphones (H5)" from "system audio (Headphones (H5))" (<see cref="AudioStrings.SystemAudioDevice"/>).</summary>
    private static string DeviceName(string name)
    {
        var around = AudioStrings.SystemAudioDevice("\0").Split('\0'); // the text before and after the device's name
        return around is [var before, var after] && name.Length >= before.Length + after.Length
            && name.StartsWith(before, StringComparison.Ordinal) && name.EndsWith(after, StringComparison.Ordinal)
            ? name[before.Length..^after.Length]
            : name;
    }

    /// <summary>The device stopped working (on the watcher's thread).</summary>
    private void OnFailed(int generation, Exception ex)
    {
        lock (_gate)
        {
            if (_disposed || generation != _generation || _endpoint is null)
            {
                return;
            }

            Lose(AudioStrings.DeviceStopped(_name, ex.Message.Trim()));
        }
    }

    private void OnDeparted(string id)
    {
        lock (_gate)
        {
            if (_disposed || _endpoint is null || !string.Equals(id, _deviceId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Lose(AudioStrings.DeviceDisconnected(_name));
        }
    }

    private void OnArrived(string id)
    {
        lock (_gate)
        {
            if (!_disposed && _endpoint is null && (FollowsDefault || string.Equals(id, _wanted, StringComparison.OrdinalIgnoreCase)))
            {
                Retry();
            }
        }
    }

    private void OnDefaultChanged(DataFlow flow, string? id)
    {
        lock (_gate)
        {
            if (_disposed || !FollowsDefault || flow != _flow)
            {
                return;
            }

            if (id is not null && _endpoint is not null && string.Equals(id, _deviceId, StringComparison.OrdinalIgnoreCase))
            {
                return; // already recording it
            }

            if (_endpoint is not null)
            {
                Close();
            }

            if (id is null)
            {
                if (!_lostReported)
                {
                    _lostReported = true;
                    Report(AudioSourceStatus.Lost, Kind == RecordingSound.Microphone ? AudioStrings.NoDefaultMicrophone(_name) : AudioStrings.NoDefaultOutputDevice(_name));
                }

                StartRetrying();
                return;
            }

            if (TryOpen(null, out var how))
            {
                Report(how ?? AudioSourceStatus.Switched, Message(how ?? AudioSourceStatus.Switched));
            }
            else
            {
                StartRetrying();
            }
        }
    }

    /// <summary>The device is gone: stop it, tell the user <paramref name="message"/> (or, following the default, wait a moment for a new default), and keep trying; under the lock.</summary>
    private void Lose(string message)
    {
        Close();
        if (FollowsDefault)
        {
            _lostReported = false; // reported by the first retry unless a new default takes over first
        }
        else
        {
            _lostReported = true;
            Report(AudioSourceStatus.Lost, message);
        }

        StartRetrying();
    }

    private void Close()
    {
        _generation++;
        var endpoint = _endpoint;
        _endpoint = null;
        endpoint?.Dispose();
    }

    private void StartRetrying()
    {
        if (_retry is null && _retryInterval != Timeout.InfiniteTimeSpan)
        {
            _retry = new Timer(_ => _events.Post(Retry), null, _retryInterval, _retryInterval);
        }
    }

    private void Report(AudioSourceStatus status, string message)
    {
        LogStatus(status, message);
        _status?.Invoke(status, message);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Recording sound from {Name} ({Id})")]
    private partial void LogOpened(string name, string id);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Name} is still not available: {Reason}")]
    private partial void LogStillMissing(string name, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recording sound {Status}: {Message}")]
    private partial void LogStatus(AudioSourceStatus status, string message);
}
