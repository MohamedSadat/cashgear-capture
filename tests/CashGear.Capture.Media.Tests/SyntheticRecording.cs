using System.IO;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Media;
using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.Media.Tests;

/// <summary>
/// Writes a recording the way the recorder will, without a screen or sound card: dark frames with white flashes at
/// chosen frames, and silence with short 1 kHz beeps at chosen samples, interleaved in time order through a
/// <see cref="MediaWriter"/>. The encoder can be switched mid-way to make a second segment.
/// </summary>
internal sealed unsafe class SyntheticRecording
{
    public required string Directory { get; init; }

    public double Seconds { get; init; } = 4;

    public VideoFormat Format { get; init; } = new(640, 360, 30);

    public EncoderCandidate Encoder { get; init; } = EncoderPresets.WindowsSoftwareH264;

    /// <summary>The encoder from <see cref="SwitchAtFrame"/> on (a new segment), if any.</summary>
    public EncoderCandidate? SwitchTo { get; init; }

    public int SwitchAtFrame { get; init; }

    public IReadOnlyList<int> FlashFrames { get; init; } = [];

    public IReadOnlyList<long> BeepSamples { get; init; } = [];

    public IReadOnlyList<string> Tracks { get; init; } = ["Mixed"];

    public const int BeepSamplesLong = 2400; // 50 ms
    public const float BeepLevel = 0.25f;

    public IReadOnlyList<SegmentFile> Write()
    {
        TestMedia.Require();
        System.IO.Directory.CreateDirectory(Directory);
        var audio = Tracks.Select((_, i) => AudioEncoder.Open(stream: i + 1)).ToList();
        var writer = new MediaWriter(
            n => Path.Combine(Directory, $"segment-{n:000}.mp4"),
            audio.Select((a, i) => StreamSetup.From(a, Tracks[i], i == 0)).ToList(),
            NullLogger.Instance);
        var video = Open(Encoder);
        var begun = false;
        var packets = new List<EncodedPacket>();
        var samples = new float[2 * 1024];
        long audioPts = 0;
        try
        {
            var frames = (int)Math.Round(Seconds * Format.Fps);
            for (var n = 0; n < frames; n++)
            {
                if (SwitchTo is { } next && n == SwitchAtFrame)
                {
                    video.Flush(packets);
                    Send(packets, writer);
                    video.Dispose();
                    video = Open(next);
                    begun = false;
                }

                Fill(video.NextFrame(), FlashFrames.Contains(n));
                video.Encode(n, packets);
                if (!begun && packets.Count > 0)
                {
                    writer.BeginSegment(StreamSetup.From(video), packets[0].Seconds);
                    begun = true;
                }

                Send(packets, writer);

                var due = (long)(n + 1) * AudioEncoder.SampleRate / Format.Fps;
                while (audioPts + 1024 <= due)
                {
                    Tone(samples, audioPts);
                    foreach (var track in audio)
                    {
                        track.Encode(samples, audioPts, packets);
                    }

                    Send(packets, writer);
                    audioPts += 1024;
                }
            }

            video.Flush(packets);
            if (!begun && packets.Count > 0)
            {
                writer.BeginSegment(StreamSetup.From(video), packets[0].Seconds);
            }

            foreach (var track in audio)
            {
                track.Flush(packets);
            }

            Send(packets, writer);
            writer.Complete(TimeSpan.FromSeconds(30)).Should().BeTrue();
            writer.Failure.Should().BeNull();
            return writer.Segments;
        }
        finally
        {
            video.Dispose();
            writer.Dispose();
            audio.ForEach(a => a.Dispose());
        }
    }

    private VideoEncoder Open(EncoderCandidate candidate) =>
        VideoEncoder.Open(candidate, Format, EncoderPresets.Options(candidate, QualityPreset.Balanced, Format.Width, Format.Height, Format.Fps));

    private static void Send(List<EncodedPacket> packets, MediaWriter writer)
    {
        packets.ForEach(writer.Write);
        packets.Clear();
    }

    private void Tone(float[] samples, long start)
    {
        Array.Clear(samples);
        foreach (var beep in BeepSamples)
        {
            for (var i = 0; i < 1024; i++)
            {
                var t = start + i - beep;
                if (t >= 0 && t < BeepSamplesLong)
                {
                    var value = MathF.Sin(2 * MathF.PI * 1000 * t / AudioEncoder.SampleRate) * BeepLevel;
                    samples[2 * i] = value;
                    samples[(2 * i) + 1] = value;
                }
            }
        }
    }

    private static void Fill(AVFrame* frame, bool flash)
    {
        TestPattern.Fill(frame, 0);
        for (var y = 0; y < frame->height; y++)
        {
            var row = frame->data[0] + (y * frame->linesize[0]);
            for (var x = 0; x < frame->width; x++)
            {
                row[x] = (byte)(flash ? 220 + (row[x] & 7) : 30 + (row[x] & 7));
            }
        }
    }
}
