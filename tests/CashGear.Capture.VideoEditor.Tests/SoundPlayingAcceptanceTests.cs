using System.Diagnostics;
using System.IO;
using CashGear.Capture.Audio;
using CashGear.Capture.Media.Tests;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Playback;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.VideoEditor.Tests;

/// <summary>Timed against a device's real pace, so it runs alone, after the other tests of the assembly.</summary>
[Collection(nameof(ExportAcceptance))]
public sealed class SoundPlayingAcceptanceTests(ITestOutputHelper output) : IDisposable
{
    private const int Rate = 48_000;

    private readonly TestMediaFiles _media = new("cashgear-sound-playing-");

    public void Dispose() => _media.Dispose();

    /// <summary>A device taking sound as fast as it is played, a little at a time, noting what it got.</summary>
    private sealed class RealTimeOutput : IAudioOutput
    {
        private readonly List<float> _heard = [];
        private Thread? _thread;
        private volatile bool _stopping;
        private long _played;

        public long Played => Interlocked.Read(ref _played);

        public void Start(AudioPull pull)
        {
            _stopping = false;
            _thread = new Thread(() =>
            {
                var block = new float[480 * 2];
                var watch = Stopwatch.StartNew();
                var sent = 0L;
                while (!_stopping)
                {
                    if (watch.Elapsed.TotalSeconds * Rate >= sent + 480)
                    {
                        pull(block);
                        _heard.AddRange(block);
                        sent += 480;
                        Interlocked.Add(ref _played, 480);
                    }
                    else
                    {
                        Thread.Sleep(1);
                    }
                }
            }) { IsBackground = true };
            _thread.Start();
        }

        public void Stop()
        {
            _stopping = true;
            _thread?.Join();
        }

        public void Dispose() => Stop();

        /// <summary>
        /// How long it took for the first sound to be heard, and the longest stretch of silence after that (when it started), in
        /// seconds, up to a time after the first sound. A late start isn't a gap: the timeline waits for it.
        /// </summary>
        public (double Lead, double Longest, double At) Silences(double upTo)
        {
            var lead = 0;
            while (lead < _heard.Count / 2 && Math.Abs(_heard[2 * lead]) < 1e-4f)
            {
                lead++;
            }

            var (longest, run, at) = (0, 0, 0);
            for (var i = lead; i < Math.Min(_heard.Count / 2, lead + (int)(upTo * Rate)); i++)
            {
                run = Math.Abs(_heard[2 * i]) < 1e-4f ? run + 1 : 0;
                if (run > longest)
                {
                    (longest, at) = (run, i - run + 1);
                }
            }

            return ((double)lead / Rate, (double)longest / Rate, (double)at / Rate);
        }
    }

    [Fact]
    public void Playing_clips_with_effects_across_jumps_in_their_source_never_runs_dry()
    {
        TestMedia.Require();
        Assert.SkipUnless(RnnoiseLibrary.IsAvailable, RnnoiseLibrary.Problem ?? "RNNoise isn't available (run tools\\get-rnnoise.ps1).");
        using var turn = new CashGear.Capture.Tests.MachineTurn();

        // Each clip starts at the far end of a stretch of the stream: as much as a seek can have to work through. Half the original
        // is kept, so a tone that RNNoise takes for noise is still there to hear.
        var path = Path.Combine(_media.Root, "jumps.wav");
        Phase22Fixture.WriteWav(path, 30, (_, t) => (0.3 * Math.Sin(2 * Math.PI * 613 * t), 0.3 * Math.Sin(2 * Math.PI * 613 * t)));
        var asset = ProjectFactory.ReadAsset(path, "jumps");
        var sound = new ClipSound(NoiseRemoval: true, NoiseStrength: 0.5, Gate: true, GateThresholdDb: -60, Compressor: true, Limiter: true, Eq: SoundEq.Of(EqPreset.Voice));
        Clip Part(string id, double start, double sourceIn) => new() { Id = id, AssetId = "jumps", Stream = 0, Start = start, SourceIn = sourceIn, SourceOut = sourceIn + 1.5, Sound = sound };
        var project = new VideoProject(VideoProject.CurrentVersion, new ProjectCanvas(320, 180, 30), [asset], [new Track("sound", TrackKind.Audio, "Sound", [Part("one", 0, 4), Part("two", 1.5, 14.9), Part("three", 3, 24.9)])]);
        using var device = new RealTimeOutput();
        using var engine = new PlaybackEngine(() => project, () => device, NullLogger.Instance);
        engine.Play(0);
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed.TotalSeconds < 4.3)
        {
            Thread.Sleep(20);
        }

        engine.Pause();
        var (lead, longest, at) = device.Silences(3.9);
        output.WriteLine($"The first sound after {lead * 1000:0.0} ms; the longest silence, over two jumps: {longest * 1000:0.0} ms, at {at:0.00} s");
        longest.Should().BeLessThan(0.005, "the sound already mixed lasts while the next clip gets ready beside it (at {0} s)", at);
        device.Played.Should().BeGreaterThan(4 * Rate, "the device was playing all along");
    }
}
