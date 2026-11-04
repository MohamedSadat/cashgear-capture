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
        var result = await CashGear.Capture.Tests.MachineTurn.RunAsync(() => Transcriber().TranscribeAsync(samples, new TranscriptionOptions(model, Language: null, UseGpu: false), new SyncProgress(progress.Add), TestContext.Current.CancellationToken));

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
            var result = await CashGear.Capture.Tests.MachineTurn.RunAsync(() => Transcriber().TranscribeAsync(samples, new TranscriptionOptions(Repository.Model(model), "en", gpu), ct: TestContext.Current.CancellationToken));
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
        await CashGear.Capture.Tests.MachineTurn.RunAsync(async () =>
        {
            using var cancel = new CancellationTokenSource();
            var transcribing = Transcriber().TranscribeAsync(samples, new TranscriptionOptions(model, "en", UseGpu: false), new SyncProgress(p =>
            {
                if (p > 0)
                {
                    cancel.Cancel();
                }
            }), cancel.Token);
            await FluentActions.Awaiting(() => transcribing).Should().ThrowAsync<OperationCanceledException>();
            return true;
        });
    }

    [Fact]
    public void Words_spell_their_segment_or_are_left_out()
    {
        static WhisperToken Token(string text, long start, long end) => new() { Text = text, Start = start, End = end };
        var segment = new SegmentData(" Hello world", TimeSpan.Zero, TimeSpan.FromSeconds(1), 0, 1, 1, 0, "en",
            [Token("[_BEG_]", 0, 0), Token(" Hello", 0, 40), Token(" world", 45, 90), Token("[_TT_50]", 90, 90), Token("<|endoftext|>", 90, 90)]);
        WhisperTranscriber.Words(segment, "Hello world").Should().Equal(
            new Core.Transcripts.TranscriptWord(0, 0.4, " Hello"), new Core.Transcripts.TranscriptWord(0.45, 0.9, " world"));

        // Tokens that don't spell the text: no words.
        var other = new SegmentData(" Hello", TimeSpan.Zero, TimeSpan.FromSeconds(1), 0, 1, 1, 0, "en", [Token(" Goodbye", 0, 40)]);
        WhisperTranscriber.Words(other, "Hello").Should().BeNull();
    }

    [Fact]
    public void A_letter_split_between_tokens_goes_to_the_token_it_began_in()
    {
        static WhisperToken Token(string text, long start, long end) => new() { Text = text, Start = start, End = end };

        // An Arabic letter split between two tokens comes out as a replacement character in each.
        var split = new SegmentData(" مرحبا", TimeSpan.Zero, TimeSpan.FromSeconds(1), 0, 1, 1, 0, "ar", [Token(" مر�", 0, 40), Token("�با", 40, 90)]);
        WhisperTranscriber.Words(split, "مرحبا").Should().Equal(
            new Core.Transcripts.TranscriptWord(0, 0.4, " مرح"), new Core.Transcripts.TranscriptWord(0.4, 0.9, "با"));

        // A token that is only part of a letter spells nothing, and two letters lost in one run are found by the next one.
        WhisperTranscriber.Spell([" م", "�", "�", "با"], "محبا").Should().Equal(" م", "ح", "", "با");
        WhisperTranscriber.Spell([" a�", "�", "z"], "abcz").Should().Equal(" ab", "", "cz");
        WhisperTranscriber.Spell([" a�", "�"], "a").Should().BeNull("more letters than the text has");
        WhisperTranscriber.Spell([" a�", "�", "z"], "a").Should().BeNull();
        WhisperTranscriber.Spell([], "").Should().BeNull();
    }

    [Theory]
    [InlineData("[BLANK_AUDIO]", true)]
    [InlineData("(music)", true)]
    [InlineData("[Music]", true)]
    [InlineData("Hello (there)", false)]
    [InlineData("[a] and [b]", false)]
    [InlineData("()", false)]
    public void Notes_about_the_sound_arent_speech(string text, bool note) => WhisperTranscriber.IsSoundNote(text).Should().Be(note);

    [Fact]
    public void Long_sound_is_cut_into_chunks_at_quiet_moments()
    {
        WhisperTranscriber.Chunks([]).Should().BeEmpty();
        WhisperTranscriber.Chunks(new float[16_000]).Should().Equal((0, 16_000));

        // A tone with a short gap every 11 s: each chunk ends in a gap.
        var samples = new float[16_000 * 100];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = i % (16_000 * 11) < 1600 ? 0 : MathF.Sin(i * 0.1f) * 0.3f;
        }

        var chunks = WhisperTranscriber.Chunks(samples);
        chunks[0].From.Should().Be(0);
        chunks[^1].To.Should().Be(samples.Length);
        chunks.Zip(chunks.Skip(1)).Should().OnlyContain(p => p.First.To == p.Second.From);
        chunks.Should().OnlyContain(c => c.To - c.From <= WhisperTranscriber.LongestChunk * 16_000);
        foreach (var (_, to) in chunks.SkipLast(1))
        {
            (to % (16_000 * 11)).Should().BeLessThan(1600, "the chunk ending at {0} ends in a gap", to);
        }
    }

    [Theory]
    [InlineData("ggml-base-q5_1.bin", WhisperAlignmentHeadsPreset.Base)]
    [InlineData("ggml-small-q5_1.bin", WhisperAlignmentHeadsPreset.Small)]
    [InlineData("ggml-medium-q5_0.bin", WhisperAlignmentHeadsPreset.Medium)]
    [InlineData("other.bin", WhisperAlignmentHeadsPreset.None)]
    public void Models_are_aligned_with_their_own_heads(string file, WhisperAlignmentHeadsPreset heads) => WhisperTranscriber.Heads(file).Should().Be(heads);

    private const string Longer = "Today I want to show you how the new editor works. First we open a recording from the tray, and the timeline shows every track of it. The screen is on top, with the webcam next to it, and the sound from the microphone sits below them. If you press the space bar the preview plays, and the playhead moves along the ruler. Now look at the settings on the right. We can choose a background, make the corners round, and add a soft shadow under the picture. Most people leave these alone, because the default look is good enough. Later on we will talk about zooms, which follow the pointer around the screen. Remember that nothing here ever changes the original file, so you can always go back and try something else.";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task All_of_long_speech_comes_back_with_times_near_its_words(bool gpu)
    {
        var model = Repository.Model(WhisperModels.Base);
        var speech = await Speech.SayWithWordsAsync(Longer + " " + Longer);
        speech.Words.Should().HaveCountGreaterThan(250);
        (speech.Samples.Length / 16_000.0).Should().BeGreaterThan(60, "more than Whisper.net hands back with alignment in one run");
        var result = await CashGear.Capture.Tests.MachineTurn.RunAsync(() => Transcriber().TranscribeAsync(speech.Samples, new TranscriptionOptions(model, "en", gpu), ct: TestContext.Current.CancellationToken));

        var heard = result.Segments.Where(s => s.Words is not null).SelectMany(s => s.Words!).ToList();
        InOrder(Words(string.Join(' ', speech.Words.Select(w => w.Text))), Words(string.Join(' ', result.Segments.Select(s => s.Text)))).Should().BeGreaterThan(speech.Words.Count * 95 / 100, "all of it is heard");
        heard.Zip(heard.Skip(1)).Should().OnlyContain(p => p.Second.Start >= p.First.Start - 1e-6, "in time order, across the chunks");

        // A word's aligned end is near the start of the next word said.
        var whole = new List<(double End, string Text)>();
        foreach (var token in heard)
        {
            if (token.Text.StartsWith(' ') || whole.Count == 0)
            {
                whole.Add((token.End, token.Text.Trim()));
            }
            else if (!token.Text.Trim().All(char.IsPunctuation))
            {
                whole[^1] = (token.End, whole[^1].Text + token.Text.Trim());
            }
        }

        var nearest = whole.Zip(whole.Skip(1)).Select(p =>
        {
            var next = speech.Words.Where(w => Words(w.Text).FirstOrDefault() == Words(p.Second.Text).FirstOrDefault()).Select(w => w.Start).OrderBy(t => Math.Abs(t - p.First.End)).FirstOrDefault(p.First.End);
            return Math.Abs(next - p.First.End);
        }).Order().ToList();
        var middle = nearest[nearest.Count / 2];
        TestContext.Current.TestOutputHelper?.WriteLine($"On {result.Runtime}: half of {nearest.Count} word ends within {middle * 1000:0} ms of the next word's start.");
        middle.Should().BeLessThan(0.08, "alignment is far nearer than token times");
    }

    [Fact]
    public async Task Whisper_can_translate_into_english()
    {
        var model = Repository.Model(WhisperModels.Base);
        var samples = await Speech.SayAsync(Said);
        var result = await CashGear.Capture.Tests.MachineTurn.RunAsync(() => Transcriber().TranscribeAsync(samples, new TranscriptionOptions(model, "en", UseGpu: false, Translate: true), ct: TestContext.Current.CancellationToken));
        result.Segments.Should().NotBeEmpty();
        Words(string.Join(' ', result.Segments.Select(s => s.Text))).Should().Contain("fox");
        result.Segments.Should().OnlyContain(s => s.End > s.Start);
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
