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
internal sealed partial class WasapiAudioSourceFactory(AudioDeviceWatcher watcher, AudioApps apps, ILoggerFactory loggers) : IAudioSourceFactory
{
    private readonly ILogger _logger = loggers.CreateLogger<WasapiAudioSourceFactory>();

    public AudioOpenResult OpenSystemAudio(string? device) =>
        Open(RecordingSound.System, DataFlow.Render, string.IsNullOrEmpty(device) ? null : device, "System audio");

    public AudioOpenResult OpenMicrophone(string id) =>
        Open(RecordingSound.Microphone, DataFlow.Capture, id == RecordingSettings.DefaultMicrophone ? null : id, "Microphone");

    public AudioOpenResult OpenAppAudio(string exe, string appName, bool exclude)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exe);
        if (!apps.IsSupported)
        {
            return AudioOpenResult.Failed($"Recording {(exclude ? "all sound except one app" : "one app's sound")} needs Windows 10 version 2004 or later.");
        }

        var root = apps.FindRoot(exe);
        var target = root ?? (exclude ? Environment.ProcessId : 0);
        var first = target == 0 ? new EndpointOpened(null, null, Missing: "not running") : OpenProcess(target, exclude, appName);
        if (first.Problem is { } problem)
        {
            return AudioOpenResult.Failed(problem);
        }

        var source = new AppAudioSource(
            exe, appName, exclude, (process, leaveOut) => OpenProcess(process, leaveOut, appName), apps.FindRoot, watcher, first, target,
            loggers.CreateLogger<AppAudioSource>());
        var note = root is not null ? null
            : exclude ? $"{appName} isn't running, so all sound is recorded; once it starts, its sound is left out."
            : $"{appName} isn't running, so its sound is silent until it starts.";
        return new AudioOpenResult(source, note);
    }

    /// <summary>Process loopback for <paramref name="process"/>'s tree (or everything else).</summary>
    internal EndpointOpened OpenProcess(int process, bool exclude, string appName)
    {
        var name = exclude ? $"system audio except {appName}" : $"{appName}'s sound";
        try
        {
            return new EndpointOpened(WasapiAudioSource.OpenProcessLoopback(process, exclude, name, _logger), process.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is COMException or PlatformNotSupportedException or InvalidOperationException or ArgumentException)
        {
            LogOpenFailed(ex, name);
            return new EndpointOpened(null, null, Problem: $"{name} couldn't be recorded ({ex.Message.Trim()}).");
        }
    }

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
