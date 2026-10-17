using System.Diagnostics;
using System.Runtime.InteropServices;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CashGear.Capture.Recording;

/// <summary>
/// Receives a packet of 48 kHz stereo float frames (interleaved), the first captured at <paramref name="start"/>
/// (a <see cref="Stopwatch"/> timestamp). <paramref name="discontinuity"/> means sound was lost just before it.
/// </summary>
internal delegate void AudioDataHandler(ReadOnlySpan<float> samples, long start, bool discontinuity);

/// <summary>A source of sound for a recording, delivering packets on its own thread once started.</summary>
internal interface IAudioSource : IDisposable
{
    /// <summary>E.g. "system audio (Headphones (H5))" or "Microphone (H5)", for logs and messages.</summary>
    string Name { get; }

    RecordingSound Kind { get; }

    /// <summary>Starts capturing. <paramref name="failed"/> is called (once, on the capture thread) if the device stops working.</summary>
    void Start(AudioDataHandler data, Action<Exception> failed);
}

/// <summary>Opens the sound a recording asks for; a device that can't be used comes back as a reason instead.</summary>
internal interface IAudioSourceFactory
{
    /// <summary>What the default output device plays.</summary>
    (IAudioSource? Source, string? Problem) OpenSystemAudio();

    /// <summary>A microphone: a device ID, or <see cref="RecordingSettings.DefaultMicrophone"/>.</summary>
    (IAudioSource? Source, string? Problem) OpenMicrophone(string id);
}

/// <summary>
/// One WASAPI endpoint in shared mode, converted by Windows to 48 kHz stereo float (AUTOCONVERTPCM): loopback of an
/// output device ("what you hear") or a microphone. Its own thread polls every 10 ms. Each packet is stamped with
/// the time Windows gives for its first frame (QPC), so where it lands doesn't depend on when the thread woke up.
/// NAudio provides only the Core Audio interop; this loop replaces its <c>WasapiCapture</c>, which drops the
/// timestamps, polls every 50 ms and can throw from its capture thread when a device disappears.
/// </summary>
internal sealed partial class WasapiAudioSource : IAudioSource
{
    private static readonly TimeSpan BufferLength = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// Windows' timestamp is trusted unless it is missing, more than 200 ms ahead of the packet's arrival (loopback
    /// stamps run a few ms ahead: they say when the sound plays), or more than 5 s old. A packet delivered late keeps
    /// its own time; the arrival time would put it after a pause it was captured in.
    /// </summary>
    private static readonly long MaxStampAhead = Stopwatch.Frequency / 5;

    private static readonly long MaxStampAge = Stopwatch.Frequency * 5;

    private readonly MMDevice _device;
    private readonly AudioClient _client;
    private readonly AudioCaptureClient _capture;
    private readonly ILogger _logger;
    private float[] _silence = new float[AudioMixer.SampleRate / 10 * AudioMixer.Channels];
    private Thread? _thread;
    private volatile bool _stopping;
    private bool _started;

    private WasapiAudioSource(MMDevice device, AudioClient client, RecordingSound kind, string name, ILogger logger)
    {
        _device = device;
        _client = client;
        _capture = client.AudioCaptureClient;
        _logger = logger;
        Kind = kind;
        Name = name;
    }

    public string Name { get; }

    public RecordingSound Kind { get; }

    /// <summary>Initializes the device for capture; takes ownership of <paramref name="device"/>.</summary>
    /// <exception cref="UnauthorizedAccessException">Windows privacy settings block the microphone.</exception>
    /// <exception cref="COMException">The device can't be used (in use exclusively, gone, ...).</exception>
    public static WasapiAudioSource Open(MMDevice device, bool loopback, RecordingSound kind, string name, ILogger logger)
    {
        var client = device.CreateAudioClient();
        try
        {
            var flags = AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality
                | (loopback ? AudioClientStreamFlags.Loopback : AudioClientStreamFlags.None);
            client.Initialize(
                AudioClientShareMode.Shared,
                flags,
                BufferLength.Ticks,
                0,
                WaveFormat.CreateIeeeFloatWaveFormat(AudioMixer.SampleRate, AudioMixer.Channels),
                Guid.Empty);
            return new WasapiAudioSource(device, client, kind, name, logger);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public void Start(AudioDataHandler data, Action<Exception> failed)
    {
        _client.Start();
        _started = true;
        _thread = new Thread(() => Run(data, failed)) { IsBackground = true, Name = "Recording sound", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Dispose()
    {
        _stopping = true;
        _thread?.Join(TimeSpan.FromSeconds(2));
        if (_started)
        {
            try
            {
                _client.Stop();
            }
            catch (COMException ex)
            {
                LogStopFailed(ex, Name); // e.g. the device is already gone
            }
        }

        _capture.Dispose();
        _client.Dispose();
        _device.Dispose();
    }

    /// <summary>A QPC value in 100 ns units (as WASAPI gives it) as a <see cref="Stopwatch"/> timestamp, without overflowing.</summary>
    internal static long FromHundredNanoseconds(long value) => QpcTime.FromHundredNanoseconds(value, Stopwatch.Frequency);

    /// <summary>When the packet's first frame was captured: Windows' timestamp, or arrival time minus its length if that is missing or implausible.</summary>
    internal static long Stamp(long qpcPosition, int frames, long arrived)
    {
        var estimate = arrived - (frames * Stopwatch.Frequency / AudioMixer.SampleRate);
        if (qpcPosition <= 0)
        {
            return estimate;
        }

        var stamped = FromHundredNanoseconds(qpcPosition);
        return stamped - estimate <= MaxStampAhead && estimate - stamped <= MaxStampAge ? stamped : estimate;
    }

    private unsafe void Run(AudioDataHandler data, Action<Exception> failed)
    {
        try
        {
            while (!_stopping)
            {
                Thread.Sleep(PollInterval);
                while (!_stopping && _capture.GetNextPacketSize() > 0)
                {
                    var buffer = _capture.GetBuffer(out var frames, out var flags, out _, out var qpcPosition);
                    try
                    {
                        var start = Stamp(qpcPosition, frames, Stopwatch.GetTimestamp());
                        var discontinuity = (flags & AudioClientBufferFlags.DataDiscontinuity) != 0;
                        var length = frames * AudioMixer.Channels;
                        if ((flags & AudioClientBufferFlags.Silent) != 0)
                        {
                            if (_silence.Length < length)
                            {
                                _silence = new float[length];
                            }

                            data(_silence.AsSpan(0, length), start, discontinuity);
                        }
                        else
                        {
                            data(new ReadOnlySpan<float>((void*)buffer, length), start, discontinuity);
                        }
                    }
                    finally
                    {
                        _capture.ReleaseBuffer(frames);
                    }
                }
            }
        }
#pragma warning disable CA1031 // A device that fails must never take the app down: report it and stop this source.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (!_stopping)
            {
                LogFailed(ex, Name);
                failed(ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sound from {Name} stopped")]
    private partial void LogFailed(Exception ex, string name);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Stopping {Name} failed")]
    private partial void LogStopFailed(Exception ex, string name);
}

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

/// <summary>The sound devices Windows has now, for the settings window.</summary>
internal sealed partial class WasapiAudioDevices(ILogger<WasapiAudioDevices> logger) : IAudioDevices
{
    public IReadOnlyList<AudioDevice> Microphones()
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            var list = new List<AudioDevice>();
            foreach (var device in devices.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
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

    public AudioDevice? Speakers()
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not list sound devices")]
    private partial void LogListFailed(Exception ex);
}
