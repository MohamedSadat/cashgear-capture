using CashGear.Capture.Core.Transcripts;
using Microsoft.Extensions.Logging;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace CashGear.Capture.Transcription;

/// <summary>What to transcribe with.</summary>
/// <param name="ModelPath">The model file (<see cref="WhisperModelStore.EnsureAsync"/>).</param>
/// <param name="Language">A two-letter language (<c>en</c>, <c>ar</c>), or null to detect it.</param>
/// <param name="UseGpu">Use the graphics card (Vulkan) when there is one that works.</param>
public sealed record TranscriptionOptions(string ModelPath, string? Language = null, bool UseGpu = true);

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
/// Whisper through whisper.cpp (Whisper.net), on a background thread. The native runtime is chosen once for the
/// process: the Vulkan one when the graphics card's driver has Vulkan (it also runs on the processor when asked
/// to), else the processor's (with AVX, or without). Words come from whisper's token times; a segment whose pieces
/// don't add up to its text (letters split between tokens) comes without words.
/// </summary>
public sealed partial class WhisperTranscriber(ILogger<WhisperTranscriber> logger) : ITranscriber
{
    public const int SampleRate = 16_000;

    private static readonly object Gate = new();
    private static bool _configured;

    public Task<TranscriptionResult> TranscribeAsync(float[] samples, TranscriptionOptions options, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(options);
        Configure();
        return Task.Factory.StartNew(() => RunAsync(samples, options, progress, ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
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

    private async Task<TranscriptionResult> RunAsync(float[] samples, TranscriptionOptions options, IProgress<double>? progress, CancellationToken ct)
    {
        using var factory = WhisperFactory.FromPath(options.ModelPath, new WhisperFactoryOptions { UseGpu = options.UseGpu });
        var builder = factory.CreateBuilder()
            .WithTokenTimestamps()
            .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 1, 8))
            .WithProgressHandler(percent => progress?.Report(percent / 100.0));
        builder = options.Language is { Length: > 0 } language ? builder.WithLanguage(language) : builder.WithLanguageDetection();
        await using var processor = builder.Build();
        var runtime = options.UseGpu ? RuntimeOptions.LoadedLibrary?.ToString() ?? "Cpu" : "Cpu";
        LogStarted(Path.GetFileName(options.ModelPath), runtime, options.UseGpu, samples.Length / (double)SampleRate);

        var segments = new List<TranscriptSegment>();
        var heard = options.Language ?? "";
        await foreach (var segment in processor.ProcessAsync(samples, ct).ConfigureAwait(false))
        {
            var text = segment.Text.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(segment.Language))
            {
                heard = segment.Language;
            }

            segments.Add(new TranscriptSegment(segment.Start.TotalSeconds, segment.End.TotalSeconds, text, Words(segment, text)));
        }

        progress?.Report(1);
        LogFinished(segments.Count, heard);
        return new TranscriptionResult(segments, heard, runtime);
    }

    /// <summary>The segment's words from its tokens (times in hundredths of a second), or null when they don't spell its text.</summary>
    internal static IReadOnlyList<TranscriptWord>? Words(SegmentData segment, string text)
    {
        var words = (segment.Tokens ?? [])
            .Where(t => !IsSpecial(t.Text))
            .Select(t => new TranscriptWord(t.Start / 100.0, Math.Max(t.Start, t.End) / 100.0, t.Text ?? ""))
            .Where(w => w.Text.Length > 0)
            .ToList();
        var spelled = string.Concat(words.Select(w => w.Text)).Trim();
        return words.Count > 0 && spelled == text && !spelled.Contains('�', StringComparison.Ordinal) ? words : null;
    }

    /// <summary>Whisper's own tokens (<c>[_BEG_]</c>, <c>[_TT_150]</c>, <c>&lt;|endoftext|&gt;</c>), which aren't speech.</summary>
    internal static bool IsSpecial(string? text) =>
        text is null || (text.StartsWith("[_", StringComparison.Ordinal) && text.EndsWith(']')) || (text.StartsWith("<|", StringComparison.Ordinal) && text.EndsWith("|>", StringComparison.Ordinal));

    [LoggerMessage(Level = LogLevel.Information, Message = "Transcribing {Seconds:0.0} s with {Model} on {Runtime} (graphics card asked: {UseGpu})")]
    private partial void LogStarted(string model, string runtime, bool useGpu, double seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Transcribed {Count} segments ({Language})")]
    private partial void LogFinished(int count, string language);
}
