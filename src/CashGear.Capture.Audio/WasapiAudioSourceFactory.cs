using System.ComponentModel;
using System.Runtime.InteropServices;
using CashGear.Capture.Audio.Resources;
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
        Open(RecordingSound.System, DataFlow.Render, string.IsNullOrEmpty(device) ? null : device, AudioStrings.SystemAudio);

    public AudioOpenResult OpenMicrophone(string id) =>
        Open(RecordingSound.Microphone, DataFlow.Capture, id == RecordingSettings.DefaultMicrophone ? null : id, AudioStrings.Microphone);

    public AudioOpenResult OpenAppAudio(string exe, string appName, bool exclude)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exe);
        if (!apps.IsSupported)
        {
            return AudioOpenResult.Failed(exclude ? AudioStrings.ExcludeAppNeedsNewerWindows : AudioStrings.OneAppNeedsNewerWindows);
        }

        var root = apps.FindRoot(exe);
        var target = root ?? (exclude ? Environment.ProcessId : 0);
        var first = target == 0 ? new EndpointOpened(null, null, Missing: AudioStrings.NotRunning) : OpenProcess(target, exclude, appName);
        if (first.Problem is { } problem)
        {
            return AudioOpenResult.Failed(problem);
        }

        var source = new AppAudioSource(
            exe, appName, exclude, (process, leaveOut) => OpenProcess(process, leaveOut, appName), apps.FindRoot, watcher, first, target,
            loggers.CreateLogger<AppAudioSource>());
        var note = root is not null ? null
            : exclude ? AudioStrings.ExcludedAppNotRunning(appName)
            : AudioStrings.RecordedAppNotRunning(appName);
        return new AudioOpenResult(source, note);
    }

    /// <summary>Process loopback for <paramref name="process"/>'s tree (or everything else).</summary>
    internal EndpointOpened OpenProcess(int process, bool exclude, string appName)
    {
        var name = exclude ? AudioStrings.SystemAudioExcept(appName) : AudioStrings.AppSound(appName);
        try
        {
            return new EndpointOpened(WasapiAudioSource.OpenProcessLoopback(process, exclude, name, _logger), process.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is COMException or PlatformNotSupportedException or InvalidOperationException or ArgumentException)
        {
            LogOpenFailed(ex, name);
            return new EndpointOpened(null, null, Problem: AudioStrings.CouldNotRecord(name, ex.Message.Trim()));
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
                        ? AudioStrings.NoOutputDevice
                        : AudioStrings.NoMicrophone);
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
                        ? AudioStrings.ChosenOutputDeviceMissing
                        : AudioStrings.ChosenMicrophoneMissing);
                }
            }
        }
        catch (COMException ex)
        {
            device?.Dispose();
            LogOpenFailed(ex, render ? "the output devices" : "the microphones");
            return new EndpointOpened(null, null, Problem: AudioStrings.DevicesNotListed(ex.Message.Trim()));
        }

        var name = render ? AudioStrings.SystemAudioDevice(device.FriendlyName) : device.FriendlyName;
        var deviceId = device.ID;
        try
        {
            return new EndpointOpened(WasapiAudioSource.Open(device, loopback: render, name, _logger), deviceId);
        }
        catch (UnauthorizedAccessException ex)
        {
            device.Dispose();
            LogOpenFailed(ex, name);
            return new EndpointOpened(null, deviceId, Problem: AudioStrings.DeviceNotAllowed(name));
        }
        catch (COMException ex)
        {
            device.Dispose();
            LogOpenFailed(ex, name);
            return new EndpointOpened(null, deviceId, Problem: AudioStrings.DeviceUnusable(name, ex.Message.Trim()));
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
    private partial void LogOpenFailed(Exception ex, [Localizable(false)] string name); // for the log

    [LoggerMessage(Level = LogLevel.Information, Message = "Sound device {Id} not found")]
    private partial void LogNotFound(Exception ex, string id);
}
