using System.Diagnostics;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Engine.Capture.Wgc;
using CashGear.Capture.Interop.Native;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.Recording;

/// <summary>The mouse as the click highlighter needs it.</summary>
public readonly record struct MouseSnapshot(PixelPoint Cursor, bool LeftDown, bool LeftClicked, bool RightDown, bool RightClicked);

public interface IMouseState
{
    MouseSnapshot Read();
}

internal sealed unsafe class Win32MouseState : IMouseState
{
    public MouseSnapshot Read()
    {
        POINT p;
        User32.GetCursorPos(&p);
        var left = User32.GetAsyncKeyState(User32.VkLButton);
        var right = User32.GetAsyncKeyState(User32.VkRButton);
        return new MouseSnapshot(new PixelPoint(p.X, p.Y), (left & 0x8000) != 0, (left & 1) != 0, (right & 0x8000) != 0, (right & 1) != 0);
    }
}

/// <summary>
/// Records an area of one monitor to MP4: a <see cref="WgcAreaStream"/> keeps the latest picture, and a writer
/// thread hands it to ffmpeg at a steady frame rate (<see cref="FramePacer"/>), drawing click highlights first.
/// Pausing stops the clock, so the video simply continues after the pause. Memory use is fixed for the whole
/// recording: two frame buffers and one staging texture (plus a few seconds of sound per sound source).
/// Sound (system audio, a microphone) is placed on the same clock by an <see cref="AudioMixer"/> and written to
/// ffmpeg's second input by its own thread; see <see cref="Session"/>.
/// </summary>
internal sealed partial class ScreenRecorder(
    IFfmpeg ffmpeg,
    WgcInterop interop,
    IMouseState mouse,
    IAudioSourceFactory audio,
    ILoggerFactory loggers) : IScreenRecorder
{
    private static readonly TimeSpan FinishTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SoundConnectTimeout = TimeSpan.FromSeconds(10);

    private readonly ILogger _logger = loggers.CreateLogger<ScreenRecorder>();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Session? _session;

    public bool IsRecording => _session is not null;

    public bool IsPaused => _session?.Paused ?? false;

    public TimeSpan Elapsed => _session?.Clock.Elapsed ?? TimeSpan.Zero;

    public RecordingSound Sound => _session?.Sound ?? RecordingSound.None;

    public string? SoundNote => _session?.SoundNote;

    public bool MicrophoneMuted
    {
        get => _session?.MicrophoneMuted ?? false;
        set
        {
            if (_session is { } session)
            {
                session.MicrophoneMuted = value;
            }
        }
    }

    public event EventHandler? StateChanged;

    public float TakeSoundLevel() => _session?.TakeSoundLevel() ?? 0;

    public async Task StartAsync(RecordingOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_session is not null)
            {
                throw new InvalidOperationException("A recording is already running.");
            }

            var path = ffmpeg.Path ?? throw new CaptureException($"Screen recording needs ffmpeg.exe in {ffmpeg.ExpectedPath}.");
            var (width, height) = FfmpegArguments.EvenSize(options.Area.Width, options.Area.Height);
            var area = options.Area with { Width = width, Height = height };
            var encoder = await ffmpeg.ChooseEncoderAsync(options.Encoder, ct).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.OutputPath))!);

            // Sound devices are opened off the caller's (possibly UI) thread.
            var (sources, soundNote) = await Task.Run(() => OpenSound(options), ct).ConfigureAwait(false);
            WgcAreaStream stream;
            try
            {
                stream = await WgcAreaStream.StartAsync(interop, options.Monitor, area, options.IncludeCursor, loggers, ct).ConfigureAwait(false);
            }
            catch
            {
                DisposeAll(sources);
                throw;
            }

            var pipe = sources.Count > 0 ? new FfmpegAudioPipe() : null;
            FfmpegProcess process;
            try
            {
                process = FfmpegProcess.Start(
                    path, FfmpegArguments.Record(width, height, options.Fps, options.Quality, encoder, options.OutputPath, pipe?.Path), _logger);
            }
            catch
            {
                stream.Dispose();
                pipe?.Dispose();
                DisposeAll(sources);
                throw;
            }

            var session = new Session(options with { Area = area }, encoder, stream, process, pipe, sources, soundNote, mouse, _logger);
            session.Failed += (_, _) => OnStateChanged();
            try
            {
                session.Start();
                await session.WaitForSoundInputAsync(SoundConnectTimeout, ct).ConfigureAwait(false);
            }
            catch
            {
                session.Abort(); // kills ffmpeg, which may still be waiting for frames or sound
                throw;
            }

            _session = session;
            LogStarted(area, options.Monitor.ShortName, options.Fps, FfmpegArguments.Name(encoder), options.Quality, options.OutputPath);
            LogSound(sources.Count == 0 ? "none" : string.Join(", ", sources.Select(s => s.Name)), soundNote ?? string.Empty);
        }
        finally
        {
            _gate.Release();
        }

        OnStateChanged();
    }

    public void Pause()
    {
        if (_session is { Paused: false } session)
        {
            session.Pause();
            OnStateChanged();
        }
    }

    public void Resume()
    {
        if (_session is { Paused: true } session)
        {
            session.Resume();
            OnStateChanged();
        }
    }

    public async Task<RecordingResult> StopAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        Session session;
        try
        {
            session = _session ?? throw new InvalidOperationException("Nothing is being recorded.");
            _session = null;
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            var result = await session.StopAsync(FinishTimeout).ConfigureAwait(false);
            LogFinished(result.Path, result.Duration, result.FramesWritten, result.FramesDropped, result.DropRate, result.FramesRepeated, result.FileBytes);
            return result;
        }
        finally
        {
            OnStateChanged();
        }
    }

    private static void DisposeAll(IEnumerable<IAudioSource> sources)
    {
        foreach (var source in sources)
        {
            source.Dispose();
        }
    }

    /// <summary>The sound devices the options ask for; any that can't be used are explained in the note instead.</summary>
    private (List<IAudioSource> Sources, string? Note) OpenSound(RecordingOptions options)
    {
        var sources = new List<IAudioSource>();
        var problems = new List<string>();
        if (options.SystemAudio)
        {
            Add(audio.OpenSystemAudio());
        }

        if (options.Microphone is { } microphone)
        {
            Add(audio.OpenMicrophone(microphone));
        }

        if (problems.Count == 0)
        {
            return (sources, null);
        }

        var note = string.Join(" ", problems) + (sources.Count == 0 ? " The video has no sound." : string.Empty);
        return (sources, note);

        void Add((IAudioSource? Source, string? Problem) opened)
        {
            if (opened.Source is { } source)
            {
                sources.Add(source);
            }
            else if (opened.Problem is { } problem)
            {
                problems.Add(problem);
            }
        }
    }

    private void OnStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recording {Area} on {Monitor} at {Fps} fps with {Encoder} (quality {Quality}) to {Path}")]
    private partial void LogStarted(PixelRect area, string monitor, int fps, string encoder, int quality, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recording sound: {Sources} {Note}")]
    private partial void LogSound(string sources, string note);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recording saved: {Path}, {Duration}, {Written} frames, {Dropped} dropped ({DropRate:P1}), {Repeated} unchanged, {Bytes} bytes")]
    private partial void LogFinished(string path, TimeSpan duration, long written, long dropped, double dropRate, long repeated, long bytes);

    /// <summary>
    /// One recording: the capture, the encoder and the writer threads between them.
    /// <para>
    /// Sound: each source's packets are placed by their capture time on the recording clock (sound from a pause is
    /// cut out), and a second writer thread mixes them up to <see cref="AudioSchedule.Latency"/> behind the clock
    /// and writes them to ffmpeg's sound pipe. Silence (loopback sends nothing while nothing plays) is filled by the
    /// clock. For each video frame the pacer drops, the same time is left out of the sound, and at Stop the video is
    /// written up to the frame due at the stop and the sound to exactly the same length.
    /// </para>
    /// <para>
    /// ffmpeg stops reading an input whose stream is ahead of the other, so neither writer ever waits for the other:
    /// the sound follows the clock only, a paused or stopped clock lets the sound catch up to it, the sound input is
    /// closed at Stop without waiting for the video, and the video input is closed without waiting for the sound.
    /// </para>
    /// </summary>
    private sealed partial class Session
    {
        private const int MixChunkFrames = AudioMixer.SampleRate / 10;

        private readonly RecordingOptions _options;
        private readonly VideoEncoder _encoder;
        private readonly WgcAreaStream _stream;
        private readonly FfmpegProcess _process;
        private readonly FfmpegAudioPipe? _pipe;
        private readonly IReadOnlyList<IAudioSource> _sources;
        private readonly IMouseState _mouse;
        private readonly ILogger _logger;
        private readonly FramePacer _pacer;
        private readonly ClickHighlighter _clicks = new();
        private readonly ManualResetEventSlim _running = new(initialState: true);
        private readonly Stopwatch _wallClock = new();
        private readonly object _pacerGate = new();
        private readonly AudioMixer? _mixer;
        private AudioTrack? _microphone;
        private readonly TaskCompletionSource? _soundConnected;
        private readonly CancellationTokenSource _abort = new();
        private readonly int _soundPerFrame;
        private Thread? _writer;
        private Thread? _soundWriter;
        private volatile bool _stopping;
        private volatile string? _soundNote;
        private bool _timerPeriod;
        private long _stillSince;
        private long _finalFrames = -1;
        private long _skippedFrames;
        private long _repeated;
        private Exception? _failure;

        public Session(
            RecordingOptions options,
            VideoEncoder encoder,
            WgcAreaStream stream,
            FfmpegProcess process,
            FfmpegAudioPipe? pipe,
            IReadOnlyList<IAudioSource> sources,
            string? soundNote,
            IMouseState mouse,
            ILogger logger)
        {
            _options = options;
            _encoder = encoder;
            _stream = stream;
            _process = process;
            _pipe = pipe;
            _sources = sources;
            _soundNote = soundNote;
            _mouse = mouse;
            _logger = logger;
            _pacer = new FramePacer(options.Fps);
            if (pipe is not null)
            {
                _mixer = new AudioMixer();
                _soundPerFrame = AudioSchedule.FramesPerVideoFrame(options.Fps);
                _soundConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                foreach (var source in sources)
                {
                    Sound |= source.Kind;
                }
            }
        }

        /// <summary>Recording time: runs only while not paused.</summary>
        public RecordingClock Clock { get; } = new(TimeProvider.System);

        public bool Paused { get; private set; }

        public RecordingSound Sound { get; }

        public string? SoundNote => _soundNote;

        public bool MicrophoneMuted
        {
            get => _microphone?.Muted ?? false;
            set
            {
                if (_microphone is not null)
                {
                    _microphone.Muted = value;
                }
            }
        }

        /// <summary>Raised (on a writer thread) when recording fails, e.g. ffmpeg quits.</summary>
        public event EventHandler? Failed;

        public float TakeSoundLevel() => _mixer?.TakePeak() ?? 0;

        public void Start()
        {
            Winmm.TimeBeginPeriod(1); // 1 ms sleeps: frames on time even at 60 fps
            _timerPeriod = true;
            Clock.Start();
            _wallClock.Start();
            _writer = new Thread(WriteFrames) { IsBackground = true, Name = "Recording writer", Priority = ThreadPriority.AboveNormal };
            _writer.Start();
            if (_mixer is null)
            {
                return;
            }

            _soundWriter = new Thread(WriteSound) { IsBackground = true, Name = "Recording sound writer", Priority = ThreadPriority.AboveNormal };
            _soundWriter.Start();
            foreach (var source in _sources)
            {
                var track = _mixer.AddTrack(source.Name);
                if (source.Kind == RecordingSound.Microphone)
                {
                    _microphone = track;
                }

                source.Start((samples, start, discontinuity) => OnSound(track, samples, start, discontinuity), ex => OnSoundFailed(source, ex));
            }
        }

        /// <summary>Waits until ffmpeg has opened the sound pipe (it does so after reading the first video frame).</summary>
        public async Task WaitForSoundInputAsync(TimeSpan timeout, CancellationToken ct)
        {
            if (_soundConnected is null)
            {
                return;
            }

            var done = await Task.WhenAny(_soundConnected.Task, _process.Exited, Task.Delay(timeout, ct)).ConfigureAwait(false);
            if (done == _soundConnected.Task)
            {
                await done.ConfigureAwait(false);
                return;
            }

            ct.ThrowIfCancellationRequested();
            var why = done == _process.Exited ? "ffmpeg quit" : $"ffmpeg didn't open it within {timeout.TotalSeconds:0} s";
            var messages = _process.Messages;
            throw new CaptureException($"The sound couldn't be passed to ffmpeg ({why}).{(messages.Length > 0 ? Environment.NewLine + messages : string.Empty)}");
        }

        public void Pause()
        {
            lock (_pacerGate)
            {
                Paused = true;
                Clock.Stop();
                _stillSince = Stopwatch.GetTimestamp();
            }

            _running.Reset();
        }

        public void Resume()
        {
            lock (_pacerGate)
            {
                Paused = false;
                Clock.Start();
                _stillSince = 0;
            }

            _running.Set();
        }

        public async Task<RecordingResult> StopAsync(TimeSpan timeout)
        {
            lock (_pacerGate)
            {
                _stopping = true;
                Clock.Stop();
                if (_stillSince == 0)
                {
                    _stillSince = Stopwatch.GetTimestamp();
                }

                if (_mixer is not null)
                {
                    // The video ends with the frame due at the stop, and the sound at the same moment.
                    _finalFrames = FramePacer.Due(Clock.Elapsed, _options.Fps) - _pacer.Dropped;
                }
            }

            _running.Set();
            LogStopStep($"stopping at {Clock.Elapsed}, final frames {_finalFrames}");
            if (!await JoinAsync(_writer, timeout).ConfigureAwait(false))
            {
                _process.Dispose(); // a write that never returns: kill ffmpeg so the thread ends
                await JoinAsync(_writer, timeout).ConfigureAwait(false);
            }

            LogStopStep($"video writer done, {_pacer.Written} frames");
            EndTimerPeriod();
            _process.CloseInput(); // the encoder flushes, so ffmpeg takes the rest of the sound too
            _stream.Dispose();
            if (!await JoinAsync(_soundWriter, timeout).ConfigureAwait(false))
            {
                _process.Dispose();
                await JoinAsync(_soundWriter, timeout).ConfigureAwait(false);
            }

            LogStopStep("sound writer done");
            DisposeSound();
            var exitCode = await _process.WaitForExitAsync(timeout).ConfigureAwait(false);
            LogStopStep($"ffmpeg exited with {exitCode}");
            var messages = _process.Messages;
            _process.Dispose();
            _running.Dispose();
            if (_mixer is not null)
            {
                var tracks = _mixer.Tracks;
                LogSoundFinished(
                    TimeSpan.FromSeconds((double)_mixer.Mixed / AudioMixer.SampleRate),
                    _mixer.Mixed,
                    _mixer.Skipped,
                    tracks.Sum(t => t.LateFrames),
                    tracks.Sum(t => t.OverflowFrames));
            }

            var file = new FileInfo(_options.OutputPath);
            if (_failure is not null || exitCode != 0 || _pacer.Written == 0 || !file.Exists)
            {
                TryDelete(file);
                var reason = _failure?.Message ?? (exitCode != 0 ? $"ffmpeg exit code {exitCode}" : "no frames were recorded");
                var message = $"The recording could not be saved ({reason}).{(messages.Length > 0 ? Environment.NewLine + messages : string.Empty)}";
                throw _failure is null ? new CaptureException(message) : new CaptureException(message, _failure);
            }

            return new RecordingResult(
                file.FullName,
                TimeSpan.FromSeconds((double)_pacer.Written / _options.Fps),
                _pacer.Written,
                _pacer.Dropped,
                _repeated,
                _encoder,
                file.Length,
                Sound,
                _soundNote);
        }

        /// <summary>Undoes a start that failed half way: kills ffmpeg, ends the threads and releases everything.</summary>
        public void Abort()
        {
            _stopping = true;
            _abort.Cancel();
            _running.Set();
            _process.Dispose();
            _writer?.Join();
            _soundWriter?.Join();
            Clock.Stop();
            EndTimerPeriod();
            _stream.Dispose();
            DisposeSound();
            _running.Dispose();
        }

        private static async Task<bool> JoinAsync(Thread? thread, TimeSpan timeout) =>
            thread is null || await Task.Run(() => thread.Join(timeout)).ConfigureAwait(false);

        private void EndTimerPeriod()
        {
            if (_timerPeriod)
            {
                Winmm.TimeEndPeriod(1);
                _timerPeriod = false;
            }
        }

        private void DisposeSound()
        {
            DisposeAll(_sources);
            _pipe?.Dispose();
        }

        private void WriteFrames()
        {
            var frame = new byte[_stream.Width * _stream.Height * 4];
            var lastSequence = -1L;
            try
            {
                while (true)
                {
                    if (Paused && !_stopping)
                    {
                        _running.Wait(100);
                        continue;
                    }

                    TimeSpan? wait;
                    lock (_pacerGate)
                    {
                        if (_stopping)
                        {
                            break;
                        }

                        wait = _pacer.Next(Clock.Elapsed);
                    }

                    if (wait is { } w)
                    {
                        Thread.Sleep(w < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : w);
                        continue;
                    }

                    WriteFrame(frame, ref lastSequence);
                }

                // With sound, every frame due by the stop is written, so the video is exactly as long as the sound.
                while (!_abort.IsCancellationRequested && _pacer.Written < Interlocked.Read(ref _finalFrames))
                {
                    WriteFrame(frame, ref lastSequence);
                }
            }
            catch (Exception ex) when (ex is IOException or CaptureException or ObjectDisposedException)
            {
                Fail(ex);
            }
        }

        private void WriteFrame(byte[] frame, ref long lastSequence)
        {
            var sequence = _stream.CopyLatest(frame);
            if (sequence == lastSequence)
            {
                _repeated++;
            }

            lastSequence = sequence;
            if (_options.ShowClicks)
            {
                var m = _mouse.Read();
                var now = _wallClock.Elapsed;
                _clicks.Update(now, m.Cursor, m.LeftDown, m.LeftClicked, m.RightDown, m.RightClicked);
                _clicks.Draw(frame, _stream.Width, _stream.Height, new PixelPoint(_options.Area.X, _options.Area.Y), now, _options.Monitor.Scale.ScaleX);
            }

            _process.Write(frame);
            _pacer.FrameWritten();
        }

        /// <summary>A packet from a sound source (on its capture thread): onto the track where the clock says it belongs.</summary>
        private void OnSound(AudioTrack track, ReadOnlySpan<float> samples, long start, bool discontinuity)
        {
            Span<SoundRun> runs = stackalloc SoundRun[4];
            var count = Clock.Map(start, samples.Length / AudioMixer.Channels, AudioMixer.SampleRate, runs);
            for (var i = 0; i < count; i++)
            {
                var run = runs[i];
                _mixer!.Place(
                    track,
                    samples.Slice(run.SourceOffset * AudioMixer.Channels, run.Count * AudioMixer.Channels),
                    run.Position,
                    discontinuity && run.SourceOffset == 0);
            }
        }

        private void OnSoundFailed(IAudioSource source, Exception ex)
        {
            var at = Clock.Elapsed;
            _soundNote = $"Sound from {source.Name} stopped at {(int)at.TotalMinutes}:{at.Seconds:00} ({ex.Message.Trim()}).";
        }

        private void WriteSound()
        {
            var mixer = _mixer!;
            var pipe = _pipe!;
            try
            {
                pipe.WaitForConnection(_abort.Token);
                LogSoundConnected(Clock.Elapsed);
                _soundConnected!.TrySetResult();
                var buffer = new float[MixChunkFrames * AudioMixer.Channels];
                var heartbeat = Stopwatch.StartNew();
                while (true)
                {
                    if (heartbeat.Elapsed >= TimeSpan.FromSeconds(5))
                    {
                        heartbeat.Restart();
                        LogProgress(Clock.Elapsed, _pacer.Written, _pacer.Dropped, mixer.Position, _stillSince != 0);
                    }

                    long target;
                    bool last;
                    lock (_pacerGate)
                    {
                        // Leave out the time of every video frame the pacer dropped.
                        var dropped = _pacer.Dropped;
                        mixer.Skip((dropped - _skippedFrames) * _soundPerFrame);
                        _skippedFrames = dropped;

                        TimeSpan? stillFor = _stillSince == 0 ? null : Stopwatch.GetElapsedTime(_stillSince);
                        last = _finalFrames >= 0 && stillFor >= AudioSchedule.Latency;
                        target = last ? (_finalFrames + dropped) * _soundPerFrame : AudioSchedule.Target(Clock.Elapsed, stillFor);
                    }

                    while (mixer.Position < target && !_abort.IsCancellationRequested)
                    {
                        var frames = (int)Math.Min(MixChunkFrames, target - mixer.Position);
                        var chunk = buffer.AsSpan(0, frames * AudioMixer.Channels);
                        mixer.Mix(chunk);
                        pipe.Write(chunk);
                    }

                    if (last || _abort.IsCancellationRequested)
                    {
                        break;
                    }

                    Thread.Sleep(10);
                }

                if (!_abort.IsCancellationRequested)
                {
                    LogStopStep("sound written, waiting for ffmpeg to read it");
                    pipe.Finish(); // ffmpeg reads the rest, then the sound input ends
                    LogStopStep("sound input closed");
                }
            }
            catch (OperationCanceledException)
            {
                _soundConnected!.TrySetCanceled();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                _soundConnected!.TrySetException(ex);
                Fail(ex);
            }
        }

        private void Fail(Exception ex)
        {
            if (_abort.IsCancellationRequested)
            {
                return; // the start failed and is being undone; the caller reports why
            }

            Interlocked.CompareExchange(ref _failure, ex, null);
            LogWriterFailed(ex);
            Failed?.Invoke(this, EventArgs.Empty);
        }

        private void TryDelete(FileInfo file)
        {
            try
            {
                file.Refresh();
                if (file.Exists)
                {
                    file.Delete();
                }
            }
            catch (IOException ex)
            {
                LogDeleteFailed(ex, file.FullName);
            }
        }

        [LoggerMessage(Level = LogLevel.Error, Message = "Recording stopped writing")]
        private partial void LogWriterFailed(Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "ffmpeg opened the sound input at {Elapsed}")]
        private partial void LogSoundConnected(TimeSpan elapsed);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Recording at {Elapsed}: {Written} frames written, {Dropped} dropped, sound mixed to frame {Mixed}, paused/stopping {Still}")]
        private partial void LogProgress(TimeSpan elapsed, long written, long dropped, long mixed, bool still);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Stop: {Step}")]
        private partial void LogStopStep(string step);

        [LoggerMessage(Level = LogLevel.Information, Message = "Recording sound: {Duration}, {Frames} frames, {Skipped} left out for dropped video frames, {Late} arrived late, {Overflow} overflowed")]
        private partial void LogSoundFinished(TimeSpan duration, long frames, long skipped, long late, long overflow);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete the unfinished recording {Path}")]
        private partial void LogDeleteFailed(Exception ex, string path);
    }
}

public static class RecordingServiceCollectionExtensions
{
    public static IServiceCollection AddScreenRecording(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IFfmpeg, FfmpegService>();
        services.AddSingleton<IMouseState, Win32MouseState>();
        services.AddSingleton<IAudioSourceFactory, WasapiAudioSourceFactory>();
        services.AddSingleton<IAudioDevices, WasapiAudioDevices>();
        services.AddSingleton<IScreenRecorder, ScreenRecorder>();
        return services;
    }
}
