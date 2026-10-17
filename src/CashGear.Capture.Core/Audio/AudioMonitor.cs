using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.Core.Audio;

/// <summary>What Settings' meters listen to: the sound a recording with these settings would record.</summary>
/// <param name="Microphone">A device ID, <see cref="RecordingSettings.DefaultMicrophone"/>, or null for none.</param>
/// <param name="SystemAudio">Listen to system audio as <paramref name="Scope"/> says.</param>
public sealed record MonitorRequest(
    string? Microphone,
    MicrophoneProcessing Processing,
    bool SystemAudio,
    string? SystemAudioDevice = null,
    SystemAudioScope Scope = SystemAudioScope.All,
    string? App = null,
    string? AppName = null);

/// <summary>
/// Listens to the sound devices for Settings: level meters for the microphone (as processed) and system audio, and
/// the microphone test, which records a few seconds and plays them back as recorded or processed.
/// </summary>
public interface IAudioMonitor : IDisposable
{
    /// <summary>Whether noise suppression can be used (RNNoise is there).</summary>
    bool CanSuppressNoise { get; }

    /// <summary>Why something asked for isn't heard (no device, blocked), or null.</summary>
    string? Problem { get; }

    /// <summary>Starts listening as <paramref name="request"/> says, instead of whatever it listened to before.</summary>
    void Start(MonitorRequest request);

    /// <summary>Changes how the microphone is processed while listening.</summary>
    void SetProcessing(MicrophoneProcessing processing);

    void Stop();

    /// <summary>The levels heard since the last call (the mix is left silent).</summary>
    SoundLevels TakeLevels();

    /// <summary>Records the microphone as captured (48 kHz interleaved stereo) for <paramref name="length"/>.</summary>
    Task<float[]> RecordMicrophoneAsync(string microphone, TimeSpan length, CancellationToken ct = default);

    /// <summary>The microphone test's recording as a recording with <paramref name="processing"/> would have it.</summary>
    float[] Process(float[] recorded, MicrophoneProcessing processing);

    /// <summary>Plays 48 kHz interleaved stereo on the default output device; returns when it has played, or is cancelled.</summary>
    Task PlayAsync(float[] samples, CancellationToken ct = default);
}
