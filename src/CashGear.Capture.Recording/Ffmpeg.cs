using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using CashGear.Capture.Core.Recording;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.Recording;

/// <summary>
/// ffmpeg next to the app (<c>ffmpeg\ffmpeg.exe</c>, as shipped), or on the PATH for development. Encoders are
/// tested with a tiny encode the first time they're needed, because one being listed doesn't mean it works
/// (an NVIDIA encoder needs a recent enough driver, Quick Sync an Intel GPU, and so on).
/// </summary>
public sealed partial class FfmpegService : IFfmpeg
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    private readonly ILogger<FfmpegService> _logger;
    private readonly Dictionary<VideoEncoder, bool> _works = [];
    private readonly SemaphoreSlim _probeGate = new(1, 1);

    public FfmpegService(ILogger<FfmpegService> logger)
        : this(System.IO.Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe"), searchPath: true, logger)
    {
    }

    internal FfmpegService(string expectedPath, bool searchPath, ILogger<FfmpegService> logger)
    {
        _logger = logger;
        ExpectedPath = expectedPath;
        Path = File.Exists(expectedPath) ? expectedPath : searchPath ? FindOnPath() : null;
        if (Path is null)
        {
            LogMissing(expectedPath);
        }
        else
        {
            LogFound(Path);
        }
    }

    public string? Path { get; }

    public string ExpectedPath { get; }

    public async Task<VideoEncoder> ChooseEncoderAsync(RecordingEncoder preference, CancellationToken ct = default)
    {
        var ffmpeg = Path ?? throw new InvalidOperationException($"ffmpeg.exe was not found (expected at {ExpectedPath}).");
        await _probeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var encoder in FfmpegArguments.Preference(preference))
            {
                if (!_works.TryGetValue(encoder, out var works))
                {
                    works = await ProbeAsync(ffmpeg, encoder, ct).ConfigureAwait(false);
                    _works[encoder] = works;
                }

                if (works)
                {
                    return encoder;
                }
            }

            throw new InvalidOperationException("No H.264 encoder works with this ffmpeg (not even libx264).");
        }
        finally
        {
            _probeGate.Release();
        }
    }

    private async Task<bool> ProbeAsync(string ffmpeg, VideoEncoder encoder, CancellationToken ct)
    {
        var start = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var argument in FfmpegArguments.Probe(encoder))
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start ffmpeg.");
        var errors = process.StandardError.ReadToEndAsync(ct);
        _ = process.StandardOutput.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            LogProbe(encoder, false, "timed out");
            return false;
        }

        var works = process.ExitCode == 0;
        var message = (await errors.ConfigureAwait(false)).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        LogProbe(encoder, works, works ? "ok" : message ?? $"exit code {process.ExitCode}");
        return works;
    }

    private static string? FindOnPath() =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => System.IO.Path.Combine(dir.Trim('"'), "ffmpeg.exe"))
            .FirstOrDefault(File.Exists);

    [LoggerMessage(Level = LogLevel.Information, Message = "ffmpeg: {Path}")]
    private partial void LogFound(string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ffmpeg.exe not found (expected at {Path}); screen recording is unavailable")]
    private partial void LogMissing(string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Encoder {Encoder}: {Works} ({Detail})")]
    private partial void LogProbe(VideoEncoder encoder, bool works, string detail);
}

/// <summary>
/// One ffmpeg encode: raw frames go to its standard input as they are written (a write blocks while ffmpeg is
/// busy, which is what tells the recorder it is falling behind). Its messages are kept for error reports.
/// </summary>
internal sealed partial class FfmpegProcess : IDisposable
{
    private const int MessagesKept = 30;

    private readonly Process _process;
    private readonly Stream _input;
    private readonly Queue<string> _messages = new();
    private readonly ILogger _logger;

    private FfmpegProcess(Process process, ILogger logger)
    {
        _process = process;
        _logger = logger;
        _input = process.StandardInput.BaseStream;
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data))
            {
                return;
            }

            lock (_messages)
            {
                _messages.Enqueue(e.Data);
                if (_messages.Count > MessagesKept)
                {
                    _messages.Dequeue();
                }
            }

            LogMessage(e.Data);
        };
        process.BeginErrorReadLine();
        Exited = process.WaitForExitAsync();
    }

    /// <summary>Completes when ffmpeg has quit, for whatever reason.</summary>
    public Task Exited { get; }

    /// <summary>The last things ffmpeg said (for an error message).</summary>
    public string Messages
    {
        get
        {
            lock (_messages)
            {
                return string.Join(Environment.NewLine, _messages);
            }
        }
    }

    public static FfmpegProcess Start(string ffmpeg, IReadOnlyList<string> arguments, ILogger logger)
    {
        var start = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start ffmpeg.");
        return new FfmpegProcess(process, logger);
    }

    /// <summary>Writes one frame; blocks until ffmpeg has taken it. Throws <see cref="IOException"/> if ffmpeg has quit.</summary>
    public void Write(ReadOnlySpan<byte> frame) => _input.Write(frame);

    /// <summary>Ends the input so ffmpeg finishes the file, and waits for it. Returns ffmpeg's exit code.</summary>
    public Task<int> FinishAsync(TimeSpan timeout)
    {
        CloseInput();
        return WaitForExitAsync(timeout);
    }

    /// <summary>Ends the video input: ffmpeg flushes the encoder and, once every input has ended, finishes the file.</summary>
    public void CloseInput()
    {
        try
        {
            _input.Close();
        }
        catch (IOException)
        {
            // ffmpeg already gone; its exit code tells the rest
        }
    }

    /// <summary>Waits for ffmpeg to quit (killing it after <paramref name="timeout"/>) and returns its exit code.</summary>
    public async Task<int> WaitForExitAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync().ConfigureAwait(false);
        }

        _process.WaitForExit(); // flush the message reader
        return _process.ExitCode;
    }

    public void Dispose()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }

        _process.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "ffmpeg: {Message}")]
    private partial void LogMessage(string message);
}

/// <summary>
/// The named pipe the recording's sound goes to ffmpeg on (its second input), as raw 48 kHz stereo float. It is
/// created before ffmpeg starts, so ffmpeg can open it, which it does after reading the first video frame. Only the
/// current user can connect.
/// </summary>
internal sealed class FfmpegAudioPipe : IDisposable
{
    private const int BufferBytes = 256 * 1024;

    private readonly NamedPipeServerStream _pipe;

    public FfmpegAudioPipe()
    {
        var name = "cashgear-rec-" + Guid.NewGuid().ToString("N");
        Path = @"\\.\pipe\" + name;
        _pipe = new NamedPipeServerStream(
            name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 0, BufferBytes);
    }

    /// <summary>The pipe's path for ffmpeg's <c>-i</c>.</summary>
    public string Path { get; }

    /// <summary>Waits for ffmpeg to open the pipe.</summary>
    public void WaitForConnection(CancellationToken ct) => _pipe.WaitForConnectionAsync(ct).GetAwaiter().GetResult();

    /// <summary>Writes sound; blocks while ffmpeg isn't reading. Throws <see cref="IOException"/> if ffmpeg has quit.</summary>
    public void Write(ReadOnlySpan<float> samples) => _pipe.Write(MemoryMarshal.AsBytes(samples));

    /// <summary>Waits until ffmpeg has read everything written, then closes the pipe, which ends the sound input.</summary>
    public void Finish()
    {
        _pipe.WaitForPipeDrain();
        _pipe.Dispose();
    }

    public void Dispose() => _pipe.Dispose();
}
