using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;

namespace CashGear.Capture.Audio;

/// <summary>Changes to Windows' sound devices, raised on one worker thread (never Windows' own notification thread).</summary>
internal interface IAudioDeviceEvents
{
    /// <summary>The default output device (<see cref="DataFlow.Render"/>) or microphone (<see cref="DataFlow.Capture"/>) is now the device with this ID, or none (null).</summary>
    event Action<DataFlow, string?>? DefaultChanged;

    /// <summary>A device was added or became active again (plugged back in, enabled).</summary>
    event Action<string>? Arrived;

    /// <summary>A device was removed or is no longer active (unplugged, disabled).</summary>
    event Action<string>? Departed;

    /// <summary>Runs <paramref name="work"/> on the worker thread, after any events already queued.</summary>
    void Post(Action work);
}

/// <summary>
/// Listens to Windows' sound-device notifications (<c>IMMNotificationClient</c>, through NAudio). Windows calls them
/// on its own thread while holding a lock, so they are only queued there, and raised on this watcher's thread, where
/// devices can be opened and closed safely. One for the app; it lives until the app exits.
/// </summary>
internal sealed partial class AudioDeviceWatcher : IAudioDeviceEvents, IDisposable
{
    private readonly BlockingCollection<Action> _queue = [];
    private readonly ILogger _logger;
    private readonly Thread _thread;
    private readonly Lock _gate = new();
    private MMDeviceEnumerator? _enumerator;
    private MMDeviceNotificationClient? _client;
    private Action<DataFlow, string?>? _defaultChanged;
    private Action<string>? _arrived;
    private Action<string>? _departed;
    private bool _disposed;

    public AudioDeviceWatcher(ILogger<AudioDeviceWatcher> logger)
    {
        _logger = logger;
        _thread = new Thread(Run) { IsBackground = true, Name = "Sound devices" };
        _thread.Start();
    }

    public event Action<DataFlow, string?>? DefaultChanged
    {
        add
        {
            lock (_gate)
            {
                EnsureListening();
                _defaultChanged += value;
            }
        }

        remove
        {
            lock (_gate)
            {
                _defaultChanged -= value;
            }
        }
    }

    public event Action<string>? Arrived
    {
        add
        {
            lock (_gate)
            {
                EnsureListening();
                _arrived += value;
            }
        }

        remove
        {
            lock (_gate)
            {
                _arrived -= value;
            }
        }
    }

    public event Action<string>? Departed
    {
        add
        {
            lock (_gate)
            {
                EnsureListening();
                _departed += value;
            }
        }

        remove
        {
            lock (_gate)
            {
                _departed -= value;
            }
        }
    }


    public void Post(Action work)
    {
        try
        {
            _queue.Add(work);
        }
        catch (InvalidOperationException)
        {
            // disposed: the app is closing
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _client?.Dispose();
            _enumerator?.Dispose();
            _client = null;
            _enumerator = null;
        }

        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(2));
    }

    /// <summary>Registers with Windows the first time anyone subscribes (so tests and the app without a recording never do).</summary>
    private void EnsureListening()
    {
        lock (_gate)
        {
            if (_client is not null || _disposed)
            {
                return;
            }

            try
            {
                _enumerator = new MMDeviceEnumerator();
                _client = _enumerator.CreateNotificationClient(useSynchronizationContext: false);
                _client.DefaultDeviceChanged += (_, e) =>
                {
                    // Recording follows the "Default Device" of the Sound settings: multimedia for output, console for input.
                    if ((e.Flow == DataFlow.Render && e.Role == Role.Multimedia) || (e.Flow == DataFlow.Capture && e.Role == Role.Console))
                    {
                        var flow = e.Flow;
                        var id = e.DeviceId;
                        Post(() => Handlers(ref _defaultChanged)?.Invoke(flow, id));
                    }
                };
                _client.DeviceAdded += (_, e) => Post(() => Handlers(ref _arrived)?.Invoke(e.DeviceId));
                _client.DeviceRemoved += (_, e) => Post(() => Handlers(ref _departed)?.Invoke(e.DeviceId));
                _client.DeviceStateChanged += (_, e) =>
                {
                    var id = e.DeviceId;
                    var active = e.NewState == DeviceState.Active;
                    Post(() => (active ? Handlers(ref _arrived) : Handlers(ref _departed))?.Invoke(id));
                };
            }
            catch (COMException ex)
            {
                LogListenFailed(ex);
            }
        }
    }

    private T? Handlers<T>(ref T? field)
        where T : Delegate
    {
        lock (_gate)
        {
            return field;
        }
    }

    private void Run()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            try
            {
                work();
            }
#pragma warning disable CA1031 // A device that misbehaves must never take the app down.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogFailed(ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not listen for sound device changes")]
    private partial void LogListenFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Handling a sound device change failed")]
    private partial void LogFailed(Exception ex);
}
