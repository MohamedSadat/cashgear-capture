using System.ComponentModel;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CashGear.Capture.VideoEditor.Playback;

/// <summary>Fills a block with the next sound to play (48 kHz stereo float, interleaved); silence when there is none.</summary>
public delegate void AudioPull(Span<float> interleaved);

/// <summary>Where the preview's sound plays, and its clock: the samples the device has played.</summary>
public interface IAudioOutput : IDisposable
{
    /// <summary>Starts playing what <paramref name="pull"/> gives, pulling as the device needs it.</summary>
    /// <exception cref="COMException">No output device, or it can't be used.</exception>
    void Start(AudioPull pull);

    void Stop();

    /// <summary>Stereo frames played (heard) since <see cref="Start"/>.</summary>
    long Played { get; }
}

/// <summary>
/// The default output device through WASAPI in shared mode (Windows converts 48 kHz float to the device's format),
/// filled by a thread of its own every 10 ms. The clock is what was written minus what still waits in the device's
/// buffer, so it follows what is heard.
/// </summary>
public sealed class WasapiAudioOutput : IAudioOutput
{
    private static readonly TimeSpan BufferLength = TimeSpan.FromMilliseconds(100);

    /// <summary>The thread's name, for debuggers.</summary>
    [Localizable(false)]
    private const string ThreadName = "Video editor sound";

    private Thread? _thread;
    private volatile bool _stopping;
    private long _written;
    private long _waiting;
    private Exception? _failure;

    public long Played => Math.Max(0, Interlocked.Read(ref _written) - Interlocked.Read(ref _waiting));

    /// <summary>Why the device stopped, if it did.</summary>
    public Exception? Failure => Volatile.Read(ref _failure);

    public void Start(AudioPull pull)
    {
        ArgumentNullException.ThrowIfNull(pull);
        Stop();
        _stopping = false;
        _written = 0;
        _waiting = 0;
        var ready = new ManualResetEventSlim();
        Exception? startFailure = null;
        _thread = new Thread(() => Run(pull, ready, e => startFailure = e)) { IsBackground = true, Name = ThreadName, Priority = ThreadPriority.Highest };
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
        if (startFailure is not null)
        {
            Stop();
            throw startFailure;
        }
    }

    public void Stop()
    {
        _stopping = true;
        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }

    public void Dispose() => Stop();

    private unsafe void Run(AudioPull pull, ManualResetEventSlim ready, Action<Exception> failed)
    {
        MMDeviceEnumerator? devices = null;
        MMDevice? device = null;
        AudioClient? client = null;
        var started = false;
        try
        {
            devices = new MMDeviceEnumerator();
            device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            client = device.CreateAudioClient();
            client.Initialize(
                AudioClientShareMode.Shared,
                AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
                BufferLength.Ticks,
                0,
                WaveFormat.CreateIeeeFloatWaveFormat(TimelineMixer.SampleRate, TimelineMixer.Channels),
                Guid.Empty);
            var render = client.AudioRenderClient;
            var capacity = client.BufferSize;
            var block = new float[capacity * TimelineMixer.Channels];
            ready.Set();
            while (!_stopping)
            {
                var padding = client.CurrentPadding;
                Interlocked.Exchange(ref _waiting, padding);
                var free = capacity - padding;
                if (free > 0)
                {
                    var samples = block.AsSpan(0, free * TimelineMixer.Channels);
                    pull(samples);
                    var buffer = render.GetBuffer(free);
                    samples.CopyTo(new Span<float>((void*)buffer, samples.Length));
                    render.ReleaseBuffer(free, AudioClientBufferFlags.None);
                    Interlocked.Add(ref _written, free);
                    Interlocked.Exchange(ref _waiting, padding + free);
                }

                if (!started)
                {
                    client.Start();
                    started = true;
                }

                Thread.Sleep(5);
            }
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or NotSupportedException)
        {
            Volatile.Write(ref _failure, e);
            failed(e);
            ready.Set();
        }
        finally
        {
            if (started)
            {
                client?.Stop();
            }

            client?.Dispose();
            device?.Dispose();
            devices?.Dispose();
        }
    }
}
