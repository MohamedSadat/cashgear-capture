using System.IO;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Media.Tests;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Playback;
using CashGear.Capture.VideoEditor.Rendering;

namespace CashGear.Capture.VideoEditor.Tests;

/// <summary>Real media for the tests, made once per test class in its own folder.</summary>
internal sealed class TestMediaFiles : IDisposable
{
    public TestMediaFiles(string prefix) => Root = Directory.CreateTempSubdirectory(prefix).FullName;

    public string Root { get; }

    /// <summary>The frequency of a tone in the left channel, from its rising zero crossings.</summary>
    public static double Frequency(ReadOnlySpan<float> interleaved)
    {
        var crossings = new List<double>();
        for (var i = 1; i < interleaved.Length / 2; i++)
        {
            var before = interleaved[2 * (i - 1)];
            var now = interleaved[2 * i];
            if (before < 0 && now >= 0)
            {
                crossings.Add(i - 1 + (before / (before - now)));
            }
        }

        return (crossings.Count - 1) / ((crossings[^1] - crossings[0]) / 48_000.0);
    }

    public void Dispose()
    {
        if (Environment.GetEnvironmentVariable("CASHGEAR_KEEP_TEST_FILES") is null)
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // a decoder thread may still close a file
            }
        }
    }

    /// <summary>A stamped recording: pictures numbered, a flash and a beep every 2 s from 0.5 s, and tone tracks if asked.</summary>
    public string Stamped(string name, double seconds = 4, VideoFormat? format = null, IReadOnlyList<string>? tracks = null, IReadOnlySet<int>? tones = null, VideoFormat? webcam = null)
    {
        var path = Path.Combine(Root, name);
        if (File.Exists(path))
        {
            return path;
        }

        var size = format ?? new VideoFormat(320, 180, 30);
        new StampedRecording
        {
            Path = path,
            Seconds = seconds,
            Format = size,
            Tracks = tracks ?? ["Mixed"],
            ToneTracks = tones ?? new HashSet<int>(),
            Flash = n => n >= size.Fps / 2 && (n - (size.Fps / 2)) % (size.Fps * 2) == 0,
            BeepEvery = 96_000,
            BeepEveryFrom = 24_000,
            Webcam = webcam,
        }.Write();
        return path;
    }
}

/// <summary>Pictures of one plain colour per clip, for compositor tests.</summary>
internal sealed class SolidFrames(Func<Clip, (int Width, int Height, uint Bgra)> look) : IFrameProvider
{
    public List<(string Clip, double Time)> Asked { get; } = [];

    public VideoFrame? Frame(Clip clip, MediaAsset asset, double sourceTime)
    {
        Asked.Add((clip.Id, sourceTime));
        var (width, height, color) = look(clip);
        var frame = FramePool.Shared.Rent(width, height);
        var pixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(frame.Pixels);
        pixels.Fill(color);
        frame.Time = sourceTime;
        return frame;
    }
}
