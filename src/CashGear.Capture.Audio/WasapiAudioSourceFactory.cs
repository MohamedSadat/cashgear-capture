using System.Runtime.InteropServices;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;

namespace CashGear.Capture.Audio;

/// <summary>Opens WASAPI endpoints for a recording: the default output device's loopback and microphones.</summary>
internal sealed partial class WasapiAudioSourceFactory(ILogger<WasapiAudioSourceFactory> logger) : IAudioSourceFactory
{
    public (IAudioSource? Source, string? Problem) OpenSystemAudio()
    {
        using var devices = new MMDeviceEnumerator();
        if (!devices.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out var device))
        {
            return (null, "No sound output device was found, so system audio isn't recorded.");
        }

        return Open(device, loopback: true, RecordingSound.System, $"system audio ({device.FriendlyName})");
    }

    public (IAudioSource? Source, string? Problem) OpenMicrophone(string id)
    {
        using var devices = new MMDeviceEnumerator();
        MMDevice? device = null;
        if (id == RecordingSettings.DefaultMicrophone)
        {
            if (!devices.TryGetDefaultAudioEndpoint(DataFlow.Capture, Role.Console, out device))
            {
                return (null, "No microphone is connected, so none is recorded.");
            }
        }
        else
        {
            try
            {
                device = devices.GetDevice(id);
            }
            catch (COMException ex)
            {
                LogNotFound(ex, id);
            }

            if (device is null || device.State != DeviceState.Active)
            {
                device?.Dispose();
                return (null, "The microphone chosen in Settings isn't connected, so it isn't recorded.");
            }
        }

        return Open(device, loopback: false, RecordingSound.Microphone, device.FriendlyName);
    }

    private (IAudioSource? Source, string? Problem) Open(MMDevice device, bool loopback, RecordingSound kind, string name)
    {
        try
        {
            return (WasapiAudioSource.Open(device, loopback, kind, name, logger), null);
        }
        catch (UnauthorizedAccessException ex)
        {
            device.Dispose();
            LogOpenFailed(ex, name);
            return (null, $"Windows doesn't let apps use {name} (Settings › Privacy › Microphone › Allow desktop apps to access your microphone), so it isn't recorded.");
        }
        catch (COMException ex)
        {
            device.Dispose();
            LogOpenFailed(ex, name);
            return (null, $"{name} couldn't be used ({ex.Message.Trim()}), so it isn't recorded.");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not open {Name} for recording")]
    private partial void LogOpenFailed(Exception ex, string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "Microphone {Id} not found")]
    private partial void LogNotFound(Exception ex, string id);
}
