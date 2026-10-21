using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CashGear.Capture.Transcription;
using Microsoft.Extensions.Logging.Abstractions;
using Whisper.net;
using Windows.Media.SpeechSynthesis;
using Windows.Storage.Streams;

namespace CashGear.Capture.Transcription.Tests;

/// <summary>Where the repository's files are: the models <c>tools\get-whisper-models.ps1</c> downloads.</summary>
internal static class Repository
{
    public static string Root { get; } = Find();

    public static string Models => Path.Combine(Root, "tools", "whisper");

    /// <summary>The model's file in tools\whisper; skips the test without it.</summary>
    public static string Model(WhisperModel model)
    {
        var path = Path.Combine(Models, model.File);
        Assert.SkipUnless(File.Exists(path) && new FileInfo(path).Length == model.Bytes, $"{model.File} isn't in tools\\whisper (run tools\\get-whisper-models.ps1).");
        return path;
    }

    private static string Find()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CashGear.Capture.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository wasn't found above the tests.");
    }
}

/// <summary>Speech made by Windows' own voices into memory (nothing is played), as Whisper takes it: mono at 16 kHz.</summary>
internal static class Speech
{
    public static async Task<float[]> SayAsync(string text)
    {
        using var synthesizer = new SpeechSynthesizer();
        Assert.SkipWhen(SpeechSynthesizer.AllVoices.Count == 0, "Windows has no speech voice.");
        synthesizer.Voice = SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase)) ?? SpeechSynthesizer.DefaultVoice;
        using var stream = await synthesizer.SynthesizeTextToStreamAsync(text);
        var bytes = new byte[stream.Size];
        using (var reader = new DataReader(stream.GetInputStreamAt(0)))
        {
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(bytes);
        }

        // A RIFF WAVE of 16-bit samples: find "data", average the channels, resample linearly to 16 kHz.
        var channels = BitConverter.ToInt16(bytes, 22);
        var rate = BitConverter.ToInt32(bytes, 24);
        var at = 12;
        while (System.Text.Encoding.ASCII.GetString(bytes, at, 4) != "data")
        {
            at += 8 + BitConverter.ToInt32(bytes, at + 4);
        }

        var count = BitConverter.ToInt32(bytes, at + 4) / 2 / channels;
        var source = new float[count];
        for (var i = 0; i < count; i++)
        {
            for (var c = 0; c < channels; c++)
            {
                source[i] += BitConverter.ToInt16(bytes, at + 8 + (((i * channels) + c) * 2)) / 32768f / channels;
            }
        }

        var length = (int)((long)count * WhisperTranscriber.SampleRate / rate);
        var samples = new float[length];
        for (var i = 0; i < length; i++)
        {
            var position = i * (double)rate / WhisperTranscriber.SampleRate;
            var k = (int)position;
            var f = (float)(position - k);
            samples[i] = (source[Math.Min(k, count - 1)] * (1 - f)) + (source[Math.Min(k + 1, count - 1)] * f);
        }

        return samples;
    }
}

public sealed class TranscriptionTests
{
    private const string Said = "The quick brown fox jumps over the lazy dog. Screen recordings with captions are easy to follow.";

    private static string[] Words(string text) => [.. Regex.Split(text.ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(w => w.Length > 0)];

    /// <summary>How many of the words said are heard, in their order (the longest common run).</summary>
    private static int InOrder(string[] said, string[] heard)
    {
        var table = new int[said.Length + 1, heard.Length + 1];
        for (var i = 1; i <= said.Length; i++)
        {
            for (var j = 1; j <= heard.Length; j++)
            {
                table[i, j] = said[i - 1] == heard[j - 1] ? table[i - 1, j - 1] + 1 : Math.Max(table[i - 1, j], table[i, j - 1]);
            }
        }

        return table[said.Length, heard.Length];
    }

    private static WhisperTranscriber Transcriber() => new(NullLogger<WhisperTranscriber>.Instance);

    [Fact]
    public async Task English_speech_is_heard_word_for_word_with_the_words_times()
    {
        var model = Repository.Model(WhisperModels.Base);
        var samples = await Speech.SayAsync(Said);
        var progress = new List<double>();
        var result = await Transcriber().TranscribeAsync(samples, new TranscriptionOptions(model, Language: null, UseGpu: false), new SyncProgress(progress.Add), TestContext.Current.CancellationToken);

        result.Language.Should().Be("en", "detected");
        result.Runtime.Should().BeOneOf("Vulkan", "Cpu", "CpuNoAvx");
        var said = Words(Said);
        var heard = Words(string.Join(' ', result.Segments.Select(s => s.Text)));
        InOrder(said, heard).Should().BeGreaterThanOrEqualTo(said.Length - 1, $"heard: {string.Join(' ', heard)}");
        progress.Should().NotBeEmpty().And.EndWith(1);

        var seconds = samples.Length / (double)WhisperTranscriber.SampleRate;
        result.Segments.Should().OnlyContain(s => s.Start >= 0 && s.End <= seconds + 0.5 && s.End > s.Start);
        result.Segments.Zip(result.Segments.Skip(1)).Should().OnlyContain(p => p.Second.Start >= p.First.Start);
        var words = result.Segments.Where(s => s.Words is not null).SelectMany(s => s.Words!).ToList();
        words.Should().NotBeEmpty("English tokens spell their segments");
        words.Zip(words.Skip(1)).Should().OnlyContain(p => p.Second.Start >= p.First.Start - 0.05, "in time order");
        words.First(w => w.Text.Trim() == "fox").Start.Should().BeLessThan(words.First(w => w.Text.Trim().StartsWith("caption", StringComparison.Ordinal)).Start);
    }

    [Fact]
    public async Task The_graphics_card_and_the_small_model_with_a_set_language_hear_the_same()
    {
        var samples = await Speech.SayAsync(Said);
        var said = Words(Said);
        foreach (var (model, gpu) in new[] { (WhisperModels.Base, true), (WhisperModels.Small, true) })
        {
            var result = await Transcriber().TranscribeAsync(samples, new TranscriptionOptions(Repository.Model(model), "en", gpu), ct: TestContext.Current.CancellationToken);
            var heard = Words(string.Join(' ', result.Segments.Select(s => s.Text)));
            TestContext.Current.TestOutputHelper?.WriteLine($"{model.Name} on {result.Runtime}: {string.Join(' ', heard)}");
            result.Language.Should().Be("en");
            InOrder(said, heard).Should().BeGreaterThanOrEqualTo(said.Length - 1);
        }
    }

    [Fact]
    public async Task Cancelling_stops_a_transcription()
    {
        var model = Repository.Model(WhisperModels.Base);
        var samples = await Speech.SayAsync(string.Join(' ', Enumerable.Repeat(Said, 6)));
        using var cancel = new CancellationTokenSource();
        var transcribing = Transcriber().TranscribeAsync(samples, new TranscriptionOptions(model, "en", UseGpu: false), new SyncProgress(p =>
        {
            if (p > 0)
            {
                cancel.Cancel();
            }
        }), cancel.Token);
        await FluentActions.Awaiting(() => transcribing).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Words_spell_their_segment_or_are_left_out()
    {
        static WhisperToken Token(string text, long start, long end) => new() { Text = text, Start = start, End = end };
        var segment = new SegmentData(" Hello world", TimeSpan.Zero, TimeSpan.FromSeconds(1), 0, 1, 1, 0, "en",
            [Token("[_BEG_]", 0, 0), Token(" Hello", 0, 40), Token(" world", 45, 90), Token("[_TT_50]", 90, 90), Token("<|endoftext|>", 90, 90)]);
        WhisperTranscriber.Words(segment, "Hello world").Should().Equal(
            new Core.Transcripts.TranscriptWord(0, 0.4, " Hello"), new Core.Transcripts.TranscriptWord(0.45, 0.9, " world"));

        // An Arabic letter split between two tokens comes out as replacement characters in each: no words then.
        var split = new SegmentData(" مرحبا", TimeSpan.Zero, TimeSpan.FromSeconds(1), 0, 1, 1, 0, "ar", [Token(" مر�", 0, 40), Token("�با", 40, 90)]);
        WhisperTranscriber.Words(split, "مرحبا").Should().BeNull();
    }

    [Fact]
    public void The_model_list_matches_the_download_script()
    {
        var script = File.ReadAllText(Path.Combine(Repository.Root, "tools", "get-whisper-models.ps1"));
        foreach (var model in WhisperModels.All)
        {
            var line = script.Split('\n').Single(l => l.Contains($"\"{model.File}\"", StringComparison.Ordinal));
            line.Should().Contain(model.Bytes.ToString(CultureInfo.InvariantCulture)).And.Contain(model.Sha256);
            line.TrimStart().Should().StartWith(model.Id);
        }

        WhisperModels.Find("SMALL").Should().Be(WhisperModels.Small);
        WhisperModels.Small.ToString().Should().Be("Small (190 MB)");
    }

    /// <summary>A local HTTP server that sends <paramref name="body"/> for any request, slowly when asked.</summary>
    private sealed class Server : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _serving;

        public Server(byte[] body, int delayMs = 0)
        {
            var port = FreePort();
            Uri = new Uri($"http://127.0.0.1:{port}/models/");
            _listener.Prefixes.Add(Uri.ToString());
            _listener.Start();
            _serving = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync();
                    }
                    catch (Exception e) when (e is HttpListenerException or ObjectDisposedException)
                    {
                        return;
                    }

                    Requests++;
                    try
                    {
                        context.Response.ContentLength64 = body.Length;
                        for (var at = 0; at < body.Length; at += 4096)
                        {
                            await context.Response.OutputStream.WriteAsync(body.AsMemory(at, Math.Min(4096, body.Length - at)));
                            if (delayMs > 0)
                            {
                                await Task.Delay(delayMs);
                            }
                        }

                        context.Response.Close();
                    }
                    catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException)
                    {
                        // the client went away
                    }
                }
            });
        }

        public Uri Uri { get; }

        public int Requests { get; private set; }

        public void Dispose()
        {
            _listener.Close();
            _serving.Wait(TimeSpan.FromSeconds(5));
        }

        private static int FreePort()
        {
            using var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            return ((IPEndPoint)socket.LocalEndpoint).Port;
        }
    }

    [Fact]
    public async Task Models_are_downloaded_once_checked_and_never_left_half_done()
    {
        var folder = Directory.CreateTempSubdirectory("cashgear-models-").FullName;
        try
        {
            var body = RandomNumberGenerator.GetBytes(300_000);
            var model = new WhisperModel("test", "Test", "ggml-test.bin", body.Length, Convert.ToHexStringLower(SHA256.HashData(body)), "");
            using var http = new HttpClient();
            using var server = new Server(body);
            var store = new WhisperModelStore(folder, http, NullLogger<WhisperModelStore>.Instance, server.Uri);
            store.Has(model).Should().BeFalse();
            var progress = new List<double>();
            var path = await store.EnsureAsync(model, new SyncProgress(progress.Add), TestContext.Current.CancellationToken);
            path.Should().Be(Path.Combine(folder, "ggml-test.bin"));
            File.ReadAllBytes(path).Should().Equal(body);
            progress.Should().EndWith(1);
            (await store.EnsureAsync(model, ct: TestContext.Current.CancellationToken)).Should().Be(path);
            server.Requests.Should().Be(1, "it is there now");

            // A download that isn't the model: refused, nothing kept.
            var wrong = model with { File = "ggml-wrong.bin", Sha256 = new string('0', 64) };
            await FluentActions.Awaiting(() => store.EnsureAsync(wrong, ct: TestContext.Current.CancellationToken)).Should().ThrowAsync<InvalidDataException>();
            Directory.GetFiles(folder).Should().Equal(path);

            // Cancelled half way: nothing kept.
            using var slow = new Server(body, delayMs: 20);
            var slowStore = new WhisperModelStore(folder, http, NullLogger<WhisperModelStore>.Instance, slow.Uri);
            using var cancel = new CancellationTokenSource();
            var other = model with { File = "ggml-other.bin" };
            var downloading = slowStore.EnsureAsync(other, new SyncProgress(p =>
            {
                if (p > 0.2)
                {
                    cancel.Cancel();
                }
            }), cancel.Token);
            await FluentActions.Awaiting(() => downloading).Should().ThrowAsync<OperationCanceledException>();
            Directory.GetFiles(folder).Should().Equal(path);

            // Found in another folder (tools\whisper, a portable copy) without downloading.
            var elsewhere = new WhisperModelStore(Path.Combine(folder, "none"), http, NullLogger<WhisperModelStore>.Instance, server.Uri, [folder]);
            elsewhere.PathOf(model).Should().Be(path);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>Progress reported on the reporting thread (not posted), so a test sees every report.</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
