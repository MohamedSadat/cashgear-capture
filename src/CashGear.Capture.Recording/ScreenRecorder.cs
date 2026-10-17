using System.Diagnostics;
using CashGear.Capture.Audio;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Engine.Capture.Wgc;
using CashGear.Capture.Interop.Native;
using CashGear.Capture.Media;
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
/// Records an area of one monitor to MP4, encoding inside the app (FFmpeg). Pictures from a
/// <see cref="WgcAreaStream"/> and sound from WASAPI are stamped with the same clock (<see cref="RecordingClock"/>),
/// so every frame and every sound packet goes into the file at the time it was captured. The recording is written
/// as it happens to a crash-safe temporary file (<see cref="MediaWriter"/>), and saved as an MP4 when it stops.
/// See <see cref="Session"/>.
/// </summary>
internal sealed partial class ScreenRecorder(
    IRecordingEncoders encoders,
    RecordingStore store,
    WgcInterop interop,
    IMouseState mouse,
    IAudioSourceFactory audio,
    ILoggerFactory loggers,
    MicrophoneProcessors? processors = null) : IScreenRecorder
{
    private readonly MicrophoneProcessors _processors = processors ?? new MicrophoneProcessors();

    private static readonly TimeSpan FinishTimeout = TimeSpan.FromSeconds(60);

    private readonly ILogger _logger = loggers.CreateLogger<ScreenRecorder>();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Session? _session;
    private Session? _saving;

    /// <summary>Called on the video thread before each frame is encoded (tests: make an encoder fail or stall).</summary>
    internal Action<EncoderCandidate, long>? BeforeEncode { get; set; }

    public bool IsRecording => _session is not null;

    public bool IsPaused => _session?.Paused ?? false;

    public TimeSpan Elapsed => _session?.Clock.Elapsed ?? TimeSpan.Zero;

    public RecordingSound Sound => _session?.Sound ?? RecordingSound.None;

    public string? SoundNote => _session?.SoundNote;

    public bool MicrophoneMuted
    {
        get => _session?.Track(RecordingSound.Microphone)?.Muted ?? false;
        set => _session?.Track(RecordingSound.Microphone)?.Muted = value;
    }

    public bool SystemAudioMuted
    {
        get => _session?.Track(RecordingSound.System)?.Muted ?? false;
        set => _session?.Track(RecordingSound.System)?.Muted = value;
    }

    public float MicrophoneGain
    {
        get => _session?.Track(RecordingSound.Microphone)?.Gain ?? 1;
        set => _session?.Track(RecordingSound.Microphone)?.Gain = value;
    }

    public float SystemAudioGain
    {
        get => _session?.Track(RecordingSound.System)?.Gain ?? 1;
        set => _session?.Track(RecordingSound.System)?.Gain = value;
    }

    public MicrophoneProcessing? MicrophoneProcessing
    {
        get => _session?.MicrophoneChain?.Settings;
        set
        {
            if (value is not null && _session?.MicrophoneChain is { } chain)
            {
                chain.Settings = value;
            }
        }
    }

    public bool IsSaving => _saving is not null;

    public double SaveProgress => _saving?.SaveProgress ?? 0;

    public event EventHandler? StateChanged;

    public event EventHandler<string>? Warning;

    public event EventHandler<string>? Notice;

    public event EventHandler<string>? Failed;

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

            if (encoders.Problem is { } problem)
            {
                throw new CaptureException("Screen recording isn't available. " + problem);
            }

            var detected = await encoders.DetectAsync(ct: ct).ConfigureAwait(false);
            var usable = EncoderService.Usable(options.Encoder, detected);
            if (usable.Count == 0)
            {
                throw new CaptureException("No video encoder works on this PC: " +
                    string.Join(" ", detected.Where(p => !p.Works).Select(p => $"{p.Id}: {p.Detail}")));
            }

            var (width, height) = EncoderPresets.EvenSize(options.Area.Width, options.Area.Height);
            var area = options.Area with { Width = width, Height = height };
            var format = new VideoFormat(width, height, options.Fps);
            var output = Path.GetFullPath(options.OutputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);

            var fallbacks = new Queue<(EncoderCandidate, EncoderProbe)>(usable);
            var video = await Task.Run(() => OpenFirst(fallbacks, format, options.Quality, _logger), ct).ConfigureAwait(false);
            var parts = new Parts();
            parts.Add(video);
            try
            {
                // Sound devices are opened off the caller's (possibly UI) thread.
                var (sources, soundNote) = await Task.Run(() => OpenSound(options), ct).ConfigureAwait(false);
                parts.Sources.AddRange(sources);
                var tracks = SoundTracks.For(options.Tracks, sources.Select(s => SourceTitle(s, options)).ToList());
                foreach (var _ in tracks)
                {
                    parts.Audio.Add(AudioEncoder.Open(parts.Audio.Count + 1));
                }

                var manifest = new RecordingManifest(
                    Guid.NewGuid().ToString("N")[..8], DateTimeOffset.Now, output, width, height, options.Fps, [], [.. tracks.Select(t => t.Title)]);
                parts.Folder = store.Create(manifest);
                parts.Writer = new MediaWriter(
                    parts.Folder.SegmentPath,
                    [.. parts.Audio.Select((a, i) => StreamSetup.From(a, tracks[i].Title, i == 0))],
                    loggers.CreateLogger<MediaWriter>());
                var clock = new RecordingClock(TimeProvider.System);
                var grid = new FrameGrid(options.Fps);
                parts.Stream = await WgcAreaStream.StartAsync(
                    interop, options.Monitor, area, options.IncludeCursor, loggers, ct, timestamp => grid.SlotOf(clock.MapInstant(timestamp))).ConfigureAwait(false);

                var chain = options.MicrophoneProcessing is { } processing && sources.Any(s => s.Kind == RecordingSound.Microphone)
                    ? _processors.Create(processing)
                    : null;
                var session = new Session(
                    this, options with { Area = area, OutputPath = output }, format, clock, grid, video, fallbacks, parts, manifest, tracks, soundNote, chain, mouse, _logger);
                session.Failed += (_, e) => OnFailed(e);
                session.Start();
                _session = session;
                LogStarted(area, options.Monitor.ShortName, options.Fps, video.Candidate.DisplayName, video.Detail ?? "", options.Quality, output, parts.Folder.Directory);
                LogSound(sources.Count == 0 ? "none" : string.Join(", ", sources.Select(s => s.Name)), string.Join(", ", tracks.Select(t => t.Title)), soundNote ?? string.Empty);
            }
            catch
            {
                parts.Dispose(deleteFolder: true);
                throw;
            }
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
            _saving = session;
        }
        finally
        {
            _gate.Release();
        }

        OnStateChanged();
        try
        {
            var result = await session.StopAsync(FinishTimeout).ConfigureAwait(false);
            LogFinished(string.Join(", ", result.AllParts), result.Duration, result.FramesWritten, result.FramesDropped, result.DropRate, result.FramesRepeated, result.FileBytes);
            return result;
        }
        finally
        {
            _saving = null;
            OnStateChanged();
        }
    }

    /// <summary>The first encoder that opens at the recording's real size (a 256×256 test passing doesn't promise that).</summary>
    private static VideoEncoder OpenFirst(Queue<(EncoderCandidate Candidate, EncoderProbe Probe)> candidates, VideoFormat format, QualityPreset quality, ILogger logger)
    {
        var problems = new List<string>();
        while (candidates.TryDequeue(out var next))
        {
            try
            {
                return Open(next.Candidate, next.Probe, format, quality);
            }
            catch (FfmpegException e)
            {
                LogOpenFailed(logger, next.Candidate.Id, e.Message);
                problems.Add($"{next.Candidate.DisplayName}: {e.Message}");
            }
        }

        throw new CaptureException($"No video encoder could record {format.Width}x{format.Height}. {string.Join(" ", problems)}");
    }

    private static VideoEncoder Open(EncoderCandidate candidate, EncoderProbe probe, VideoFormat format, QualityPreset quality) =>
        VideoEncoder.Open(candidate, format, EncoderPresets.Options(candidate, quality, format.Width, format.Height, format.Fps, probe.QualityMode));

    /// <summary>A track's title: "Microphone", "System audio", or the app's name when only its sound is recorded.</summary>
    private static string SourceTitle(IAudioSource source, RecordingOptions options) =>
        source.Kind == RecordingSound.Microphone ? "Microphone"
        : options.SystemAudioScope == SystemAudioScope.OnlyApp && source is AppAudioSource ? options.SystemAudioAppName ?? "System audio"
        : "System audio";

    /// <summary>The sound devices the options ask for; any that can't be used are explained in the note instead.</summary>
    private (List<IAudioSource> Sources, string? Note) OpenSound(RecordingOptions options)
    {
        var sources = new List<IAudioSource>();
        var problems = new List<string>();
        if (options.SystemAudio)
        {
            if (options.SystemAudioScope != SystemAudioScope.All && options.SystemAudioApp is { } app)
            {
                var exclude = options.SystemAudioScope == SystemAudioScope.AllExceptApp;
                var opened = audio.OpenAppAudio(app, options.SystemAudioAppName ?? app, exclude);
                if (opened.Source is null && exclude)
                {
                    // Leaving one app out isn't possible here: recording everything is closer than nothing.
                    problems.Add($"{opened.Note} All system audio is recorded instead.");
                    Add(audio.OpenSystemAudio(options.SystemAudioDevice));
                }
                else
                {
                    Add(opened);
                }
            }
            else
            {
                Add(audio.OpenSystemAudio(options.SystemAudioDevice));
            }
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

        void Add(AudioOpenResult opened)
        {
            if (opened.Source is { } source)
            {
                sources.Add(source);
            }

            if (opened.Note is { } note)
            {
                problems.Add(note);
            }
        }
    }

    private void OnStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    private void OnWarning(string message)
    {
        LogWarning(message);
        Warning?.Invoke(this, message);
    }

    private void OnNotice(string message) => Notice?.Invoke(this, message);

    private void OnFailed(Exception e)
    {
        OnStateChanged();
        Failed?.Invoke(this, e.Message);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Recording {Area} on {Monitor} at {Fps} fps with {Encoder} {Detail} ({Quality}) to {Path}, temporary files in {Folder}")]
    private partial void LogStarted(PixelRect area, string monitor, int fps, string encoder, string detail, QualityPreset quality, string path, string folder);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recording sound: {Sources}; tracks {Tracks} {Note}")]
    private partial void LogSound(string sources, string tracks, string note);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recording saved: {Paths}, {Duration}, {Written} frames, {Dropped} dropped ({DropRate:P1}), {Repeated} unchanged, {Bytes} bytes")]
    private partial void LogFinished(string paths, TimeSpan duration, long written, long dropped, double dropRate, long repeated, long bytes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Encoder {Id} could not open: {Reason}")]
    private static partial void LogOpenFailed(ILogger logger, string id, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recording: {Message}")]
    private partial void LogWarning(string message);

    /// <summary>What a recording is made of, so a start that fails half way can undo exactly what it did.</summary>
    private sealed class Parts
    {
        public List<VideoEncoder> Video { get; } = [];

        public List<AudioEncoder> Audio { get; } = [];

        public List<IAudioSource> Sources { get; } = [];

        public RecordingFolder? Folder { get; set; }

        public MediaWriter? Writer { get; set; }

        public WgcAreaStream? Stream { get; set; }

        public void Add(VideoEncoder video) => Video.Add(video);

        public void Dispose(bool deleteFolder)
        {
            Stream?.Dispose();
            Sources.ForEach(s => s.Dispose());
            Writer?.Complete(FinishTimeout);
            Writer?.Dispose();
            Audio.ForEach(a => a.Dispose());
            Video.ForEach(v => v.Dispose());
            Folder?.Dispose();
            if (deleteFolder && Folder is not null)
            {
                RecordingStore.Delete(Folder.Directory);
            }
        }
    }

    /// <summary>
    /// One recording: the capture, the encoders, the temporary file and the threads between them.
    /// <para>
    /// Video ("Recording video" thread): a <see cref="FrameGrid"/> decides which frame is due; it shows the newest
    /// picture captured by then (by its capture time on the recording clock), which is read back from the GPU,
    /// gets its click highlights, is converted to the encoder's format and encoded with the frame's timestamp. If
    /// encoding falls far behind, frames are skipped and their timestamps simply left out. If the encoder fails,
    /// the next one that works takes over in a new segment (saved as a second file).
    /// </para>
    /// <para>
    /// Sound ("Recording sound" thread): each source's packets are placed on the clock by their capture time (sound
    /// from a pause is cut out); the mix and, as the track layout asks, each source by itself are encoded to AAC up
    /// to <see cref="AudioSchedule.Latency"/> behind the clock. Silence is filled by the clock, so a source that
    /// stops (an unplugged microphone) leaves silence, never a gap. Sound is never dropped.
    /// </para>
    /// <para>
    /// At Stop the video gets every frame up to the stop and the sound exactly the same length; the temporary file
    /// is finished and saved as MP4, and the temporary folder removed. If saving fails it is kept for recovery.
    /// </para>
    /// </summary>
    private sealed partial class Session
    {
        private const int AudioFrameSize = 1024;
        private static readonly TimeSpan HungEncoder = TimeSpan.FromSeconds(2);

        private readonly ScreenRecorder _owner;
        private readonly RecordingOptions _options;
        private readonly VideoFormat _format;
        private readonly Queue<(EncoderCandidate Candidate, EncoderProbe Probe)> _fallbacks;
        private readonly Parts _parts;
        private readonly WgcAreaStream _stream;
        private readonly MediaWriter _writer;
        private readonly RecordingFolder _folder;
        private readonly IReadOnlyList<SoundTrack> _tracks;
        private readonly IMouseState _mouse;
        private readonly ILogger _logger;
        private readonly FrameGrid _grid;
        private readonly object _gridGate = new();
        private readonly ClickHighlighter _clicks = new();
        private readonly ManualResetEventSlim _running = new(initialState: true);
        private readonly Stopwatch _wallClock = new();
        private readonly AudioMixer? _mixer;
        private readonly Dictionary<RecordingSound, AudioTrack> _sourceTracks = [];
        private readonly List<string> _encoderNames = [];
        private readonly CancellationTokenSource _abort = new();
        private RecordingManifest _manifest;
        private VideoEncoder _video;
        private FrameConverter _converter;
        private Timer? _watchdog;
        private Thread? _videoThread;
        private Thread? _soundThread;
        private volatile bool _stopping;
        private volatile string? _soundNote;
        private volatile string? _note;
        private bool _timerPeriod;
        private bool _segmentBegun;
        private int _segments;
        private long _stillSince;
        private long _finalSamples = -1;
        private long _encodingSince;
        private double _saveProgress;
        private Exception? _failure;

        public Session(
            ScreenRecorder owner,
            RecordingOptions options,
            VideoFormat format,
            RecordingClock clock,
            FrameGrid grid,
            VideoEncoder video,
            Queue<(EncoderCandidate, EncoderProbe)> fallbacks,
            Parts parts,
            RecordingManifest manifest,
            IReadOnlyList<SoundTrack> tracks,
            string? soundNote,
            MicrophoneChain? microphoneChain,
            IMouseState mouse,
            ILogger logger)
        {
            MicrophoneChain = microphoneChain;
            _owner = owner;
            _options = options;
            _format = format;
            _video = video;
            _fallbacks = fallbacks;
            _parts = parts;
            _stream = parts.Stream!;
            _writer = parts.Writer!;
            _folder = parts.Folder!;
            _manifest = manifest;
            _tracks = tracks;
            _soundNote = soundNote;
            _mouse = mouse;
            _logger = logger;
            Clock = clock;
            _grid = grid;
            _converter = new FrameConverter(format.Width, format.Height, video.PixelFormat);
            _encoderNames.Add(video.Candidate.DisplayName);
            _writer.Failed += (_, e) => Fail(e);
            if (parts.Sources.Count > 0)
            {
                _mixer = new AudioMixer();
                foreach (var source in parts.Sources)
                {
                    var track = _mixer.AddTrack(source.Name);
                    track.Gain = source.Kind == RecordingSound.Microphone ? options.MicrophoneGain : options.SystemAudioGain;
                    _sourceTracks[source.Kind] = track;
                    Sound |= source.Kind;
                }
            }
        }

        /// <summary>Recording time: runs only while not paused.</summary>
        public RecordingClock Clock { get; }

        public bool Paused { get; private set; }

        public RecordingSound Sound { get; }

        public string? SoundNote => _soundNote;

        public double SaveProgress => Volatile.Read(ref _saveProgress);

        /// <summary>The microphone's processing, when it has any.</summary>
        public MicrophoneChain? MicrophoneChain { get; }

        /// <summary>Raised (on a worker thread) when recording can't go on, e.g. the disk is full.</summary>
        public event EventHandler<Exception>? Failed;

        public AudioTrack? Track(RecordingSound kind) => _sourceTracks.GetValueOrDefault(kind);

        public float TakeSoundLevel() => _mixer?.TakePeak() ?? 0;

        public void Start()
        {
            Winmm.TimeBeginPeriod(1); // 1 ms sleeps: frames on time even at 60 fps
            _timerPeriod = true;
            Clock.Start();
            _wallClock.Start();
            _videoThread = new Thread(WriteVideo) { IsBackground = true, Name = "Recording video", Priority = ThreadPriority.AboveNormal };
            _videoThread.Start();
            _watchdog = new Timer(_ => Watch(), null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
            if (_mixer is null)
            {
                return;
            }

            _soundThread = new Thread(WriteSound) { IsBackground = true, Name = "Recording sound", Priority = ThreadPriority.AboveNormal };
            _soundThread.Start();
            foreach (var source in _parts.Sources)
            {
                var track = _sourceTracks[source.Kind];
                AudioDataHandler place = (samples, start, discontinuity) => OnSound(track, samples, start, discontinuity);
                if (source.Kind == RecordingSound.Microphone && MicrophoneChain is { } chain)
                {
                    // Processed on the capture thread; each block keeps the time its sound was captured.
                    source.Start((samples, start, discontinuity) => chain.Process(samples, start, discontinuity, place), (status, message) => OnSoundStatus(status, message));
                }
                else
                {
                    source.Start(place, (status, message) => OnSoundStatus(status, message));
                }
            }
        }

        public void Pause()
        {
            lock (_gridGate)
            {
                Paused = true;
                Clock.Stop();
                _stillSince = Stopwatch.GetTimestamp();
            }

            _running.Reset();
        }

        public void Resume()
        {
            lock (_gridGate)
            {
                Paused = false;
                Clock.Start();
                _stillSince = 0;
            }

            _running.Set();
        }

        public async Task<RecordingResult> StopAsync(TimeSpan timeout)
        {
            lock (_gridGate)
            {
                _stopping = true;
                Clock.Stop();
                if (_stillSince == 0)
                {
                    _stillSince = Stopwatch.GetTimestamp();
                }

                // The video ends with the frame due at the stop, and the sound at the same moment.
                _grid.Finish(Clock.Elapsed);
                _finalSamples = _grid.End!.Value * AudioSchedule.FramesPerVideoFrame(_format.Fps);
            }

            _running.Set();
            LogStopStep($"stopping at {Clock.Elapsed}, {_grid.End} frames");
            var videoDone = await JoinAsync(_videoThread, _failure is null ? timeout : HungEncoder).ConfigureAwait(false);
            if (!videoDone)
            {
                Fail(new CaptureException("The video encoder stopped responding."));
            }

            LogStopStep($"video done ({videoDone}), {_grid.Written} frames");
            _watchdog?.Dispose();
            EndTimerPeriod();
            _stream.Dispose();
            if (!await JoinAsync(_soundThread, timeout).ConfigureAwait(false))
            {
                _abort.Cancel();
                await JoinAsync(_soundThread, timeout).ConfigureAwait(false);
            }

            LogStopStep("sound done");
            _parts.Sources.ForEach(s => s.Dispose());
            MicrophoneChain?.Dispose();
            await Task.Run(() => _writer.Complete(timeout)).ConfigureAwait(false);
            _writer.Dispose();
            LogStopStep($"temporary file finished, {_writer.Segments.Count} segment(s)");
            _parts.Audio.ForEach(a => a.Dispose());
            if (videoDone)
            {
                _video.Dispose();
                _converter.Dispose();
            }

            _running.Dispose();
            LogSoundFinished();

            var segments = _writer.Segments;
            _manifest = _manifest with { Segments = [.. segments.Select(s => Path.GetFileName(s.Path))] };
            TrySaveManifest();
            if (segments.Count == 0 || _grid.Written == 0)
            {
                _folder.Dispose();
                RecordingStore.Delete(_folder.Directory);
                var reason = _failure?.Message ?? "no frames were recorded";
                throw new CaptureException($"The recording could not be saved ({reason}).", _failure ?? new InvalidOperationException(reason));
            }

            RecoveredRecording saved;
            try
            {
                var progress = new Progress<double>(p => Volatile.Write(ref _saveProgress, p));
                saved = await Task.Run(() => RecordingRecovery.Save(_folder.Directory, _manifest, progress, CancellationToken.None)).ConfigureAwait(false);
            }
            catch (Exception e) when (e is FfmpegException or IOException or UnauthorizedAccessException)
            {
                _folder.Dispose();
                throw new CaptureException($"The recording could not be saved ({e.Message}). It was kept and can be recovered from the tray menu.", e);
            }

            _folder.Dispose();
            RecordingStore.Delete(_folder.Directory);
            if (saved.Files.Count == 0)
            {
                throw new CaptureException("The recording could not be saved: nothing in it could be read.");
            }

            var note = _failure is { } failure ? $"Recording stopped early: {failure.Message}" : _note;
            return new RecordingResult(
                saved.Files[0],
                TimeSpan.FromSeconds((double)(_grid.End ?? _grid.Next) / _format.Fps),
                _grid.Written,
                _grid.Dropped,
                _grid.Repeated,
                string.Join(", then ", _encoderNames),
                saved.Files.Sum(f => new FileInfo(f).Length),
                Sound,
                _soundNote,
                saved.Files,
                note);
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

        private void WriteVideo()
        {
            // Like a video player: while recording, Windows must not turn the display off or put the PC to sleep for
            // lack of input. A display that goes off stops the screen changing, and the video would freeze there.
            // Held by this thread, so it ends with the recording however the thread ends.
            Kernel32.SetThreadExecutionState(Kernel32.EsContinuous | Kernel32.EsSystemRequired | Kernel32.EsDisplayRequired);
            var packets = new List<EncodedPacket>();
            var picture = new byte[_format.Width * _format.Height * 4];
            var marked = _options.ShowClicks ? new byte[picture.Length] : null;
            Picture? current = null;
            try
            {
                while (_failure is null)
                {
                    GridStep step;
                    lock (_gridGate)
                    {
                        step = _grid.Step(Clock.Elapsed);
                        if (!step.Produce && _grid.End is not null)
                        {
                            break;
                        }
                    }

                    if (!step.Produce)
                    {
                        if (Paused && !_stopping)
                        {
                            _running.Wait(100);
                        }
                        else
                        {
                            Thread.Sleep(TimeSpan.FromMilliseconds(Math.Clamp(step.Wait.TotalMilliseconds, 1, 20)));
                        }

                        continue;
                    }

                    var changed = TakePictures(step.Slot, ref current);
                    if (changed)
                    {
                        _stream.Read(current!, picture);
                    }

                    var frame = picture;
                    if (marked is not null)
                    {
                        var m = _mouse.Read();
                        var now = _wallClock.Elapsed;
                        _clicks.Update(now, m.Cursor, m.LeftDown, m.LeftClicked, m.RightDown, m.RightClicked);
                        if (_clicks.IsActive(now))
                        {
                            picture.CopyTo(marked, 0);
                            _clicks.Draw(marked, _format.Width, _format.Height, new PixelPoint(_options.Area.X, _options.Area.Y), now, _options.Monitor.Scale.ScaleX);
                            frame = marked;
                        }
                    }

                    Encode(step.Slot, frame, packets);
                    lock (_gridGate)
                    {
                        _grid.Produced(repeated: !changed);
                    }
                }

                if (_failure is null)
                {
                    _video.Flush(packets);
                    Post(packets);
                }
            }
            catch (Exception ex)
            {
                Fail(ex); // never let a recording thread take the app down
            }
            finally
            {
                packets.ForEach(p => p.Dispose());
                if (current is not null)
                {
                    _stream.Release(current);
                }

                Kernel32.SetThreadExecutionState(Kernel32.EsContinuous);
            }
        }

        /// <summary>Takes the pictures due by frame <paramref name="slot"/>, keeping the newest; true if it is a new one.</summary>
        private bool TakePictures(long slot, ref Picture? current)
        {
            var changed = false;
            while (_stream.TryPeek(out var next) && (current is null || next.Slot <= slot))
            {
                _stream.TryTake(out _);
                if (current is not null)
                {
                    _stream.Release(current);
                }

                LogPicture(slot, next.Slot);
                current = next;
                changed = true;
            }

            return changed;
        }

        private unsafe void Encode(long slot, byte[] bgra, List<EncodedPacket> packets)
        {
            while (true)
            {
                try
                {
                    Volatile.Write(ref _encodingSince, Stopwatch.GetTimestamp());
                    _owner.BeforeEncode?.Invoke(_video.Candidate, slot);
                    _converter.Convert(bgra, _format.Width * 4, _video.NextFrame());
                    _video.Encode(slot, packets);
                    Post(packets);
                    return;
                }
                catch (FfmpegException ex)
                {
                    packets.ForEach(p => p.Dispose());
                    packets.Clear();
                    SwitchEncoder(ex);
                }
                finally
                {
                    Volatile.Write(ref _encodingSince, 0);
                }
            }
        }

        /// <summary>The encoder failed: finish its segment and carry on with the next one that opens, in a new segment.</summary>
        private void SwitchEncoder(FfmpegException ex)
        {
            var failed = _video;
            var failedFormat = failed.PixelFormat;
            LogEncoderFailed(ex, failed.Candidate.Id);
            var flushed = new List<EncodedPacket>();
            try
            {
                failed.Flush(flushed);
                Post(flushed);
            }
            catch (FfmpegException)
            {
                flushed.ForEach(p => p.Dispose());
            }

            failed.Dispose();
            while (_fallbacks.TryDequeue(out var next))
            {
                if (next.Candidate.Id == failed.Candidate.Id)
                {
                    continue;
                }

                try
                {
                    _video = Open(next.Candidate, next.Probe, _format, _options.Quality);
                }
                catch (FfmpegException e)
                {
                    LogOpenFailed(_logger, next.Candidate.Id, e.Message);
                    continue;
                }

                if (_video.PixelFormat != failedFormat)
                {
                    _converter.Dispose();
                    _converter = new FrameConverter(_format.Width, _format.Height, _video.PixelFormat);
                }

                _segmentBegun = false;
                _encoderNames.Add(next.Candidate.DisplayName);
                var at = Clock.Elapsed;
                _note = $"{failed.Candidate.DisplayName} stopped working at {(int)at.TotalMinutes}:{at.Seconds:00}; {next.Candidate.DisplayName} recorded the rest, saved as a second file.";
                _owner.OnWarning($"{failed.Candidate.DisplayName} stopped working ({ex.Message}). Recording continues with {next.Candidate.DisplayName}; it will be saved as a second file.");
                return;
            }

            throw new CaptureException($"The video encoder stopped working and no other could take over ({ex.Message}).", ex);
        }

        /// <summary>Hands packets to the writer, starting a segment for the current encoder with its first packet.</summary>
        private void Post(List<EncodedPacket> packets)
        {
            if (packets.Count == 0)
            {
                return;
            }

            if (!_segmentBegun)
            {
                _writer.BeginSegment(StreamSetup.From(_video), packets[0].Seconds);
                _segmentBegun = true;
                _segments++;
                _manifest = _manifest with { Segments = [.. Enumerable.Range(1, _segments).Select(n => Path.GetFileName(_folder.SegmentPath(n)))] };
                TrySaveManifest();
            }

            packets.ForEach(_writer.Write);
            packets.Clear();
        }

        private void TrySaveManifest()
        {
            try
            {
                _folder.Save(_manifest);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                LogManifestFailed(e);
            }
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

        /// <summary>A source's device stopped, came back or was replaced: tell the user now, and note it (with the time) for the saved notification.</summary>
        private void OnSoundStatus(AudioSourceStatus status, string message)
        {
            var at = Clock.Elapsed;
            var note = $"At {(int)at.TotalMinutes}:{at.Seconds:00}: {message}";
            _soundNote = _soundNote is null ? note : _soundNote + " " + note;
            if (status == AudioSourceStatus.Lost)
            {
                _owner.OnWarning(message);
            }
            else
            {
                _owner.OnNotice(message);
            }
        }

        private void WriteSound()
        {
            var mixer = _mixer!;
            var mixed = new float[AudioFrameSize * AudioMixer.Channels];
            var alone = _tracks.Any(t => t.Source is not null)
                ? _parts.Sources.Select(_ => new float[AudioFrameSize * AudioMixer.Channels]).ToArray()
                : [];
            var packets = new List<EncodedPacket>();
            var heartbeat = Stopwatch.StartNew();
            try
            {
                while (!_abort.IsCancellationRequested)
                {
                    if (heartbeat.Elapsed >= TimeSpan.FromSeconds(5))
                    {
                        heartbeat.Restart();
                        LogProgress(Clock.Elapsed, _grid.Written, _grid.Dropped, mixer.Position, _writer.Queued, _stream.Delivered);
                    }

                    long target;
                    bool last;
                    lock (_gridGate)
                    {
                        TimeSpan? stillFor = _stillSince == 0 ? null : Stopwatch.GetElapsedTime(_stillSince);
                        last = _finalSamples >= 0 && stillFor >= AudioSchedule.Latency;
                        target = last ? _finalSamples : AudioSchedule.Target(Clock.Elapsed, stillFor);
                    }

                    while (mixer.Position + AudioFrameSize <= target && !_abort.IsCancellationRequested)
                    {
                        EncodeSound(AudioFrameSize, mixer, mixed, alone, packets);
                    }

                    if (last)
                    {
                        var rest = (int)(target - mixer.Position);
                        if (rest > 0)
                        {
                            EncodeSound(rest, mixer, mixed, alone, packets);
                        }

                        foreach (var encoder in _parts.Audio)
                        {
                            encoder.Flush(packets);
                        }

                        packets.ForEach(_writer.Write);
                        packets.Clear();
                        break;
                    }

                    Thread.Sleep(10);
                }
            }
            catch (Exception ex)
            {
                packets.ForEach(p => p.Dispose());
                Fail(ex); // never let a recording thread take the app down
            }
        }

        private void EncodeSound(int frames, AudioMixer mixer, float[] mixed, float[][] alone, List<EncodedPacket> packets)
        {
            var position = mixer.Position;
            mixer.Mix(frames, mixed, alone);
            for (var i = 0; i < _tracks.Count; i++)
            {
                var samples = _tracks[i].Source is { } source ? alone[source] : mixed;
                _parts.Audio[i].Encode(samples.AsSpan(0, frames * AudioMixer.Channels), position, packets);
            }

            packets.ForEach(_writer.Write);
            packets.Clear();
        }

        /// <summary>Every half second: a video encoder call that hasn't returned for 2 s means the encoder hangs.</summary>
        private void Watch()
        {
            var since = Volatile.Read(ref _encodingSince);
            if (since != 0 && Stopwatch.GetElapsedTime(since) > HungEncoder)
            {
                Fail(new CaptureException($"{_video.Candidate.DisplayName} stopped responding."));
            }
        }

        private void Fail(Exception ex)
        {
            if (Interlocked.CompareExchange(ref _failure, ex, null) is not null || _abort.IsCancellationRequested)
            {
                return;
            }

            LogFailed(ex);
            Failed?.Invoke(this, ex);
        }

        private void LogSoundFinished()
        {
            if (_mixer is null)
            {
                return;
            }

            var tracks = _mixer.Tracks;
            LogSound(TimeSpan.FromSeconds((double)_mixer.Mixed / AudioMixer.SampleRate), _mixer.Mixed, tracks.Sum(t => t.LateFrames), tracks.Sum(t => t.OverflowFrames));
        }

        [LoggerMessage(Level = LogLevel.Error, Message = "Recording can't go on")]
        private partial void LogFailed(Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Encoder {Id} failed during the recording")]
        private partial void LogEncoderFailed(Exception ex, string id);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Recording at {Elapsed}: {Written} frames, {Dropped} dropped, sound mixed to {Mixed}, {Queued} packets waiting, {Pictures} pictures from Windows")]
        private partial void LogProgress(TimeSpan elapsed, long written, long dropped, long mixed, int queued, long pictures);

        [LoggerMessage(Level = LogLevel.Trace, Message = "Frame {Slot} shows the picture captured for frame {PictureSlot}")]
        private partial void LogPicture(long slot, long pictureSlot);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Stop: {Step}")]
        private partial void LogStopStep(string step);

        [LoggerMessage(Level = LogLevel.Information, Message = "Recording sound: {Duration}, {Frames} frames, {Late} arrived late, {Overflow} overflowed")]
        private partial void LogSound(TimeSpan duration, long frames, long late, long overflow);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Could not update the recording's manifest")]
        private partial void LogManifestFailed(Exception e);
    }
}

public static class RecordingServiceCollectionExtensions
{
    public static IServiceCollection AddScreenRecording(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<FfmpegRuntime>();
        services.AddSingleton<IRecordingEncoders, EncoderService>();
        services.AddSingleton<RecordingStore>();
        services.AddSingleton<IRecordingRecovery, RecordingRecovery>();
        services.AddSingleton<IMouseState, Win32MouseState>();
        services.AddAudio();
        services.AddSingleton<IScreenRecorder, ScreenRecorder>();
        return services;
    }
}
