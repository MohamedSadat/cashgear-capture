using System.Diagnostics;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Engine.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.CoreAudioApi;

namespace CashGear.Capture.Audio.Tests;

public sealed class ProcessTreeTests
{
    // explorer (1) ─┬─ chrome (10) ─┬─ chrome (11, the audio service)
    //               │               └─ chrome (12) ── helper.exe (13)
    //               ├─ chrome (20, a second browser started separately)
    //               └─ spotify (30) ── spotify (31)
    private static readonly ProcessEntry[] Processes =
    [
        new(1, 0, "explorer.exe"),
        new(10, 1, "chrome.exe"),
        new(11, 10, "chrome.exe"),
        new(12, 10, "Chrome.exe"),
        new(13, 12, "helper.exe"),
        new(20, 1, "chrome.exe"),
        new(30, 1, "Spotify.exe"),
        new(31, 30, "spotify.exe"),
    ];

    [Fact]
    public void An_app_is_recorded_from_the_top_of_its_tree_preferring_the_biggest()
    {
        ProcessTree.Roots(Processes, "chrome.exe").Should().Equal(10, 20);
        ProcessTree.Roots(Processes, "SPOTIFY.EXE").Should().Equal(30);
        ProcessTree.Roots(Processes, "notepad.exe").Should().BeEmpty();
    }

    [Fact]
    public void The_tree_with_sound_open_comes_first()
    {
        ProcessTree.Roots(Processes, "chrome.exe", new HashSet<int> { 31, 20 }).Should().Equal(20, 10);
        ProcessTree.Roots(Processes, "chrome.exe", new HashSet<int> { 11 }).Should().Equal([10, 20], "its audio service is under 10");
    }

    [Fact]
    public void A_process_belongs_to_the_top_process_of_the_program_above_it()
    {
        var byId = Processes.ToDictionary(p => p.Id);

        ProcessTree.RootOf(byId, 11, "chrome.exe").Should().Be(10);
        ProcessTree.RootOf(byId, 13, "chrome.exe").Should().Be(10, "a child of another program is still in the tree");
        ProcessTree.RootOf(byId, 31, "chrome.exe").Should().Be(0);
        ProcessTree.RootOf(byId, 999, "chrome.exe").Should().Be(0);
    }

    [Fact]
    public void A_process_whose_parent_id_was_reused_by_itself_does_not_loop()
    {
        ProcessTree.Roots([new ProcessEntry(5, 5, "app.exe")], "app.exe").Should().Equal(5);
    }

    [Fact]
    public void The_snapshot_has_this_process()
    {
        var snapshot = ProcessTree.Snapshot();

        snapshot.Should().Contain(p => p.Id == Environment.ProcessId);
        var self = snapshot.Single(p => p.Id == Environment.ProcessId);
        self.Exe.Should().EndWithEquivalentOf(".exe");
        ProcessTree.Roots(snapshot, self.Exe).Should().Contain(Environment.ProcessId);
    }
}

public sealed class AppAudioSourceTests
{
    private readonly ImmediateEvents _events = new();
    private readonly List<(int Process, bool Exclude, FakeEndpoint Endpoint)> _opened = [];
    private readonly List<(AudioSourceStatus Status, string Message)> _statuses = [];
    private readonly List<bool> _discontinuities = [];
    private int? _running;
    private bool _refuse;

    [Fact]
    public void An_app_that_is_not_running_is_recorded_from_when_it_starts()
    {
        using var source = Create(exclude: false);
        source.Name.Should().Be("Spotify's sound");
        source.Target.Should().Be(0);

        source.Poll();
        _opened.Should().BeEmpty();

        _running = 4321;
        source.Poll();

        _opened.Should().ContainSingle().Which.Should().Match<(int Process, bool Exclude, FakeEndpoint Endpoint)>(o => o.Process == 4321 && !o.Exclude);
        _statuses.Should().ContainSingle().Which.Should().Be((AudioSourceStatus.Restored, "Spotify started; its sound is being recorded."));
        _opened[0].Endpoint.Deliver();
        _discontinuities.Should().Equal(true);
    }

    [Fact]
    public void An_app_that_restarts_is_followed_to_its_new_process()
    {
        _running = 100;
        using var source = Create(exclude: false);
        var first = _opened.Single().Endpoint;

        _running = null; // quit: its silent recording is kept
        source.Poll();
        first.Disposed.Should().BeFalse();

        _running = 200;
        source.Poll();

        first.Disposed.Should().BeTrue();
        source.Target.Should().Be(200);
        _statuses.Should().ContainSingle().Which.Message.Should().Be("Spotify was started again; its sound is being recorded again.");
        first.Deliver();
        _discontinuities.Should().BeEmpty("the old process's packets are dropped");
    }

    [Fact]
    public void Leaving_out_an_app_that_is_not_running_leaves_out_this_process_until_it_starts()
    {
        using var source = Create(exclude: true);
        source.Name.Should().Be("system audio except Spotify");
        _opened.Should().ContainSingle().Which.Should().Match<(int Process, bool Exclude, FakeEndpoint Endpoint)>(o => o.Process == Environment.ProcessId && o.Exclude);

        source.Poll();
        _opened.Should().ContainSingle("nothing changed");

        _running = 555;
        source.Poll();

        _opened.Should().HaveCount(2);
        _opened[1].Process.Should().Be(555);
        _opened[1].Exclude.Should().BeTrue();
        _opened[0].Endpoint.Disposed.Should().BeTrue();
        _statuses.Should().ContainSingle().Which.Should().Be((AudioSourceStatus.Switched, "Spotify is running now, so its sound is left out of the recording."));

        _running = null;
        source.Poll();
        _opened.Should().HaveCount(3, "once it quits, this process is left out again");
        _opened[2].Process.Should().Be(Environment.ProcessId);
        _statuses.Should().ContainSingle();
    }

    [Fact]
    public void A_capture_that_fails_is_reported_and_opened_again()
    {
        _running = 100;
        using var source = Create(exclude: false);

        _opened.Single().Endpoint.Fail(new InvalidOperationException("Audio service restarted"));
        _statuses.Should().ContainSingle().Which.Should().Be((AudioSourceStatus.Lost, "Spotify's sound stopped (Audio service restarted). It is silent in the recording until it can be recorded again."));

        _refuse = true;
        source.Poll();
        _statuses.Should().ContainSingle("a failed retry isn't reported again");

        _refuse = false;
        source.Poll();
        _statuses.Should().HaveCount(2);
        _statuses[1].Should().Be((AudioSourceStatus.Restored, "Spotify's sound is being recorded again."));
    }

    [Fact]
    public void Disposing_stops_the_capture_and_the_polling()
    {
        _running = 100;
        var source = Create(exclude: false);

        source.Dispose();
        _running = 200;
        source.Poll();

        _opened.Should().ContainSingle().Which.Endpoint.Disposed.Should().BeTrue();
    }

    private AppAudioSource Create(bool exclude)
    {
        var target = _running ?? (exclude ? Environment.ProcessId : 0);
        var first = target == 0 ? new EndpointOpened(null, null, Missing: "not running") : Open(target, exclude);
        var source = new AppAudioSource(
            "spotify.exe", "Spotify", exclude, Open, _ => _running, _events, first, target, NullLogger.Instance, Timeout.InfiniteTimeSpan);
        source.Start((_, _, discontinuity) => _discontinuities.Add(discontinuity), (status, message) => _statuses.Add((status, message)));
        return source;
    }

    private EndpointOpened Open(int process, bool exclude)
    {
        if (_refuse)
        {
            return new EndpointOpened(null, null, Problem: "refused");
        }

        var endpoint = new FakeEndpoint();
        _opened.Add((process, exclude, endpoint));
        return new EndpointOpened(endpoint, process.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private sealed class FakeEndpoint : IAudioEndpoint
    {
        private AudioDataHandler? _data;
        private Action<Exception>? _failed;

        public string Name => "fake";

        public bool Disposed { get; private set; }

        public void Start(AudioDataHandler data, Action<Exception> failed)
        {
            _data = data;
            _failed = failed;
        }

        public void Deliver() => _data!([0f, 0f], 0, discontinuity: false);

        public void Fail(Exception ex) => _failed!(ex);

        public void Dispose() => Disposed = true;
    }

    private sealed class ImmediateEvents : IAudioDeviceEvents
    {
        public event Action<DataFlow, string?>? DefaultChanged
        {
            add { }
            remove { }
        }

        public event Action<string>? Arrived
        {
            add { }
            remove { }
        }

        public event Action<string>? Departed
        {
            add { }
            remove { }
        }

        public void Post(Action work) => work();
    }
}

/// <summary>Process loopback on this PC (Windows 10 2004 or later), recording processes that play nothing, so it is silent.</summary>
[Trait("Category", InteractiveDesktop.Category)]
public sealed class ProcessLoopbackTests
{
    [Fact(Timeout = 30_000)]
    public async Task One_app_is_recorded_continuously_with_windows_timestamps()
    {
        InteractiveDesktop.Require();
        var apps = new AudioApps(NullLogger<AudioApps>.Instance);
        Assert.SkipUnless(apps.IsSupported, "Process loopback needs Windows 10 2004 or later.");
        using var watcher = new AudioDeviceWatcher(NullLogger<AudioDeviceWatcher>.Instance);
        var factory = new WasapiAudioSourceFactory(watcher, apps, NullLoggerFactory.Instance);
        var self = ProcessTree.Snapshot().Single(p => p.Id == Environment.ProcessId).Exe;

        var opened = factory.OpenAppAudio(self, "the tests", exclude: false);

        opened.Note.Should().BeNull("this process is running");
        using var source = opened.Source.Should().BeOfType<AppAudioSource>().Subject;
        source.Target.Should().Be(Environment.ProcessId);
        var frames = 0L;
        var first = 0L;
        var last = 0L;
        source.Start(
            (samples, start, _) =>
            {
                if (first == 0)
                {
                    first = start;
                }

                last = start + (samples.Length / 2 * Stopwatch.Frequency / 48000);
                frames += samples.Length / 2;
            },
            (_, _) => { });
        await Task.Delay(2000, TestContext.Current.CancellationToken);
        source.Dispose();

        // Process loopback sends silence while nothing plays (device loopback sends nothing), every frame of it.
        var span = (double)(last - first) / Stopwatch.Frequency;
        span.Should().BeGreaterThan(1.5);
        (frames / 48000.0).Should().BeApproximately(span, 0.02, "no frame is lost between the 10 ms polls");
    }

    [Fact]
    public void An_app_that_is_not_running_waits_with_a_note()
    {
        InteractiveDesktop.Require();
        var apps = new AudioApps(NullLogger<AudioApps>.Instance);
        Assert.SkipUnless(apps.IsSupported, "Process loopback needs Windows 10 2004 or later.");
        using var watcher = new AudioDeviceWatcher(NullLogger<AudioDeviceWatcher>.Instance);
        var factory = new WasapiAudioSourceFactory(watcher, apps, NullLoggerFactory.Instance);

        var only = factory.OpenAppAudio("no-such-app-cashgear.exe", "Nothing", exclude: false);
        var except = factory.OpenAppAudio("no-such-app-cashgear.exe", "Nothing", exclude: true);

        only.Note.Should().Be("Nothing isn't running, so its sound is silent until it starts.");
        except.Note.Should().Contain("all sound is recorded");
        using var a = only.Source.Should().BeOfType<AppAudioSource>().Subject;
        using var b = except.Source.Should().BeOfType<AppAudioSource>().Subject;
        a.Target.Should().Be(0);
        b.Target.Should().Be(Environment.ProcessId);
    }

    [Fact]
    public void Apps_with_windows_are_listed_once_each()
    {
        InteractiveDesktop.Require();
        var list = new AudioApps(NullLogger<AudioApps>.Instance).List();

        list.Select(a => a.Exe).Should().OnlyHaveUniqueItems();
        list.Should().OnlyContain(a => a.Exe.EndsWith(".exe", StringComparison.Ordinal) && a.Name.Length > 0);
        list.Should().NotContain(a => a.Exe == "explorer.exe");
    }

    /// <summary>
    /// The acceptance test, explicit because it plays two tones on the speakers for a few seconds: one helper process
    /// plays 440 Hz and another 1 kHz. Recording only the first must hear only 440 Hz; everything but the first, only 1 kHz.
    /// </summary>
    [Fact(Explicit = true, Timeout = 60_000)]
    public async Task Only_the_chosen_app_is_recorded_while_another_plays_too()
    {
        InteractiveDesktop.Require();
        var apps = new AudioApps(NullLogger<AudioApps>.Instance);
        using var watcher = new AudioDeviceWatcher(NullLogger<AudioDeviceWatcher>.Instance);
        var factory = new WasapiAudioSourceFactory(watcher, apps, NullLoggerFactory.Instance);
        var folder = Directory.CreateTempSubdirectory("cashgear-app-audio").FullName;
        using var low = TonePlayer.Start(folder, 440);
        using var high = TonePlayer.Start(folder, 1000);
        await Task.Delay(1500, TestContext.Current.CancellationToken); // both playing

        var only = await Capture(factory.OpenProcess(low.Id, exclude: false, "440 Hz"));
        var except = await Capture(factory.OpenProcess(low.Id, exclude: true, "all but 440 Hz"));

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Only the 440 Hz app: 440 Hz {Tone.Level(only, 440):0.0000}, 1 kHz {Tone.Level(only, 1000):0.000000}; all but it: 440 Hz {Tone.Level(except, 440):0.000000}, 1 kHz {Tone.Level(except, 1000):0.0000}");
        Tone.Level(only, 440).Should().BeGreaterThan(0.01f, "the chosen app is heard");
        Tone.Level(only, 1000).Should().BeLessThan(Tone.Level(only, 440) / 100, "the other app is not (40 dB down)");
        Tone.Level(except, 1000).Should().BeGreaterThan(0.01f);
        Tone.Level(except, 440).Should().BeLessThan(Tone.Level(except, 1000) / 100);
    }

    private static async Task<float[]> Capture(EndpointOpened opened)
    {
        using var endpoint = opened.Endpoint ?? throw new InvalidOperationException(opened.Problem);
        var mono = new List<float>();
        endpoint.Start(
            (samples, _, _) =>
            {
                lock (mono)
                {
                    for (var i = 0; i < samples.Length; i += 2)
                    {
                        mono.Add((samples[i] + samples[i + 1]) / 2);
                    }
                }
            },
            _ => { });
        await Task.Delay(1500, TestContext.Current.CancellationToken);
        lock (mono)
        {
            return [.. mono];
        }
    }

    /// <summary>A separate process playing a tone in a loop (PowerShell with SoundPlayer), so it has its own process tree.</summary>
    private sealed class TonePlayer : IDisposable
    {
        private readonly Process _process;

        private TonePlayer(Process process) => _process = process;

        public int Id => _process.Id;

        public static TonePlayer Start(string folder, int frequency)
        {
            var wav = Path.Combine(folder, $"{frequency}.wav");
            Tone.WriteWav(wav, frequency, seconds: 2, amplitude: 0.2);
            var start = new ProcessStartInfo("powershell.exe", $"-NoProfile -Command \"$p = New-Object Media.SoundPlayer '{wav}'; $p.PlayLooping(); Start-Sleep 30\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            return new TonePlayer(Process.Start(start)!);
        }

        public void Dispose()
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // already gone
            }

            _process.Dispose();
        }
    }
}

/// <summary>Test tones: writing one as a WAV file, and measuring one frequency's level in a recording (Goertzel).</summary>
internal static class Tone
{
    public static void WriteWav(string path, int frequency, double seconds, double amplitude)
    {
        const int rate = 48000;
        var samples = (int)(rate * seconds);
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8);
        writer.Write(36 + (samples * 2));
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(rate);
        writer.Write(rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples * 2);
        for (var i = 0; i < samples; i++)
        {
            writer.Write((short)(amplitude * short.MaxValue * Math.Sin(2 * Math.PI * frequency * i / rate)));
        }
    }

    /// <summary>The amplitude of <paramref name="frequency"/> in 48 kHz <paramref name="samples"/>.</summary>
    public static float Level(float[] samples, double frequency)
    {
        var w = 2 * Math.PI * frequency / 48000;
        var coefficient = 2 * Math.Cos(w);
        double s1 = 0, s2 = 0;
        foreach (var x in samples)
        {
            var s0 = x + (coefficient * s1) - s2;
            s2 = s1;
            s1 = s0;
        }

        var power = (s1 * s1) + (s2 * s2) - (coefficient * s1 * s2);
        return samples.Length == 0 ? 0 : (float)(2 * Math.Sqrt(Math.Max(0, power)) / samples.Length);
    }
}
