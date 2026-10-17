using System.Diagnostics;
using System.Runtime.InteropServices;
using CashGear.Capture.Core.Recording;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CashGear.Capture.Audio;

/// <summary>
/// One WASAPI endpoint in shared mode, converted by Windows to 48 kHz stereo float (AUTOCONVERTPCM): loopback of an
/// output device ("what you hear") or a microphone. Its own thread polls every 10 ms. Each packet is stamped with
/// the time Windows gives for its first frame (QPC), so where it lands doesn't depend on when the thread woke up.
/// NAudio provides only the Core Audio interop; this loop replaces its <c>WasapiCapture</c>, which drops the
/// timestamps, polls every 50 ms and can throw from its capture thread when a device disappears.
/// </summary>
internal sealed partial class WasapiAudioSource : IAudioEndpoint
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

    private WasapiAudioSource(MMDevice device, AudioClient client, string name, ILogger logger)
    {
        _device = device;
        _client = client;
        _capture = client.AudioCaptureClient;
        _logger = logger;
        Name = name;
    }

    public string Name { get; }

    /// <summary>Initializes the device for capture; takes ownership of <paramref name="device"/>.</summary>
    /// <exception cref="UnauthorizedAccessException">Windows privacy settings block the microphone.</exception>
    /// <exception cref="COMException">The device can't be used (in use exclusively, gone, ...).</exception>
    public static WasapiAudioSource Open(MMDevice device, bool loopback, string name, ILogger logger)
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
            return new WasapiAudioSource(device, client, name, logger);
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
