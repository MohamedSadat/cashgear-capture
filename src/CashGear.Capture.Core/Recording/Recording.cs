using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;

namespace CashGear.Capture.Core.Recording;

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
/// <param name="Encoder"><see cref="EncoderPresets.Auto"/>, <see cref="EncoderPresets.Software"/> or an encoder's id.</param>
/// <param name="OutputPath">The .mp4 file to write (later parts, if the encoder changes, are named after it).</param>
/// <param name="SystemAudio">Record what the PC plays.</param>
/// <param name="SystemAudioDevice">The output device whose sound is recorded: a device ID, or null for Windows' default (followed when it changes).</param>
/// <param name="Microphone">The microphone to record: null for none, <see cref="Settings.RecordingSettings.DefaultMicrophone"/> or a device ID.</param>
/// <param name="Tracks">How system audio and the microphone become tracks when both are recorded.</param>
public sealed record RecordingOptions(
    MonitorInfo Monitor,
    PixelRect Area,
    int Fps,
    QualityPreset Quality,
    bool IncludeCursor,
    bool ShowClicks,
    string Encoder,
    string OutputPath,
    bool SystemAudio = false,
    string? Microphone = null,
    AudioTrackLayout Tracks = AudioTrackLayout.Mixed,
    float SystemAudioGain = 1,
    float MicrophoneGain = 1,
    string? SystemAudioDevice = null);

/// <param name="Path">The (first) file saved.</param>
/// <param name="FramesDropped">Frames skipped because encoding fell behind (the picture holds still for that long).</param>
/// <param name="FramesRepeated">Frames that showed the previous picture again because the screen didn't change (normal).</param>
/// <param name="EncoderName">The encoder used, e.g. "Windows hardware encoder (H.264)" (several, if it changed).</param>
/// <param name="FileBytes">All parts together.</param>
/// <param name="Sound">The sound recorded with the video.</param>
/// <param name="SoundNote">Why sound that was asked for is missing (no device, device removed), or null.</param>
/// <param name="Parts">Every file saved, in order: more than one when the encoder failed and another took over.</param>
/// <param name="Note">Anything else the user should know: the encoder changed, or the recording stopped early.</param>
public sealed record RecordingResult(
    string Path,
    TimeSpan Duration,
    long FramesWritten,
    long FramesDropped,
    long FramesRepeated,
    string EncoderName,
    long FileBytes,
    RecordingSound Sound = RecordingSound.None,
    string? SoundNote = null,
    IReadOnlyList<string>? Parts = null,
    string? Note = null)
{
    public double DropRate => FramesWritten + FramesDropped == 0 ? 0 : (double)FramesDropped / (FramesWritten + FramesDropped);

    public IReadOnlyList<string> AllParts => Parts ?? [Path];
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

    /// <summary>While true system audio is silent in the recording.</summary>
    bool SystemAudioMuted { get; set; }

    /// <summary>The microphone's volume, 1 as captured (0 to 4).</summary>
    float MicrophoneGain { get; set; }

    /// <summary>System audio's volume, 1 as captured (0 to 4).</summary>
    float SystemAudioGain { get; set; }

    /// <summary>True while a stopped recording is being saved; <see cref="SaveProgress"/> says how far (0 to 1).</summary>
    bool IsSaving { get; }

    double SaveProgress { get; }

    /// <summary>The loudest sound recorded since the last call, 0 to 1.</summary>
    float TakeSoundLevel();

    /// <summary>Raised when recording starts, pauses, resumes or stops (on any thread).</summary>
    event EventHandler? StateChanged;

    /// <summary>Something the user should know while recording continues, e.g. a microphone was unplugged (on any thread).</summary>
    event EventHandler<string>? Warning;

    /// <summary>Good news while recording, e.g. an unplugged microphone is back, or system audio follows a new default device (on any thread).</summary>
    event EventHandler<string>? Notice;

    /// <summary>The recording can't go on (e.g. the disk is full): stop it to save what was recorded (on any thread).</summary>
    event EventHandler<string>? Failed;

    Task StartAsync(RecordingOptions options, CancellationToken ct = default);

    void Pause();

    void Resume();

    /// <summary>Stops and finishes the file.</summary>
    Task<RecordingResult> StopAsync(CancellationToken ct = default);
}

/// <summary>FFmpeg inside the app, and the video encoders that work with it on this PC.</summary>
public interface IRecordingEncoders
{
    /// <summary>Why recording can't work (FFmpeg missing or unusable), in words for the user, or null.</summary>
    string? Problem { get; }

    /// <summary>FFmpeg's version text, or null when it isn't available.</summary>
    string? FfmpegVersion { get; }

    /// <summary>The results of the last detection (or of the cache), or null before there are any.</summary>
    IReadOnlyList<EncoderProbe>? Detected { get; }

    /// <summary>Finds out which encoders work (from the cache when it is still valid, unless <paramref name="force"/>).</summary>
    Task<IReadOnlyList<EncoderProbe>> DetectAsync(bool force = false, CancellationToken ct = default);
}

/// <summary>A sound device as Windows names it.</summary>
public sealed record AudioDevice(string Id, string Name);

/// <summary>The sound devices a recording can use.</summary>
public interface IAudioDevices
{
    /// <summary>The microphones (and other inputs) connected now.</summary>
    IReadOnlyList<AudioDevice> Microphones();

    /// <summary>The output devices connected now, whose sound "system audio" can record.</summary>
    IReadOnlyList<AudioDevice> Outputs();

    /// <summary>Windows' default output device, which system audio records unless another is chosen; null if there is none.</summary>
    AudioDevice? DefaultOutput();
}
