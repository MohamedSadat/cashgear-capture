using System.Runtime.InteropServices;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;

namespace CashGear.Capture.Audio;

/// <summary>
/// Opens WASAPI endpoints for a recording: an output device's loopback (system audio) and microphones, each as a
/// <see cref="FollowingAudioSource"/> that follows Windows' default device or waits for an unplugged one.
/// </summary>
internal sealed partial class WasapiAudioSourceFactory(AudioDeviceWatcher watcher, ILoggerFactory loggers) : IAudioSourceFactory
{
    private readonly ILogger _logger = loggers.CreateLogger<WasapiAudioSourceFactory>();

    public AudioOpenResult OpenSystemAudio(string? device) =>
        Open(RecordingSound.System, DataFlow.Render, string.IsNullOrEmpty(device) ? null : device, "System audio");

    public AudioOpenResult OpenMicrophone(string id) =>
        Open(RecordingSound.Microphone, DataFlow.Capture, id == RecordingSettings.DefaultMicrophone ? null : id, "Microphone");

    /// <summary>Opens one endpoint of <paramref name="flow"/>: <paramref name="id"/>, or (null) the default one now.</summary>
    internal EndpointOpened OpenEndpoint(DataFlow flow, string? id)
    {
        var render = flow == DataFlow.Render;
        MMDevice? device = null;
        try
        {
            using var devices = new MMDeviceEnumerator();
            if (id is null)
            {
                if (!devices.TryGetDefaultAudioEndpoint(flow, render ? Role.Multimedia : Role.Console, out device))
                {
                    return new EndpointOpened(null, null, render
                        ? "No sound output device is connected, so system audio is silent until one is."
                        : "No microphone is connected, so the microphone is silent until one is.");
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
                    return new EndpointOpened(null, null, render
                        ? "The output device chosen in Settings isn't connected, so system audio is silent until it is."
                        : "The microphone chosen in Settings isn't connected, so it is silent until it is.");
                }
            }
        }
        catch (COMException ex)
        {
            device?.Dispose();
            LogOpenFailed(ex, render ? "the output devices" : "the microphones");
            return new EndpointOpened(null, null, Problem: $"Windows' sound devices couldn't be listed ({ex.Message.Trim()}).");
        }

        var name = render ? $"system audio ({device.FriendlyName})" : device.FriendlyName;
        var deviceId = device.ID;
        try
        {
            return new EndpointOpened(WasapiAudioSource.Open(device, loopback: render, name, _logger), deviceId);
        }
        catch (UnauthorizedAccessException ex)
        {
            device.Dispose();
            LogOpenFailed(ex, name);
            return new EndpointOpened(null, deviceId, Problem: $"Windows doesn't let apps use {name} (Settings › Privacy › Microphone › Allow desktop apps to access your microphone), so it isn't recorded.");
        }
        catch (COMException ex)
        {
            device.Dispose();
            LogOpenFailed(ex, name);
            return new EndpointOpened(null, deviceId, Problem: $"{name} couldn't be used ({ex.Message.Trim()}), so it isn't recorded.");
        }
    }

    private AudioOpenResult Open(RecordingSound kind, DataFlow flow, string? wanted, string waitingName)
    {
        var first = OpenEndpoint(flow, wanted);
        if (first.Problem is { } problem)
        {
            return AudioOpenResult.Failed(problem);
        }

        var source = new FollowingAudioSource(
            kind, flow, wanted, id => OpenEndpoint(flow, id), watcher, first, waitingName, loggers.CreateLogger<FollowingAudioSource>());
        return new AudioOpenResult(source, first.Missing);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not open {Name} for recording")]
    private partial void LogOpenFailed(Exception ex, string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sound device {Id} not found")]
    private partial void LogNotFound(Exception ex, string id);
}
