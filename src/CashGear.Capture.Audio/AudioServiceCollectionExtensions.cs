using CashGear.Capture.Core.Recording;
using Microsoft.Extensions.DependencyInjection;

namespace CashGear.Capture.Audio;

public static class AudioServiceCollectionExtensions
{
    /// <summary>The sound devices: sources for recordings and the device lists for Settings.</summary>
    public static IServiceCollection AddAudio(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<AudioDeviceWatcher>();
        services.AddSingleton<AudioApps>();
        services.AddSingleton<IAudioApps>(s => s.GetRequiredService<AudioApps>());
        services.AddSingleton<IAudioSourceFactory, WasapiAudioSourceFactory>();
        services.AddSingleton<IAudioDevices, WasapiAudioDevices>();
        services.AddSingleton<MicrophoneProcessors>();
        return services;
    }
}
