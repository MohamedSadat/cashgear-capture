using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Recording;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace CashGear.Capture.SyncTest;

/// <summary>
/// The clapperboard: a short 1 kHz beep through the default output device every <see cref="Interval"/>, and a
/// black square on screen that turns white when each beep starts. The beeps are placed by the sound card's own
/// clock (sample positions); each flash is timed from the card's clock read against QPC, so the two stay together
/// however the card's clock drifts. The square's display delay (a frame or two) is a constant offset, which the
/// analysis reports apart from drift.
/// </summary>
internal sealed class Stimulus : IDisposable
{
    public const int SampleRate = 48000;
    public const int BeepSamples = SampleRate / 20; // 50 ms
    public const float BeepLevel = 0.2f;
    private static readonly TimeSpan FlashLength = TimeSpan.FromMilliseconds(150);

    private readonly MMDevice _device;
    private readonly AudioClient _client;
    private readonly AudioRenderClient _render;
    private readonly AudioClockClient _clock;
    private readonly Window _square;
    private readonly long _period;
    private readonly long _firstBeep;
    private readonly Thread _audioThread;
    private readonly Thread _flashThread;
    private volatile bool _stopping;
    private long _written;

    public Stimulus(PixelRect square, double scale, TimeSpan interval)
    {
        Interval = interval;
        _period = (long)(interval.TotalSeconds * SampleRate);
        _firstBeep = SampleRate; // the first beep a second in
        using (var devices = new MMDeviceEnumerator())
        {
            _device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }

        _client = _device.CreateAudioClient();
        _client.Initialize(
            AudioClientShareMode.Shared,
            AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
            TimeSpan.FromMilliseconds(60).Ticks,
            0,
            WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2),
            Guid.Empty);
        _render = _client.AudioRenderClient;
        _clock = _client.AudioClockClient;
        DeviceName = _device.FriendlyName;

        _square = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Left = square.X / scale,
            Top = square.Y / scale,
            Width = square.Width / scale,
            Height = square.Height / scale,
            Background = Brushes.Black,
            Title = "CashGear sync test",
        };
        _audioThread = new Thread(RenderLoop) { IsBackground = true, Name = "Sync test sound", Priority = ThreadPriority.Highest };
        _flashThread = new Thread(FlashLoop) { IsBackground = true, Name = "Sync test flashes", Priority = ThreadPriority.Highest };
    }

    public TimeSpan Interval { get; }

    public string DeviceName { get; }

    /// <summary>Flashes shown so far.</summary>
    public int Flashes { get; private set; }

    /// <summary>Shows the square and starts the beeps and flashes (call on the UI thread).</summary>
    public void Start()
    {
        _square.Show();
        Prefill();
        _client.Start();
        _audioThread.Start();
        _flashThread.Start();
    }

    public void Dispose()
    {
        _stopping = true;
        _audioThread.Join(TimeSpan.FromSeconds(2));
        _flashThread.Join(TimeSpan.FromSeconds(2));
        try
        {
            _client.Stop();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // the device went away
        }

        _client.Dispose();
        _device.Dispose();
        _square.Dispatcher.Invoke(_square.Close);
    }

    private void Prefill()
    {
        var frames = _client.BufferSize - _client.CurrentPadding;
        Write(frames);
    }

    private void RenderLoop()
    {
        try
        {
            while (!_stopping)
            {
                Thread.Sleep(5);
                var free = _client.BufferSize - _client.CurrentPadding;
                if (free > 0)
                {
                    Write(free);
                }
            }
        }
        catch (System.Runtime.InteropServices.COMException e)
        {
            // The output device went away (e.g. unplugged): no more beeps, and no more flashes either.
            Console.WriteLine($"Beeps stopped: {e.Message}");
            _stopping = true;
        }
    }

    private unsafe void Write(int frames)
    {
        var buffer = (float*)_render.GetBuffer(frames);
        for (var i = 0; i < frames; i++)
        {
            var n = _written + i - _firstBeep;
            var phase = n >= 0 ? n % _period : -1;
            var value = phase is >= 0 and < BeepSamples ? BeepLevel * MathF.Sin(2 * MathF.PI * 1000 * phase / SampleRate) : 0f;
            buffer[2 * i] = value;
            buffer[(2 * i) + 1] = value;
        }

        _render.ReleaseBuffer(frames, AudioClientBufferFlags.None);
        _written += frames;
    }

    /// <summary>For each beep: when the card will play its first sample (read from the card's clock), turn the square white.</summary>
    private void FlashLoop()
    {
        var white = Brushes.White;
        var black = Brushes.Black;
        for (var k = 0L; !_stopping; k++)
        {
            var beepAt = _firstBeep + (k * _period);
            long target;
            do
            {
                ulong position, qpc;
                try
                {
                    _clock.GetPosition(out position, out qpc);
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    return; // the output device went away
                }

                var playedSeconds = position / (double)_clock.Frequency;
                var qpcTicks = QpcTime.FromHundredNanoseconds((long)qpc, Stopwatch.Frequency);
                target = qpcTicks + (long)(((beepAt / (double)SampleRate) - playedSeconds) * Stopwatch.Frequency);
                var wait = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), target);
                if (wait > TimeSpan.FromMilliseconds(30))
                {
                    Thread.Sleep(wait - TimeSpan.FromMilliseconds(20)); // then read the clock again, closer in
                    continue;
                }

                break;
            }
            while (!_stopping);

            while (!_stopping && Stopwatch.GetTimestamp() < target)
            {
                Thread.SpinWait(50);
            }

            if (_stopping)
            {
                return;
            }

            _square.Dispatcher.Invoke(() => _square.Background = white);
            Flashes++;
            Thread.Sleep(FlashLength);
            _square.Dispatcher.Invoke(() => _square.Background = black);
        }
    }
}
