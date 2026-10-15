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

/// <summary>The sound in a recording.</summary>
[Flags]
public enum RecordingSound
{
    None = 0,

    /// <summary>What the PC plays (loopback of the default output device).</summary>
    System = 1,

    Microphone = 2,
}

/// <param name="Monitor">The monitor recorded (Windows.Graphics.Capture records one monitor at a time).</param>
/// <param name="Area">The part of the monitor recorded, in virtual-screen pixels; even width and height.</param>
/// <param name="Quality">Constant-quality value (x264 CRF scale): lower is better and bigger.</param>
/// <param name="OutputPath">The .mp4 file to write.</param>
/// <param name="SystemAudio">Record what the PC plays.</param>
/// <param name="Microphone">The microphone to record: null for none, <see cref="Settings.RecordingSettings.DefaultMicrophone"/> or a device ID.</param>
public sealed record RecordingOptions(
    MonitorInfo Monitor,
    PixelRect Area,
    int Fps,
    int Quality,
    bool IncludeCursor,
    bool ShowClicks,
    RecordingEncoder Encoder,
    string OutputPath,
    bool SystemAudio = false,
    string? Microphone = null);

/// <param name="FramesDropped">Frames skipped because encoding fell behind (the video is that much shorter).</param>
/// <param name="FramesRepeated">Frames written again because the screen didn't change (normal for a still screen).</param>
/// <param name="Sound">The sound recorded with the video.</param>
/// <param name="SoundNote">Why sound that was asked for is missing (no device, device removed), or null.</param>
public sealed record RecordingResult(
    string Path,
    TimeSpan Duration,
    long FramesWritten,
    long FramesDropped,
    long FramesRepeated,
    VideoEncoder Encoder,
    long FileBytes,
    RecordingSound Sound = RecordingSound.None,
    string? SoundNote = null)
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

    /// <summary>The sound being recorded (None when nothing is recording or there is no sound).</summary>
    RecordingSound Sound { get; }

    /// <summary>Why sound that was asked for isn't being recorded, or null.</summary>
    string? SoundNote { get; }

    /// <summary>While true the microphone is silent in the recording.</summary>
    bool MicrophoneMuted { get; set; }

    /// <summary>The loudest sound recorded since the last call, 0 to 1.</summary>
    float TakeSoundLevel();

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

/// <summary>A sound device as Windows names it.</summary>
public sealed record AudioDevice(string Id, string Name);

/// <summary>The sound devices a recording can use.</summary>
public interface IAudioDevices
{
    /// <summary>The microphones (and other inputs) connected now.</summary>
    IReadOnlyList<AudioDevice> Microphones();

    /// <summary>The default output device, which "system audio" records; null if there is none.</summary>
    AudioDevice? Speakers();
}
