using System.Diagnostics;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.Audio;

/// <summary>
/// Receives a packet of 48 kHz stereo float frames (interleaved), the first captured at <paramref name="start"/>
/// (a <see cref="Stopwatch"/> timestamp). <paramref name="discontinuity"/> means sound was lost just before it.
/// </summary>
public delegate void AudioDataHandler(ReadOnlySpan<float> samples, long start, bool discontinuity);

/// <summary>What happened to a source's device while it was recording.</summary>
public enum AudioSourceStatus
{
    /// <summary>The device stopped working (unplugged, disabled, taken): its sound is silent until it is back.</summary>
    Lost,

    /// <summary>The device that was lost works again and is recorded.</summary>
    Restored,

    /// <summary>Windows' default device changed, and the source now records the new one.</summary>
    Switched,
}

/// <summary>Called (on a worker thread) when a source's device changes; <paramref name="message"/> is for the user.</summary>
public delegate void AudioStatusHandler(AudioSourceStatus status, string message);

/// <summary>A source of sound for a recording, delivering packets on its own thread once started.</summary>
public interface IAudioSource : IDisposable
{
    /// <summary>E.g. "system audio (Headphones (H5))" or "Microphone (H5)", for logs and messages (the device recorded now).</summary>
    string Name { get; }

    RecordingSound Kind { get; }

    /// <summary>Starts capturing. <paramref name="status"/> reports a device that stops working, comes back or is replaced.</summary>
    void Start(AudioDataHandler data, AudioStatusHandler status);
}

/// <summary>
/// Opens the sound a recording asks for. A device that isn't connected comes back as a source that waits for it,
/// with a note; one that can't be used at all (blocked by privacy settings, held exclusively) as a reason instead.
/// </summary>
public interface IAudioSourceFactory
{
    /// <summary>What an output device plays: <paramref name="device"/> is a device ID, or null for Windows' default output device (followed when it changes).</summary>
    AudioOpenResult OpenSystemAudio(string? device);

    /// <summary>A microphone: a device ID, or <see cref="RecordingSettings.DefaultMicrophone"/> (followed when Windows' default changes).</summary>
    AudioOpenResult OpenMicrophone(string id);
}

/// <param name="Source">The source, or null when there is nothing to record.</param>
/// <param name="Note">Why there is no source, or why it is silent for now (its device isn't connected), for the user; null when all is well.</param>
public sealed record AudioOpenResult(IAudioSource? Source, string? Note)
{
    public static AudioOpenResult Failed(string problem) => new(null, problem);
}
