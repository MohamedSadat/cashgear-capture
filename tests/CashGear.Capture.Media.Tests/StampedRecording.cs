using System.IO;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Media;
using FFmpeg.AutoGen.Abstractions;

namespace CashGear.Capture.Media.Tests;

/// <summary>
/// Writes an MP4 shaped like a saved recording (H.264, AAC sound tracks, optionally a second "Webcam" video) through
/// the public export API, with each picture's number stamped into its top row as <see cref="Bits"/> white or black
/// blocks, the rest of the picture dark grey or (on flash frames) light, and 1 kHz beeps on chosen samples. Reading
/// the stamps back tells exactly which source picture a decoder, an edit or an export gave.
/// </summary>
internal sealed unsafe class StampedRecording
{
    public const int Bits = 16;
    public const int Block = 16;
    public const int BeepSamplesLong = 2400; // 50 ms

    public required string Path { get; init; }

    public double Seconds { get; init; } = 4;

    public VideoFormat Format { get; init; } = new(320, 180, 30);

    public EncoderCandidate Encoder { get; init; } = EncoderPresets.WindowsSoftwareH264;

    /// <summary>Whether a picture is light (a flash).</summary>
    public Func<int, bool> Flash { get; init; } = _ => false;

    /// <summary>The samples (48 kHz) where beeps start.</summary>
    public IReadOnlyList<long> BeepSamples { get; init; } = [];

    /// <summary>A beep every this many samples from <see cref="BeepEveryFrom"/> (0: none), for long recordings.</summary>
    public long BeepEvery { get; init; }

    public long BeepEveryFrom { get; init; }

    public IReadOnlyList<string> Tracks { get; init; } = ["Mixed"];

    /// <summary>Tracks (by index) that get a steady tone of their own instead of the beeps: 440 Hz × (index + 1).</summary>
    public IReadOnlySet<int> ToneTracks { get; init; } = new HashSet<int>();

    public float BeepLevel { get; init; } = 0.25f;

    /// <summary>A second, stamped video titled "Webcam" (not the default stream), at its own size and rate.</summary>
    public VideoFormat? Webcam { get; init; }

    /// <summary>
    /// Draws picture n itself, as top-down BGRA rows of the format's width, instead of the stamp (pictures like a
    /// real screen's, for tests about how they compress).
    /// </summary>
    public Action<int, byte[]>? Picture { get; init; }

    /// <summary>The picture number stamped into a decoded top-down BGRA picture of width <paramref name="width"/> (scale 1).</summary>
    public static int ReadStamp(ReadOnlySpan<byte> bgra, int width, double scale = 1)
    {
        var value = 0;
        var y = (int)(Block / 2 * scale);
        for (var bit = 0; bit < Bits; bit++)
        {
            var x = (int)(((bit * Block) + (Block / 2)) * scale);
            var green = bgra[(((y * width) + x) * 4) + 1];
            value = (value << 1) | (green > 128 ? 1 : 0);
        }

        return value;
    }

    /// <summary>The mean green of the picture below the stamp row: dark (about 40) or light (about 200) on flashes.</summary>
    public static double Brightness(ReadOnlySpan<byte> bgra, int width, int height)
    {
        long sum = 0;
        var count = 0;
        for (var y = Block * 2; y < height; y += 4)
        {
            for (var x = 0; x < width; x += 4)
            {
                sum += bgra[(((y * width) + x) * 4) + 1];
                count++;
            }
        }

        return (double)sum / count;
    }

    public void Write()
    {
        TestMedia.Require();
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var audio = Tracks.Select((_, i) => AudioEncoder.Open(stream: i + 1)).ToList();
        var webcamStream = Tracks.Count + 1;
        var video = Open(Format, 0);
        var webcam = Webcam is { } w ? Open(w, webcamStream) : null;
        using var writer = new ExportWriter(Path, ExportContainer.Mp4);
        writer.Add(video);
        for (var i = 0; i < audio.Count; i++)
        {
            writer.Add(audio[i], Tracks[i]);
        }

        if (webcam is not null)
        {
            writer.Add(webcam, "Webcam");
        }

        var packets = new List<EncodedPacket>();
        var samples = new float[2 * 1024];
        using var converter = Picture is null ? null : new FrameConverter(Format.Width, Format.Height, video.PixelFormat);
        var picture = Picture is null ? null : new byte[Format.Width * Format.Height * 4];
        long audioPts = 0;
        var webcamFrame = 0;
        try
        {
            var frames = (int)Math.Round(Seconds * Format.Fps);
            for (var n = 0; n < frames; n++)
            {
                if (Picture is not null)
                {
                    Picture(n, picture!);
                    converter!.Convert(picture, Format.Width * 4, video.NextFrame());
                }
                else
                {
                    Fill(video.NextFrame(), n, Flash(n));
                }

                video.Encode(n, packets);
                Send(packets, writer);

                // The webcam's pictures up to this time.
                while (webcam is not null && webcamFrame * Format.Fps <= n * webcam.Format.Fps)
                {
                    Fill(webcam.NextFrame(), webcamFrame, false);
                    webcam.Encode(webcamFrame++, packets);
                    Send(packets, writer);
                }

                var due = (long)(n + 1) * AudioEncoder.SampleRate / Format.Fps;
                while (audioPts + 1024 <= due)
                {
                    for (var t = 0; t < audio.Count; t++)
                    {
                        Sound(samples, audioPts, ToneTracks.Contains(t) ? 440 * (t + 1) : 0);
                        audio[t].Encode(samples, audioPts, packets);
                    }

                    Send(packets, writer);
                    audioPts += 1024;
                }
            }

            video.Flush(packets);
            webcam?.Flush(packets);
            foreach (var track in audio)
            {
                track.Flush(packets);
            }

            Send(packets, writer);
            writer.Finish();
        }
        finally
        {
            packets.ForEach(p => p.Dispose());
            video.Dispose();
            webcam?.Dispose();
            audio.ForEach(a => a.Dispose());
        }
    }

    private VideoEncoder Open(VideoFormat format, int stream) =>
        VideoEncoder.Open(Encoder, format, EncoderPresets.Options(Encoder, QualityPreset.High, format.Width, format.Height, format.Fps), stream);

    private static void Send(List<EncodedPacket> packets, ExportWriter writer)
    {
        packets.ForEach(writer.Write);
        packets.Clear();
    }

    /// <summary>1024 samples of the beeps or a tone from sample <paramref name="start"/>.</summary>
    private void Sound(float[] samples, long start, double tone)
    {
        Array.Clear(samples);
        for (var i = 0; i < 1024; i++)
        {
            var at = start + i;
            float value = 0;
            if (tone > 0)
            {
                value = (float)Math.Sin(2 * Math.PI * tone * at / AudioEncoder.SampleRate) * 0.2f;
            }
            else if (IsBeep(at) is { } t)
            {
                value = MathF.Sin(2 * MathF.PI * 1000 * t / AudioEncoder.SampleRate) * BeepLevel;
            }

            samples[2 * i] = value;
            samples[(2 * i) + 1] = value;
        }
    }

    /// <summary>How far into a beep sample <paramref name="at"/> is, or null outside beeps.</summary>
    private long? IsBeep(long at)
    {
        foreach (var beep in BeepSamples)
        {
            if (at - beep is >= 0 and < BeepSamplesLong)
            {
                return at - beep;
            }
        }

        if (BeepEvery > 0 && at >= BeepEveryFrom)
        {
            var into = (at - BeepEveryFrom) % BeepEvery;
            if (into < BeepSamplesLong)
            {
                return into;
            }
        }

        return null;
    }

    /// <summary>The stamp in the top row, the rest dark or light; neutral colour.</summary>
    private static void Fill(AVFrame* frame, int number, bool flash)
    {
        var width = frame->width;
        var height = frame->height;
        for (var y = 0; y < height; y++)
        {
            var row = frame->data[0] + (y * frame->linesize[0]);
            for (var x = 0; x < width; x++)
            {
                byte luma;
                if (y < Block && x < Bits * Block)
                {
                    var bit = (number >> (Bits - 1 - (x / Block))) & 1;
                    luma = bit == 1 ? (byte)235 : (byte)16;
                }
                else
                {
                    luma = flash ? (byte)200 : (byte)50;
                }

                row[x] = luma;
            }
        }

        var nv12 = frame->format == (int)AVPixelFormat.AV_PIX_FMT_NV12;
        for (var plane = 1u; plane <= (nv12 ? 1u : 2u); plane++)
        {
            for (var y = 0; y < height / 2; y++)
            {
                new Span<byte>(frame->data[plane] + (y * frame->linesize[plane]), nv12 ? width : width / 2).Fill(128);
            }
        }
    }
}
