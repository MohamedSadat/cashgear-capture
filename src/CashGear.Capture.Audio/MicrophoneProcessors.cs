using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.Audio;

/// <summary>Makes the microphone's processing chain for a recording or the microphone test, with RNNoise when it is there.</summary>
public sealed class MicrophoneProcessors
{
    /// <param name="noiseSuppression">Whether to use RNNoise when it is there (tests turn it off).</param>
    public MicrophoneProcessors(bool noiseSuppression = true) => CanSuppressNoise = noiseSuppression && RnnoiseLibrary.IsAvailable;

    /// <summary>Whether noise suppression can be offered (RNNoise is there).</summary>
    public bool CanSuppressNoise { get; }

    /// <summary>Why noise suppression can't be offered, or null.</summary>
    public string? NoiseSuppressionProblem => CanSuppressNoise ? null : RnnoiseLibrary.Problem ?? "Noise suppression is turned off.";

    /// <summary>A chain for live sound, stamped with <see cref="System.Diagnostics.Stopwatch"/> timestamps.</summary>
    public MicrophoneChain Create(MicrophoneProcessing settings) => new(settings, Suppressor());

    /// <summary>A chain for <see cref="MicrophoneChain.ProcessAll"/>.</summary>
    public MicrophoneChain CreateOffline(MicrophoneProcessing settings) => new(settings, Suppressor(), Core.Recording.AudioMixer.SampleRate);

    private RnnoiseSuppressor? Suppressor() => CanSuppressNoise ? new RnnoiseSuppressor() : null;
}
