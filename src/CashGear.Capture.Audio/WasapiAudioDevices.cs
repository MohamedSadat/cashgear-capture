using System.Runtime.InteropServices;
using CashGear.Capture.Core.Recording;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;

namespace CashGear.Capture.Audio;

/// <summary>The sound devices Windows has now, for the settings window.</summary>
internal sealed partial class WasapiAudioDevices(ILogger<WasapiAudioDevices> logger) : IAudioDevices
{
    public IReadOnlyList<AudioDevice> Microphones() => List(DataFlow.Capture);

    public IReadOnlyList<AudioDevice> Outputs() => List(DataFlow.Render);

    public AudioDevice? DefaultOutput()
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            if (!devices.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out var device))
            {
                return null;
            }

            using (device)
            {
                return new AudioDevice(device.ID, device.FriendlyName);
            }
        }
        catch (COMException ex)
        {
            LogListFailed(ex);
            return null;
        }
    }

    private IReadOnlyList<AudioDevice> List(DataFlow flow)
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            var list = new List<AudioDevice>();
            foreach (var device in devices.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (device)
                {
                    list.Add(new AudioDevice(device.ID, device.FriendlyName));
                }
            }

            return list;
        }
        catch (COMException ex)
        {
            LogListFailed(ex);
            return [];
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not list sound devices")]
    private partial void LogListFailed(Exception ex);
}
