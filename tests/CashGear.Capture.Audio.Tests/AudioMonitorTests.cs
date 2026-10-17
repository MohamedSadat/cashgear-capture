using CashGear.Capture.Core.Audio;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Engine.Tests;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.Audio.Tests;

public sealed class AudioMonitorTests
{
    [Fact]
    public void The_meters_measure_system_audio_and_the_processed_microphone()
    {
        var sources = new FakeSources();
        using var monitor = new AudioMonitor(sources, new MicrophoneProcessors(), NullLogger<AudioMonitor>.Instance);

        monitor.Start(new MonitorRequest("default", new MicrophoneProcessing(), SystemAudio: true));
        sources.System!.Deliver(0.5f, 480);
        sources.Microphone!.Deliver(1.8f, 960); // limited by the processing to -1 dB

        var levels = monitor.TakeLevels();
        levels.System.Peak.Should().Be(0.5f);
        levels.Microphone.Peak.Should().BeApproximately(0.891f, 0.001f);
        levels.Microphone.Clipped.Should().BeFalse("the limiter keeps it from clipping");
        monitor.TakeLevels().System.Peak.Should().Be(0, "taken");

        monitor.SetProcessing(new MicrophoneProcessing(Limiter: false));
        sources.Microphone.Deliver(1.8f, 960);
        monitor.TakeLevels().Microphone.Clipped.Should().BeTrue("the limiter was switched off");

        monitor.Stop();
        sources.System.Disposed.Should().BeTrue();
        sources.Microphone.Disposed.Should().BeTrue();
    }

    [Fact]
    public void Only_one_app_or_a_device_is_opened_as_asked_and_problems_are_reported()
    {
        var sources = new FakeSources { MicrophoneNote = "The microphone chosen in Settings isn't connected." };
        using var monitor = new AudioMonitor(sources, new MicrophoneProcessors(), NullLogger<AudioMonitor>.Instance);

        monitor.Start(new MonitorRequest("{gone}", new MicrophoneProcessing(), SystemAudio: true, Scope: SystemAudioScope.OnlyApp, App: "spotify.exe", AppName: "Spotify"));
        sources.Opened.Should().Equal("app spotify.exe include", "microphone {gone}");
        monitor.Problem.Should().Be("The microphone chosen in Settings isn't connected.");

        monitor.Start(new MonitorRequest(null, new MicrophoneProcessing(), SystemAudio: true, SystemAudioDevice: "{out}"));
        sources.Opened.Should().EndWith("system {out}");
        monitor.Problem.Should().BeNull();
    }

    [Fact]
    public async Task The_test_records_the_microphone_as_captured_while_the_meters_listen()
    {
        var sources = new FakeSources();
        using var monitor = new AudioMonitor(sources, new MicrophoneProcessors(), NullLogger<AudioMonitor>.Instance);
        monitor.Start(new MonitorRequest("default", new MicrophoneProcessing(), SystemAudio: false));

        var recording = monitor.RecordMicrophoneAsync("default", TimeSpan.FromSeconds(0.02), TestContext.Current.CancellationToken);
        sources.Microphone!.Deliver(1.8f, 480);
        sources.Microphone.Deliver(1.8f, 960);

        var samples = await recording;
        samples.Should().HaveCount(960 * 2, "20 ms of stereo");
        samples.Should().OnlyContain(s => s == 1.8f, "as captured, before the limiter");
        sources.Opened.Should().ContainSingle("the meters' microphone is used, not opened again");

        var processed = monitor.Process(samples, new MicrophoneProcessing());
        processed.Should().HaveCount(samples.Length);
        processed.Max(Math.Abs).Should().BeLessThanOrEqualTo(0.8913f);
    }

    [Fact]
    public async Task Without_the_meters_the_test_opens_the_microphone_itself()
    {
        var sources = new FakeSources();
        using var monitor = new AudioMonitor(sources, new MicrophoneProcessors(), NullLogger<AudioMonitor>.Instance);

        var recording = monitor.RecordMicrophoneAsync("{h5}", TimeSpan.FromSeconds(0.01), TestContext.Current.CancellationToken);
        await Task.Yield();
        sources.Microphone!.Deliver(0.25f, 480);

        (await recording).Should().HaveCount(960);
        sources.Microphone.Disposed.Should().BeTrue("closed after the test");
    }

    /// <summary>The real microphone (Windows' default), half a second, nothing played.</summary>
    [Fact(Timeout = 30_000)]
    [Trait("Category", InteractiveDesktop.Category)]
    public async Task The_real_default_microphone_can_be_recorded_for_the_test()
    {
        InteractiveDesktop.Require();
        Assert.SkipWhen(new WasapiAudioDevices(NullLogger<WasapiAudioDevices>.Instance).Microphones().Count == 0, "This PC has no microphone.");
        using var watcher = new AudioDeviceWatcher(NullLogger<AudioDeviceWatcher>.Instance);
        var factory = new WasapiAudioSourceFactory(watcher, new AudioApps(NullLogger<AudioApps>.Instance), NullLoggerFactory.Instance);
        using var monitor = new AudioMonitor(factory, new MicrophoneProcessors(), NullLogger<AudioMonitor>.Instance);

        monitor.Start(new MonitorRequest(RecordingSettings.DefaultMicrophone, new MicrophoneProcessing(), SystemAudio: true));
        var samples = await monitor.RecordMicrophoneAsync(RecordingSettings.DefaultMicrophone, TimeSpan.FromSeconds(0.5), TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        var levels = monitor.TakeLevels();
        monitor.Stop();

        samples.Should().HaveCount(48000);
        levels.Microphone.Rms.Should().BeGreaterThan(0, "a real microphone always hears something");
    }

    private sealed class FakeSources : IAudioSourceFactory
    {
        public List<string> Opened { get; } = [];

        public string? MicrophoneNote { get; init; }

        public FakeSource? System { get; private set; }

        public FakeSource? Microphone { get; private set; }

        public AudioOpenResult OpenSystemAudio(string? device)
        {
            Opened.Add($"system {device ?? "default"}");
            System = new FakeSource(RecordingSound.System);
            return new AudioOpenResult(System, null);
        }

        public AudioOpenResult OpenMicrophone(string id)
        {
            Opened.Add($"microphone {id}");
            Microphone = new FakeSource(RecordingSound.Microphone);
            return new AudioOpenResult(Microphone, MicrophoneNote);
        }

        public AudioOpenResult OpenAppAudio(string exe, string appName, bool exclude)
        {
            Opened.Add($"app {exe} {(exclude ? "exclude" : "include")}");
            System = new FakeSource(RecordingSound.System);
            return new AudioOpenResult(System, null);
        }
    }

    private sealed class FakeSource(RecordingSound kind) : IAudioSource
    {
        private AudioDataHandler? _data;
        private long _next;

        public string Name => kind.ToString();

        public RecordingSound Kind => kind;

        public bool Disposed { get; private set; }

        public void Start(AudioDataHandler data, AudioStatusHandler status) => _data = data;

        /// <summary>A packet of <paramref name="frames"/> frames all at <paramref name="value"/>.</summary>
        public void Deliver(float value, int frames)
        {
            var samples = Enumerable.Repeat(value, frames * 2).ToArray();
            _data!(samples, _next * System.Diagnostics.Stopwatch.Frequency / 48000, false);
            _next += frames;
        }

        public void Dispose() => Disposed = true;
    }
}
