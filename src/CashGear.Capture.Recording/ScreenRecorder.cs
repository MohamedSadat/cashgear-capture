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
/// recording: two frame buffers and one staging texture.
/// </summary>
internal sealed partial class ScreenRecorder(
    IFfmpeg ffmpeg,
    WgcInterop interop,
    IMouseState mouse,
    ILoggerFactory loggers) : IScreenRecorder
{
    private static readonly TimeSpan FinishTimeout = TimeSpan.FromSeconds(60);

    private readonly ILogger _logger = loggers.CreateLogger<ScreenRecorder>();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Session? _session;

    public bool IsRecording => _session is not null;

    public bool IsPaused => _session?.Paused ?? false;

    public TimeSpan Elapsed => _session?.Clock.Elapsed ?? TimeSpan.Zero;

    public event EventHandler? StateChanged;

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

            var stream = await WgcAreaStream.StartAsync(interop, options.Monitor, area, options.IncludeCursor, loggers, ct).ConfigureAwait(false);
            FfmpegProcess process;
            try
            {
                process = FfmpegProcess.Start(
                    path, FfmpegArguments.Record(width, height, options.Fps, options.Quality, encoder, options.OutputPath), _logger);
            }
            catch
            {
                stream.Dispose();
                throw;
            }

            var session = new Session(options with { Area = area }, encoder, stream, process, mouse, _logger);
            session.Failed += (_, _) => OnStateChanged();
            try
            {
                session.Start();
            }
            catch
            {
                stream.Dispose();
                process.Dispose(); // kills ffmpeg, which is still waiting for frames
                throw;
            }

            _session = session;
            LogStarted(area, options.Monitor.ShortName, options.Fps, FfmpegArguments.Name(encoder), options.Quality, options.OutputPath);
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

    private void OnStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recording {Area} on {Monitor} at {Fps} fps with {Encoder} (quality {Quality}) to {Path}")]
    private partial void LogStarted(PixelRect area, string monitor, int fps, string encoder, int quality, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recording saved: {Path}, {Duration}, {Written} frames, {Dropped} dropped ({DropRate:P1}), {Repeated} unchanged, {Bytes} bytes")]
    private partial void LogFinished(string path, TimeSpan duration, long written, long dropped, double dropRate, long repeated, long bytes);

    /// <summary>One recording: the capture, the encoder and the writer thread between them.</summary>
    private sealed partial class Session(
        RecordingOptions options, VideoEncoder encoder, WgcAreaStream stream, FfmpegProcess process, IMouseState mouse, ILogger logger)
    {
        private readonly FramePacer _pacer = new(options.Fps);
        private readonly ClickHighlighter _clicks = new();
        private readonly ManualResetEventSlim _running = new(initialState: true);
        private readonly Stopwatch _wallClock = new();
        private Thread? _writer;
        private volatile bool _stopping;
        private long _repeated;
        private Exception? _failure;

        /// <summary>Recording time: runs only while not paused.</summary>
        public Stopwatch Clock { get; } = new();

        public bool Paused { get; private set; }

        /// <summary>Raised (on the writer thread) when recording fails, e.g. ffmpeg quits.</summary>
        public event EventHandler? Failed;

        public void Start()
        {
            Winmm.TimeBeginPeriod(1); // 1 ms sleeps: frames on time even at 60 fps
            Clock.Start();
            _wallClock.Start();
            _writer = new Thread(WriteFrames) { IsBackground = true, Name = "Recording writer", Priority = ThreadPriority.AboveNormal };
            _writer.Start();
        }

        public void Pause()
        {
            Paused = true;
            Clock.Stop();
            _running.Reset();
        }

        public void Resume()
        {
            Paused = false;
            Clock.Start();
            _running.Set();
        }

        public async Task<RecordingResult> StopAsync(TimeSpan timeout)
        {
            _stopping = true;
            _running.Set();
            await Task.Run(() => _writer?.Join()).ConfigureAwait(false);
            Clock.Stop();
            Winmm.TimeEndPeriod(1);
            stream.Dispose();

            var exitCode = await process.FinishAsync(timeout).ConfigureAwait(false);
            var messages = process.Messages;
            process.Dispose();
            _running.Dispose();

            var file = new FileInfo(options.OutputPath);
            if (_failure is not null || exitCode != 0 || _pacer.Written == 0 || !file.Exists)
            {
                TryDelete(file);
                var reason = _failure?.Message ?? (exitCode != 0 ? $"ffmpeg exit code {exitCode}" : "no frames were recorded");
                var message = $"The recording could not be saved ({reason}).{(messages.Length > 0 ? Environment.NewLine + messages : string.Empty)}";
                throw _failure is null ? new CaptureException(message) : new CaptureException(message, _failure);
            }

            return new RecordingResult(
                file.FullName,
                TimeSpan.FromSeconds((double)_pacer.Written / options.Fps),
                _pacer.Written,
                _pacer.Dropped,
                _repeated,
                encoder,
                file.Length);
        }

        private void WriteFrames()
        {
            var frame = new byte[stream.Width * stream.Height * 4];
            var lastSequence = -1L;
            var origin = new PixelPoint(options.Area.X, options.Area.Y);
            var scale = options.Monitor.Scale.ScaleX;
            try
            {
                while (!_stopping)
                {
                    if (Paused)
                    {
                        _running.Wait(100);
                        continue;
                    }

                    if (_pacer.Next(Clock.Elapsed) is { } wait)
                    {
                        Thread.Sleep(wait < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : wait);
                        continue;
                    }

                    var sequence = stream.CopyLatest(frame);
                    if (sequence == lastSequence)
                    {
                        _repeated++;
                    }

                    lastSequence = sequence;
                    if (options.ShowClicks)
                    {
                        var m = mouse.Read();
                        var now = _wallClock.Elapsed;
                        _clicks.Update(now, m.Cursor, m.LeftDown, m.LeftClicked, m.RightDown, m.RightClicked);
                        _clicks.Draw(frame, stream.Width, stream.Height, origin, now, scale);
                    }

                    process.Write(frame);
                    _pacer.FrameWritten();
                }
            }
            catch (Exception ex) when (ex is IOException or CaptureException or ObjectDisposedException)
            {
                _failure = ex;
                LogWriterFailed(ex);
                Failed?.Invoke(this, EventArgs.Empty);
            }
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

        [LoggerMessage(Level = LogLevel.Error, Message = "Recording stopped writing frames")]
        private partial void LogWriterFailed(Exception ex);

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
        services.AddSingleton<IScreenRecorder, ScreenRecorder>();
        return services;
    }
}
