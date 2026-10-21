using System.Diagnostics;
using System.IO;
using CashGear.Capture.Audio;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Engine.Capture.Wgc;
using CashGear.Capture.Recording;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.App.Tests.Recording;

/// <summary>
/// The long-run test: hours of recording on a clock that runs many times faster than real time, from a fake picture
/// stream and a fake microphone with the same clock, through the real encoders and file. Memory is sampled every
/// ten recorded minutes; the last hour must stay where the first was. Explicit: it takes several minutes.
/// </summary>
[Collection(RecordingFaultCollection.Name)]
public sealed class RecordingSoakTests : IDisposable
{
    private static readonly MonitorInfo Monitor = new(1, @"\\.\DISPLAY7", new PixelRect(0, 0, 320, 180), new PixelRect(0, 0, 320, 160), 96, 96, IsPrimary: true);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cashgear-tests", Guid.NewGuid().ToString("N"));
    private readonly WgcInterop _interop = new();
    private readonly ILoggerFactory _loggers = TestLog.Create();
    private readonly RecordingStore _store;

    public RecordingSoakTests() => _store = new RecordingStore(TestFfmpeg.Paths(_folder));

    public void Dispose()
    {
        _interop.Dispose();
        _loggers.Dispose();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact(Explicit = true, Timeout = 3_600_000)]
    public async Task Four_recorded_hours_keep_memory_flat()
    {
        TestFfmpeg.Require();
        var ct = TestContext.Current.CancellationToken;
        const double Speed = 30;
        var hours = double.TryParse(Environment.GetEnvironmentVariable("CASHGEAR_SOAK_HOURS"), System.Globalization.CultureInfo.InvariantCulture, out var h) ? h : 4;
        var time = new FastTime(Speed);
        var sound = new FakeSound(microphone: () => new FastTone(time));
        var recorder = new ScreenRecorder(TestFfmpeg.Encoders, _store, _interop, sound, _loggers, desktop: new TestDesktopState(), power: new TestPowerEvents()) { Time = time };
        recorder.StreamFactory = (request, _) => Task.FromResult<IPictureStream>(new FakePictureStream(request, time, 30));
        var options = new RecordingOptions(
            Monitor, Monitor.Bounds, 30, QualityPreset.Small, IncludeCursor: false, EncoderPresets.Software, Path.Combine(_folder, "soak.mp4"),
            Microphone: RecordingSettings.DefaultMicrophone, Overlays: new OverlaySettings(BurnIn: true, WriteMetadata: true, Pointer: new PointerOverlaySettings(PointerEffect.Halo)));

        var samples = new List<(double Minutes, double HeapMb, double PrivateMb, long Frames)>();
        var started = Stopwatch.StartNew();
        await recorder.StartAsync(options, ct);
        using var process = Process.GetCurrentProcess();
        for (var minute = 10; minute <= hours * 60; minute += 10)
        {
            while (recorder.Elapsed.TotalMinutes < minute)
            {
                await Task.Delay(50, ct);
            }

            var heap = GC.GetTotalMemory(forceFullCollection: true) / 1048576.0;
            process.Refresh();
            var counts = recorder.Allocations!.Value;
            samples.Add((minute, heap, process.PrivateMemorySize64 / 1048576.0, counts.Frames));
        }

        var result = await recorder.StopAsync(ct);
        var report = string.Join(Environment.NewLine, samples.Select(s => $"{s.Minutes,4:0} min: GC heap {s.HeapMb,6:0.0} MB, private {s.PrivateMb,6:0.0} MB, {s.Frames} frames"));
        TestContext.Current.TestOutputHelper?.WriteLine(report);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "cashgear-soak.txt"),
            $"{hours} recorded hours at {Speed}x in {started.Elapsed}; {result.FramesWritten} frames written, {result.FramesDropped} skipped (encoding behind the fast clock), {result.FileBytes} bytes{Environment.NewLine}{report}");

        // After the first hour has warmed everything up, the last hour holds the same.
        var firstHour = samples.Where(s => s.Minutes is > 10 and <= 60).ToList();
        var lastHour = samples.Where(s => s.Minutes > (hours * 60) - 60).ToList();
        lastHour.Max(s => s.HeapMb).Should().BeLessThan(firstHour.Max(s => s.HeapMb) + 5, "the managed heap doesn't grow");
        lastHour.Max(s => s.PrivateMb).Should().BeLessThan(firstHour.Max(s => s.PrivateMb) + 20, "the process doesn't grow");
        result.Duration.TotalHours.Should().BeApproximately(hours, 0.01);
    }

    /// <summary>A clock running <paramref name="speed"/> times faster than real time (QPC units).</summary>
    private sealed class FastTime(double speed) : TimeProvider
    {
        private readonly long _origin = Stopwatch.GetTimestamp();

        public override long TimestampFrequency => Stopwatch.Frequency;

        public override long GetTimestamp() => _origin + (long)((Stopwatch.GetTimestamp() - _origin) * speed);
    }

    /// <summary>A microphone on the fast clock: 10 ms packets of a quiet tone, stamped with the clock's time, as fast as that clock asks.</summary>
    private sealed class FastTone(TimeProvider time) : IAudioSource
    {
        private const int Packet = 480;
        private Thread? _thread;
        private volatile bool _stopping;

        public string Name => "fast tone";

        public RecordingSound Kind => RecordingSound.Microphone;

        public void Start(AudioDataHandler data, AudioStatusHandler status)
        {
            _thread = new Thread(() => Run(data)) { IsBackground = true, Name = "Fast tone" };
            _thread.Start();
        }

        public void Dispose()
        {
            _stopping = true;
            _thread?.Join();
        }

        private void Run(AudioDataHandler data)
        {
            var samples = new float[Packet * 2];
            var step = time.TimestampFrequency * Packet / 48_000;
            var next = time.GetTimestamp();
            long frame = 0;
            while (!_stopping)
            {
                if (time.GetTimestamp() < next + step)
                {
                    Thread.Sleep(1);
                    continue;
                }

                for (var i = 0; i < Packet; i++, frame++)
                {
                    samples[i * 2] = samples[(i * 2) + 1] = 0.05f * MathF.Sin(2 * MathF.PI * 440 * frame / 48_000f);
                }

                data(samples, next, discontinuity: false);
                next += step;
            }
        }
    }
}
