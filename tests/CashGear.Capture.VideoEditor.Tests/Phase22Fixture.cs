using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using CashGear.Capture.VideoEditor.Model;

namespace CashGear.Capture.VideoEditor.Tests;

/// <summary>
/// What goes with <c>Fixtures\phase-22\Project.cgcap</c>: the sound its clips play, made here always the same rather than
/// kept as large files, and the reading of its golden mix (<c>mix.f32.gz</c>): every <see cref="MixStep"/>th frame of what
/// the Phase 22 code mixed, 32-bit float.
/// </summary>
internal static class Phase22Fixture
{
    public const int MixStep = 32;

    public const int Rate = 48_000;

    /// <summary>The voice's and the music's files in <paramref name="folder"/>.</summary>
    public static (string Voice, string Music) WriteSound(string folder)
    {
        Directory.CreateDirectory(folder);
        var voice = Path.Combine(folder, "voice.wav");
        var music = Path.Combine(folder, "music.wav");
        WriteWav(voice, 9, (i, t) =>
        {
            // Syllables at about four a second over a little noise, each channel a bit its own.
            var syllable = Math.Pow(Math.Max(0, Math.Sin(2 * Math.PI * 2 * t)), 2) * (Math.Sin(2 * Math.PI * 0.11 * t) > -0.7 ? 1 : 0);
            var pitch = 140 + (30 * Math.Sin(2 * Math.PI * 0.7 * t));
            var phase = 2 * Math.PI * pitch * t;
            var left = syllable * ((0.3 * Math.Sin(phase)) + (0.12 * Math.Sin(2 * phase)) + (0.05 * Math.Sin(3 * phase)));
            var noise = (Noise(i) * 0.004) - 0.002;
            return (left + noise, (left * 0.8) + (Noise(i + 7) * 0.004) - 0.002);
        });
        WriteWav(music, 13, (_, t) =>
        {
            var chord = Math.Sin(2 * Math.PI * 220 * t) + Math.Sin(2 * Math.PI * 277.18 * t) + Math.Sin(2 * Math.PI * 329.63 * t);
            var swell = 0.75 + (0.25 * Math.Sin(2 * Math.PI * 0.25 * t));
            return (0.15 * chord * swell, 0.15 * chord * (1.5 - swell));
        });
        return (voice, music);
    }

    /// <summary>A 16-bit stereo WAV file of <paramref name="seconds"/> seconds.</summary>
    public static void WriteWav(string path, double seconds, Func<int, double, (double Left, double Right)> sample)
    {
        var frames = (int)(seconds * Rate);
        var samples = new short[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var (left, right) = sample(i, (double)i / Rate);
            samples[2 * i] = (short)Math.Round(Math.Clamp(left, -1, 1) * 32767);
            samples[(2 * i) + 1] = (short)Math.Round(Math.Clamp(right, -1, 1) * 32767);
        }

        WriteWav(path, samples);
    }

    /// <summary>A 16-bit stereo WAV file of these samples.</summary>
    public static void WriteWav(string path, short[] interleaved)
    {
        using var file = new BinaryWriter(File.Create(path));
        var bytes = interleaved.Length * 2;
        file.Write(Encoding.ASCII.GetBytes("RIFF"));
        file.Write(36 + bytes);
        file.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        file.Write(16);
        file.Write((short)1);
        file.Write((short)2);
        file.Write(Rate);
        file.Write(Rate * 4);
        file.Write((short)4);
        file.Write((short)16);
        file.Write(Encoding.ASCII.GetBytes("data"));
        file.Write(bytes);
        file.Write(MemoryMarshal.AsBytes(interleaved.AsSpan()));
    }

    /// <summary>0 to 1, always the same for the same number.</summary>
    private static double Noise(int n)
    {
        var x = unchecked((uint)n * 2654435761u);
        x ^= x >> 13;
        x = unchecked(x * 1274126177u);
        x ^= x >> 16;
        return x / (double)uint.MaxValue;
    }

    /// <summary>The golden mix as written.</summary>
    public static float[] ReadMix(string path)
    {
        using var input = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        using var bytes = new MemoryStream();
        input.CopyTo(bytes);
        return MemoryMarshal.Cast<byte, float>(bytes.GetBuffer().AsSpan(0, (int)bytes.Length)).ToArray();
    }

    public static void WriteMix(string path, ReadOnlySpan<float> samples)
    {
        using var output = new GZipStream(File.Create(path), CompressionLevel.SmallestSize);
        output.Write(MemoryMarshal.AsBytes(samples));
    }

    /// <summary>Every <see cref="MixStep"/>th frame of the project's whole mix, made a block at a time as the export makes it.</summary>
    public static float[] Mix(VideoProject project, int blockFrames = 1024)
    {
        var total = Playback.TimelineMixer.Sample(project.Duration);
        var kept = new List<float>((int)((total / MixStep * 2) + 2));
        var block = new float[blockFrames * Playback.TimelineMixer.Channels];
        using var mixer = new Playback.TimelineMixer();
        for (var at = 0L; at < total; at += blockFrames)
        {
            var count = (int)Math.Min(blockFrames, total - at);
            var samples = block.AsSpan(0, count * Playback.TimelineMixer.Channels);
            mixer.Mix(project, at, samples);
            for (var n = 0; n < count; n++)
            {
                if ((at + n) % MixStep == 0)
                {
                    kept.Add(samples[2 * n]);
                    kept.Add(samples[(2 * n) + 1]);
                }
            }
        }

        return [.. kept];
    }
}
