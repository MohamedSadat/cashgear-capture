using System.Runtime.InteropServices;
using CashGear.Capture.Core.Recording;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CashGear.Capture.Audio;

/// <summary>
/// Plays 48 kHz stereo float on the default output device (WASAPI shared mode; Windows converts it to the device's
/// format), for the microphone test. Its own thread keeps the device's buffer filled.
/// </summary>
internal static class TestPlayback
{
    private static readonly TimeSpan BufferLength = TimeSpan.FromMilliseconds(100);

    /// <exception cref="COMException">There is no output device, or it can't be used.</exception>
    public static Task PlayAsync(float[] samples, CancellationToken ct) =>
        Task.Factory.StartNew(() => Play(samples, ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static unsafe void Play(float[] samples, CancellationToken ct)
    {
        using var devices = new MMDeviceEnumerator();
        using var device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        using var client = device.CreateAudioClient();
        client.Initialize(
            AudioClientShareMode.Shared,
            AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
            BufferLength.Ticks,
            0,
            WaveFormat.CreateIeeeFloatWaveFormat(AudioMixer.SampleRate, AudioMixer.Channels),
            Guid.Empty);
        using var render = client.AudioRenderClient;
        var capacity = client.BufferSize;
        var frames = samples.Length / AudioMixer.Channels;
        var written = 0;
        var started = false;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var free = capacity - client.CurrentPadding;
                if (written >= frames)
                {
                    if (client.CurrentPadding == 0)
                    {
                        break; // all of it played
                    }
                }
                else if (free > 0)
                {
                    var count = Math.Min(free, frames - written);
                    var buffer = render.GetBuffer(count);
                    new ReadOnlySpan<float>(samples, written * AudioMixer.Channels, count * AudioMixer.Channels)
                        .CopyTo(new Span<float>((void*)buffer, count * AudioMixer.Channels));
                    render.ReleaseBuffer(count, AudioClientBufferFlags.None);
                    written += count;
                }

                if (!started)
                {
                    client.Start();
                    started = true;
                }

                Thread.Sleep(10);
            }
        }
        finally
        {
            if (started)
            {
                client.Stop();
            }
        }
    }
}
