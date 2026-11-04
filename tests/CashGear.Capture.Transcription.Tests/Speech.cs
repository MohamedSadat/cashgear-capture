using CashGear.Capture.Transcription;
using Windows.Media.Core;
using Windows.Media.SpeechSynthesis;
using Windows.Storage.Streams;

namespace CashGear.Capture.Transcription.Tests;

/// <summary>A word a voice said, and when it started.</summary>
internal sealed record SaidWord(double Start, string Text);

/// <summary>Speech at 16 kHz mono, and the true time of each word from the voice itself.</summary>
internal sealed record SaidSpeech(float[] Samples, IReadOnlyList<SaidWord> Words);

/// <summary>Speech made by Windows' own voices into memory (nothing is played), as Whisper takes it: mono at 16 kHz.</summary>
internal static class Speech
{
    public static async Task<float[]> SayAsync(string text) => (await SayWithWordsAsync(text)).Samples;

    /// <summary>What the voice said, with when each word started (from the voice's own marks; they have no ends).</summary>
    public static async Task<SaidSpeech> SayWithWordsAsync(string text)
    {
        using var synthesizer = new SpeechSynthesizer();
        Assert.SkipWhen(SpeechSynthesizer.AllVoices.Count == 0, "Windows has no speech voice.");
        synthesizer.Voice = SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase)) ?? SpeechSynthesizer.DefaultVoice;
        synthesizer.Options.IncludeWordBoundaryMetadata = true;
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

        var words = new List<SaidWord>();
        foreach (var track in stream.TimedMetadataTracks.Where(t => t.TimedMetadataKind == TimedMetadataKind.Speech))
        {
            words.AddRange(track.Cues.OfType<SpeechCue>().Select(c => new SaidWord(c.StartTime.TotalSeconds, c.Text)));
        }

        return new SaidSpeech(samples, [.. words.OrderBy(w => w.Start)]);
    }
}
