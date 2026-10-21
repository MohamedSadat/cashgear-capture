using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace CashGear.Capture.Media.Tests;

/// <summary>What ffprobe (from tools\ffmpeg) says about an animated GIF, and whether it says to loop.</summary>
internal sealed record GifProbe(string Format, string Codec, int Width, int Height, int Frames, double Duration, bool Loops)
{
    public static GifProbe Of(string file)
    {
        var info = new ProcessStartInfo(Path.Combine(TestMedia.Directory, "ffprobe.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        foreach (var arg in new[] { "-v", "error", "-count_frames", "-show_entries", "format=format_name,duration:stream=codec_name,width,height,nb_read_frames", "-of", "json", file })
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info)!;
        var json = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        using var document = JsonDocument.Parse(json);
        var format = document.RootElement.GetProperty("format");
        var stream = document.RootElement.GetProperty("streams")[0];

        // The NETSCAPE2.0 application extension is what makes a GIF play again (FFmpeg leaves it out to play once).
        var loops = File.ReadAllBytes(file).AsSpan().IndexOf("NETSCAPE2.0"u8) >= 0;
        return new GifProbe(
            format.GetProperty("format_name").GetString()!,
            stream.GetProperty("codec_name").GetString()!,
            stream.GetProperty("width").GetInt32(),
            stream.GetProperty("height").GetInt32(),
            int.Parse(stream.GetProperty("nb_read_frames").GetString()!, CultureInfo.InvariantCulture),
            double.Parse(format.GetProperty("duration").GetString()!, CultureInfo.InvariantCulture),
            loops);
    }
}
