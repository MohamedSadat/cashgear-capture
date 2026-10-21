using CashGear.Capture.Audio.Resources;
using CashGear.Capture.Core.Audio;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.Audio;

/// <summary>
/// Settings' meters and microphone test. It opens the same sources a recording would (so what the meters show is
/// what would be recorded: the chosen device, app, and processing) and only measures their levels. The microphone
/// test taps the microphone as captured, before processing, so the processing can be tried on it afterwards.
/// </summary>
internal sealed partial class AudioMonitor(IAudioSourceFactory sources, MicrophoneProcessors processors, ILogger<AudioMonitor> logger) : IAudioMonitor
{
    private readonly Lock _gate = new();
    private readonly LevelAccumulator _system = new();
    private readonly LevelAccumulator _microphone = new();
    private IAudioSource? _systemSource;
    private IAudioSource? _microphoneSource;
    private MicrophoneChain? _chain;
    private string? _microphoneId;
    private Action<ReadOnlySpan<float>>? _tap;
    private string? _problem;

    public bool CanSuppressNoise => processors.CanSuppressNoise;

    public string? Problem
    {
        get
        {
            lock (_gate)
            {
                return _problem;
            }
        }
    }

    public void Start(MonitorRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Stop();
        var problems = new List<string>();
        IAudioSource? system = null;
        IAudioSource? microphone = null;
        if (request.SystemAudio)
        {
            var opened = request.Scope != SystemAudioScope.All && request.App is { } app
                ? sources.OpenAppAudio(app, request.AppName ?? app, request.Scope == SystemAudioScope.AllExceptApp)
                : sources.OpenSystemAudio(request.SystemAudioDevice);
            system = opened.Source;
            Note(opened.Note);
        }

        MicrophoneChain? chain = null;
        if (request.Microphone is { } id)
        {
            var opened = sources.OpenMicrophone(id);
            microphone = opened.Source;
            Note(opened.Note);
            chain = microphone is null ? null : processors.Create(request.Processing);
        }

        lock (_gate)
        {
            _systemSource = system;
            _microphoneSource = microphone;
            _chain = chain;
            _microphoneId = microphone is null ? null : request.Microphone;
            _problem = problems.Count == 0 ? null : string.Join(" ", problems);
            _system.Take();
            _microphone.Take();
        }

        system?.Start(OnSystem, OnStatus);
        microphone?.Start(OnMicrophone, OnStatus);
        LogStarted(system?.Name ?? "none", microphone?.Name ?? "none");

        void Note(string? note)
        {
            if (note is not null)
            {
                problems.Add(note);
            }
        }
    }

    public void SetProcessing(MicrophoneProcessing processing)
    {
        lock (_gate)
        {
            _chain?.Settings = processing;
        }
    }

    public void Stop()
    {
        IAudioSource? system;
        IAudioSource? microphone;
        MicrophoneChain? chain;
        lock (_gate)
        {
            system = _systemSource;
            microphone = _microphoneSource;
            chain = _chain;
            _systemSource = null;
            _microphoneSource = null;
            _chain = null;
            _microphoneId = null;
        }

        // Outside the lock: stopping a source waits for its capture thread, which may be waiting for the lock.
        system?.Dispose();
        microphone?.Dispose();
        chain?.Dispose();
    }

    public SoundLevels TakeLevels()
    {
        lock (_gate)
        {
            return new SoundLevels(_system.Take(), _microphone.Take(), default);
        }
    }

    public async Task<float[]> RecordMicrophoneAsync(string microphone, TimeSpan length, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(microphone);
        var wanted = (int)(length.TotalSeconds * AudioMixer.SampleRate) * AudioMixer.Channels;
        var recorded = new List<float>(wanted);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Tap(ReadOnlySpan<float> samples)
        {
            var take = Math.Min(samples.Length, wanted - recorded.Count);
            for (var i = 0; i < take; i++)
            {
                recorded.Add(samples[i]);
            }

            if (recorded.Count >= wanted)
            {
                done.TrySetResult();
            }
        }

        // The meters may be listening to this microphone already; otherwise it is opened just for the test.
        IAudioSource? own = null;
        lock (_gate)
        {
            if (_microphoneSource is not null && _microphoneId == microphone)
            {
                _tap = Tap;
            }
        }

        if (_tap is null)
        {
            var opened = sources.OpenMicrophone(microphone);
            own = opened.Source ?? throw new InvalidOperationException(opened.Note ?? AudioStrings.MicrophoneUnusable);
            own.Start((samples, _, _) => Tap(samples), (_, _) => { });
        }

        try
        {
            using var registration = ct.Register(() => done.TrySetCanceled(ct));
            using var timeout = new CancellationTokenSource(length + TimeSpan.FromSeconds(5));
            using var late = timeout.Token.Register(() => done.TrySetException(new TimeoutException(AudioStrings.MicrophoneSentNoSound)));
            await done.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _tap = null;
            }

            own?.Dispose();
        }

        lock (_gate)
        {
            return [.. recorded];
        }
    }

    public float[] Process(float[] recorded, MicrophoneProcessing processing)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        using var chain = processors.CreateOffline(processing);
        return chain.ProcessAll(recorded);
    }

    public Task PlayAsync(float[] samples, CancellationToken ct = default) => TestPlayback.PlayAsync(samples, ct);

    public void Dispose() => Stop();

    private void OnSystem(ReadOnlySpan<float> samples, long start, bool discontinuity)
    {
        lock (_gate)
        {
            _system.Add(samples);
        }
    }

    private void OnMicrophone(ReadOnlySpan<float> samples, long start, bool discontinuity)
    {
        MicrophoneChain? chain;
        lock (_gate)
        {
            _tap?.Invoke(samples);
            chain = _chain;
        }

        // The chain is only ever fed from this (the microphone's capture) thread.
        chain?.Process(samples, start, discontinuity, (processed, _, _) =>
        {
            lock (_gate)
            {
                _microphone.Add(processed);
            }
        });
    }

    private void OnStatus(AudioSourceStatus status, string message)
    {
        lock (_gate)
        {
            _problem = status == AudioSourceStatus.Lost ? message : null;
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Settings' meters listen to {System} and {Microphone}")]
    private partial void LogStarted(string system, string microphone);
}
