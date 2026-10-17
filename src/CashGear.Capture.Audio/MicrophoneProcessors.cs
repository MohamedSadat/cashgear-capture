using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.Audio;

/// <summary>Makes the microphone's processing chain for a recording or the microphone test.</summary>
public sealed class MicrophoneProcessors
{
    /// <summary>Whether noise suppression can be offered (RNNoise is there).</summary>
    public bool CanSuppressNoise => false;

    /// <summary>A chain for live sound, stamped with <see cref="System.Diagnostics.Stopwatch"/> timestamps.</summary>
    public MicrophoneChain Create(MicrophoneProcessing settings) => new(settings);

    /// <summary>A chain for <see cref="MicrophoneChain.ProcessAll"/>.</summary>
    public MicrophoneChain CreateOffline(MicrophoneProcessing settings) => new(settings, frequency: Core.Recording.AudioMixer.SampleRate);
}
