using System.ComponentModel;
using System.Text;
using CashGear.Capture.Core.Transcripts;
using Microsoft.Extensions.Logging;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace CashGear.Capture.Transcription;

/// <summary>What to transcribe with.</summary>
/// <param name="ModelPath">The model file (<see cref="WhisperModelStore.EnsureAsync"/>).</param>
/// <param name="Language">A two-letter language (<c>en</c>, <c>ar</c>), or null to detect it.</param>
/// <param name="UseGpu">Use the graphics card (Vulkan) when there is one that works.</param>
/// <param name="KeepFillers">Ask Whisper to write down "um" and "uh", through a prompt that has them (<see cref="FillerPrompts"/>).</param>
/// <param name="Translate">Into English, whatever the language heard: segments without word times.</param>
public sealed record TranscriptionOptions(string ModelPath, string? Language = null, bool UseGpu = true, bool KeepFillers = false, bool Translate = false);

/// <param name="Language">The language heard (two letters).</param>
/// <param name="Runtime">Where it ran: "Vulkan" (on the graphics card, when its driver has it) or "Cpu".</param>
public sealed record TranscriptionResult(IReadOnlyList<TranscriptSegment> Segments, string Language, string Runtime);

/// <summary>Speech to text.</summary>
public interface ITranscriber
{
    /// <summary>
    /// The speech in <paramref name="samples"/> (mono, <see cref="WhisperTranscriber.SampleRate"/> Hz) as segments
    /// with their words' times. Progress is the part of the sound done, 0 to 1.
    /// </summary>
    Task<TranscriptionResult> TranscribeAsync(float[] samples, TranscriptionOptions options, IProgress<double>? progress = null, CancellationToken ct = default);
}

/// <summary>
/// What Whisper reads before listening, for the languages with filler words: text in the style to write, fillers
/// and all. Whisper tends to leave disfluencies out of its text otherwise (measured in docs/TESTING.md).
/// </summary>
[Localizable(false)]
public static class FillerPrompts
{
    /// <summary>The prompt for a two-letter language, or null when there is none.</summary>
    public static string? For(string? language) => language switch
    {
        "en" => "Um, uh, er, ah, hmm. So, um, I think, uh, we should, er, start here. Hmm, ah, right, um, okay.",
        "ar" => "اممم، إممم، آه. يعني، اممم، أعتقد، آه، أننا، إممم، نبدأ من هنا. آه، اممم، حسنا.",
        _ => null,
    };
}

/// <summary>
/// Whisper through whisper.cpp (Whisper.net), on a background thread. The native runtime is chosen once for the
/// process: the Vulkan one when the graphics card's driver has Vulkan (it also runs on the processor when asked
/// to), else the processor's (with AVX, or without).
/// <para>
/// Word times come from whisper.cpp's alignment heads (DTW), far nearer than its token times (TESTING.md). Whisper.net
/// 1.9.1 hands back only the first thirty-second window of a run with them, so the sound is given to it in chunks of at
/// most <see cref="LongestChunk"/> seconds, each ending at a quiet moment. Tokens that split a letter between them
/// (Arabic, often) are put together again from the segment's text; a segment whose tokens can't spell its text comes
/// without words.
/// </para>
/// </summary>
public sealed partial class WhisperTranscriber(ILogger<WhisperTranscriber> logger) : ITranscriber
{
    public const int SampleRate = 16_000;

    /// <summary>The longest stretch of sound given to Whisper at once, in seconds.</summary>
    public const int LongestChunk = 27;

    /// <summary>A chunk ends at the quietest moment of its last seconds.</summary>
    private const int ChunkEndSearch = 5;

    /// <summary>What Whisper shows for bytes that aren't a whole letter.</summary>
    private const char Broken = '�';

    private static readonly object Gate = new();
    private static bool _configured;

    public Task<TranscriptionResult> TranscribeAsync(float[] samples, TranscriptionOptions options, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(options);
        Configure();
        return Task.Factory.StartNew(() => RunAsync(samples, options, progress, ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    /// <summary>The sound cut into chunks of at most <see cref="LongestChunk"/> seconds, each ending at the quietest 20 ms of its last seconds.</summary>
    internal static List<(int From, int To)> Chunks(ReadOnlySpan<float> samples)
    {
        var chunks = new List<(int From, int To)>();
        var longest = LongestChunk * SampleRate;
        var search = ChunkEndSearch * SampleRate;
        const int window = SampleRate / 50;
        var from = 0;
        while (from < samples.Length)
        {
            var to = from + longest;
            if (to >= samples.Length)
            {
                chunks.Add((from, samples.Length));
                break;
            }

            var best = to;
            var lowest = double.MaxValue;
            for (var at = to - search; at + window <= to; at += window / 2)
            {
                double sum = 0;
                foreach (var sample in samples.Slice(at, window))
                {
                    sum += sample * sample;
                }

                if (sum < lowest)
                {
                    lowest = sum;
                    best = at + (window / 2);
                }
            }

            chunks.Add((from, best));
            from = best;
        }

        return chunks;
    }

    /// <summary>The runtimes tried, in order: before anything is transcribed, once.</summary>
    private static void Configure()
    {
        lock (Gate)
        {
            if (!_configured)
            {
                RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx];
                _configured = true;
            }
        }
    }

    /// <summary>The alignment heads of a model, from its file's name.</summary>
    internal static WhisperAlignmentHeadsPreset Heads(string modelPath)
    {
        var name = Path.GetFileName(modelPath);
        return name.Contains("medium", StringComparison.OrdinalIgnoreCase) ? WhisperAlignmentHeadsPreset.Medium
            : name.Contains("small", StringComparison.OrdinalIgnoreCase) ? WhisperAlignmentHeadsPreset.Small
            : name.Contains("base", StringComparison.OrdinalIgnoreCase) ? WhisperAlignmentHeadsPreset.Base
            : name.Contains("tiny", StringComparison.OrdinalIgnoreCase) ? WhisperAlignmentHeadsPreset.Tiny
            : WhisperAlignmentHeadsPreset.None;
    }

    /// <summary>How much earlier than the true end of a word its alignment time is, in seconds (measured against a voice's own word marks: TESTING.md).</summary>
    internal static double Lag(WhisperAlignmentHeadsPreset heads) => heads switch
    {
        WhisperAlignmentHeadsPreset.None => 0,
        WhisperAlignmentHeadsPreset.Base or WhisperAlignmentHeadsPreset.Tiny => 0.02,
        _ => 0.05,
    };

    private async Task<TranscriptionResult> RunAsync(float[] samples, TranscriptionOptions options, IProgress<double>? progress, CancellationToken ct)
    {
        var heads = options.Translate ? WhisperAlignmentHeadsPreset.None : Heads(options.ModelPath);
        var aligned = heads != WhisperAlignmentHeadsPreset.None;
        using var factory = WhisperFactory.FromPath(options.ModelPath, new WhisperFactoryOptions { UseGpu = options.UseGpu, UseDtwTimeStamps = aligned, HeadsPreset = heads });
        var chunks = aligned ? Chunks(samples) : [(0, samples.Length)];
        LogStarted(Path.GetFileName(options.ModelPath), options.UseGpu, samples.Length / (double)SampleRate);

        // The progress of the chunk being heard, as a part of all the sound.
        var chunkFrom = 0;
        var chunkLength = samples.Length;
        void Report(int percent)
        {
            if (samples.Length > 0)
            {
                progress?.Report(Math.Min(1, (chunkFrom + (chunkLength * percent / 100.0)) / samples.Length));
            }
        }

        var language = options.Language is { Length: > 0 } set ? set : Detect(factory, samples, chunks);
        var builder = factory.CreateBuilder()
            .WithTokenTimestamps()
            .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 1, 8))
            .WithProgressHandler(Report);
        builder = language is { Length: > 0 } ? builder.WithLanguage(language) : builder.WithLanguageDetection();
        if (options.Translate)
        {
            builder = builder.WithTranslate();
        }

        if (options.KeepFillers && FillerPrompts.For(language) is { } prompt)
        {
            builder = builder.WithPrompt(prompt);
        }

        await using var processor = builder.Build();
        var runtime = options.UseGpu ? RuntimeOptions.LoadedLibrary?.ToString() ?? "Cpu" : "Cpu";
        var segments = new List<TranscriptSegment>();
        var heard = language ?? "";
        foreach (var (from, to) in chunks)
        {
            ct.ThrowIfCancellationRequested();
            chunkFrom = from;
            chunkLength = to - from;
            var offset = from / (double)SampleRate;
            await foreach (var segment in processor.ProcessAsync(samples[from..to], ct).ConfigureAwait(false))
            {
                var text = segment.Text.Trim();
                if (text.Length == 0 || IsSoundNote(text))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(segment.Language))
                {
                    heard = segment.Language;
                }

                segments.Add(new TranscriptSegment(segment.Start.TotalSeconds + offset, segment.End.TotalSeconds + offset, text, Words(segment, text, offset, aligned, Lag(heads))));
            }
        }

        progress?.Report(1);
        LogFinished(segments.Count, heard, runtime);
        return new TranscriptionResult(segments, heard, runtime);
    }

    /// <summary>The language of the loudest of the first chunks (null: left to each segment).</summary>
    private static string? Detect(WhisperFactory factory, float[] samples, List<(int From, int To)> chunks)
    {
        if (samples.Length == 0)
        {
            return null;
        }

        var loudest = chunks.Take(4).MaxBy(c =>
        {
            double sum = 0;
            for (var i = c.From; i < c.To; i++)
            {
                sum += samples[i] * samples[i];
            }

            return sum / Math.Max(1, c.To - c.From);
        });
        using var detector = factory.CreateBuilder().WithLanguageDetection().Build();
        return detector.DetectLanguage(samples[loudest.From..loudest.To]);
    }

    /// <summary>Whisper's note about the sound rather than speech: <c>[BLANK_AUDIO]</c>, <c>(music)</c>.</summary>
    internal static bool IsSoundNote(string text) =>
        text.Length > 2 && ((text[0] == '[' && text[^1] == ']') || (text[0] == '(' && text[^1] == ')')) && text.IndexOfAny(['[', ']', '(', ')'], 1, text.Length - 2) < 0;

    /// <summary>
    /// The segment's words from its tokens, each with the part of the segment's text it spells, or null when they
    /// can't spell it. With <paramref name="aligned"/> a token ends at its alignment time (plus <paramref name="lag"/>) and starts
    /// where the one before ended (its own start for the first); punctuation takes no time. Times are moved by
    /// <paramref name="offset"/> seconds.
    /// </summary>
    internal static IReadOnlyList<TranscriptWord>? Words(SegmentData segment, string text, double offset = 0, bool aligned = false, double lag = 0)
    {
        var tokens = (segment.Tokens ?? []).Where(t => !IsSpecial(t.Text) && !string.IsNullOrEmpty(t.Text)).ToList();
        var spelled = Spell([.. tokens.Select(t => t.Text!)], text);
        if (spelled is null)
        {
            return null;
        }

        var words = new List<TranscriptWord>(tokens.Count);
        var end = -1.0;
        for (var i = 0; i < tokens.Count; i++)
        {
            if (spelled[i].Length == 0)
            {
                continue;
            }

            var token = tokens[i];
            var from = token.Start / 100.0;
            var to = Math.Max(token.Start, token.End) / 100.0;
            if (aligned)
            {
                var punctuation = spelled[i].Trim().All(char.IsPunctuation);
                var at = token.DtwTimestamp >= 0 ? (token.DtwTimestamp / 100.0) + lag : to;
                from = end >= 0 ? end : Math.Min(from, at);
                to = punctuation && end >= 0 ? end : Math.Max(from, at);
                end = to;
            }

            words.Add(new TranscriptWord(Math.Round(from + offset, 3), Math.Round(to + offset, 3), spelled[i]));
        }

        return words.Count > 0 ? words : null;
    }

    /// <summary>
    /// What each token spells of <paramref name="text"/> (a token's leading space kept). A token holding part of a
    /// letter shows it as a replacement character: the letter goes to the token it began in, and the replacement
    /// characters after it count for nothing. Null when the tokens don't spell the text.
    /// </summary>
    internal static IReadOnlyList<string>? Spell(IReadOnlyList<string> tokens, string text)
    {
        var joined = string.Concat(tokens);
        if (!joined.Contains(Broken, StringComparison.Ordinal))
        {
            return tokens.Count > 0 && joined.Trim() == text ? tokens : null;
        }

        var spelled = new List<string>(tokens.Count);
        var at = 0;
        var inBroken = false;
        var started = false;
        foreach (var token in tokens)
        {
            var piece = new StringBuilder();
            foreach (var letter in token)
            {
                if (letter == Broken)
                {
                    if (!inBroken)
                    {
                        if (at >= text.Length)
                        {
                            return null;
                        }

                        piece.Append(text[at++]);
                        inBroken = true;
                        started = true;
                    }

                    continue;
                }

                if (!started && letter == ' ')
                {
                    piece.Append(letter); // the segment's text is trimmed; its first word's space isn't in it
                    started = true;
                    continue;
                }

                started = true;
                if (at >= text.Length || text[at] != letter)
                {
                    // More than one letter hidden in a run of replacement characters: the next letter says how many.
                    var next = inBroken && at < text.Length ? text.IndexOf(letter, at) : -1;
                    if (next < 0 || next - at > 4)
                    {
                        return null;
                    }

                    piece.Append(text, at, next - at);
                    at = next;
                }

                inBroken = false;
                piece.Append(letter);
                at++;
            }

            spelled.Add(piece.ToString());
        }

        return at == text.Length ? spelled : null;
    }

    /// <summary>Whisper's own tokens (<c>[_BEG_]</c>, <c>[_TT_150]</c>, <c>&lt;|endoftext|&gt;</c>), which aren't speech.</summary>
    internal static bool IsSpecial(string? text) =>
        text is null || (text.StartsWith("[_", StringComparison.Ordinal) && text.EndsWith(']')) || (text.StartsWith("<|", StringComparison.Ordinal) && text.EndsWith("|>", StringComparison.Ordinal));

    [LoggerMessage(Level = LogLevel.Information, Message = "Transcribing {Seconds:0.0} s with {Model} (graphics card asked: {UseGpu})")]
    private partial void LogStarted(string model, bool useGpu, double seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Transcribed {Count} segments ({Language}) on {Runtime}")]
    private partial void LogFinished(int count, string language, string runtime);
}
