using System.Diagnostics;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.Audio;

/// <summary>
/// Receives a packet of 48 kHz stereo float frames (interleaved), the first captured at <paramref name="start"/>
/// (a <see cref="Stopwatch"/> timestamp). <paramref name="discontinuity"/> means sound was lost just before it.
/// </summary>
public delegate void AudioDataHandler(ReadOnlySpan<float> samples, long start, bool discontinuity);

/// <summary>A source of sound for a recording, delivering packets on its own thread once started.</summary>
public interface IAudioSource : IDisposable
{
    /// <summary>E.g. "system audio (Headphones (H5))" or "Microphone (H5)", for logs and messages.</summary>
    string Name { get; }

    RecordingSound Kind { get; }

    /// <summary>Starts capturing. <paramref name="failed"/> is called (once, on the capture thread) if the device stops working.</summary>
    void Start(AudioDataHandler data, Action<Exception> failed);
}

/// <summary>Opens the sound a recording asks for; a device that can't be used comes back as a reason instead.</summary>
public interface IAudioSourceFactory
{
    /// <summary>What the default output device plays.</summary>
    (IAudioSource? Source, string? Problem) OpenSystemAudio();

    /// <summary>A microphone: a device ID, or <see cref="RecordingSettings.DefaultMicrophone"/>.</summary>
    (IAudioSource? Source, string? Problem) OpenMicrophone(string id);
}
