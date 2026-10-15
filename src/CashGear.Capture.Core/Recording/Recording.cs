using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;

namespace CashGear.Capture.Core.Recording;

/// <summary>Which H.264 encoder ffmpeg uses.</summary>
public enum RecordingEncoder
{
    /// <summary>A hardware encoder (NVIDIA, Intel, AMD) when one works on this PC, otherwise x264.</summary>
    Auto,

    /// <summary>Always the x264 software encoder.</summary>
    Software,
}

/// <summary>An ffmpeg H.264 encoder.</summary>
public enum VideoEncoder
{
    X264,
    Nvenc,
    QuickSync,
    Amf,
}

/// <param name="Monitor">The monitor recorded (Windows.Graphics.Capture records one monitor at a time).</param>
/// <param name="Area">The part of the monitor recorded, in virtual-screen pixels; even width and height.</param>
/// <param name="Quality">Constant-quality value (x264 CRF scale): lower is better and bigger.</param>
/// <param name="OutputPath">The .mp4 file to write.</param>
public sealed record RecordingOptions(
    MonitorInfo Monitor,
    PixelRect Area,
    int Fps,
    int Quality,
    bool IncludeCursor,
    bool ShowClicks,
    RecordingEncoder Encoder,
    string OutputPath);

/// <param name="FramesDropped">Frames skipped because encoding fell behind (the video is that much shorter).</param>
/// <param name="FramesRepeated">Frames written again because the screen didn't change (normal for a still screen).</param>
public sealed record RecordingResult(
    string Path,
    TimeSpan Duration,
    long FramesWritten,
    long FramesDropped,
    long FramesRepeated,
    VideoEncoder Encoder,
    long FileBytes)
{
    public double DropRate => FramesWritten + FramesDropped == 0 ? 0 : (double)FramesDropped / (FramesWritten + FramesDropped);
}

/// <summary>Records an area of the screen to an MP4 file.</summary>
public interface IScreenRecorder
{
    bool IsRecording { get; }

    bool IsPaused { get; }

    /// <summary>Time recorded so far, not counting pauses.</summary>
    TimeSpan Elapsed { get; }

    /// <summary>Raised when recording starts, pauses, resumes or stops (on any thread).</summary>
    event EventHandler? StateChanged;

    Task StartAsync(RecordingOptions options, CancellationToken ct = default);

    void Pause();

    void Resume();

    /// <summary>Stops and finishes the file.</summary>
    Task<RecordingResult> StopAsync(CancellationToken ct = default);
}

/// <summary>Finds ffmpeg and the encoders that work with it.</summary>
public interface IFfmpeg
{
    /// <summary>The ffmpeg.exe used, or null if there isn't one (recording is then unavailable).</summary>
    string? Path { get; }

    /// <summary>Where ffmpeg.exe is looked for (for the message when it's missing).</summary>
    string ExpectedPath { get; }

    /// <summary>The first encoder that actually works here, by preference (tested once, then remembered).</summary>
    Task<VideoEncoder> ChooseEncoderAsync(RecordingEncoder preference, CancellationToken ct = default);
}
