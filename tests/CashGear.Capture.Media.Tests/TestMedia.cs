using System.IO;
using CashGear.Capture.Media;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.Media.Tests;

/// <summary>The FFmpeg build from tools\ffmpeg, copied next to the tests as ffmpeg\ (run tools\get-ffmpeg.ps1 to get it).</summary>
internal static class TestMedia
{
    public static string Directory { get; } = Path.Combine(AppContext.BaseDirectory, "ffmpeg");

    public static FfmpegRuntime Require()
    {
        Assert.SkipUnless(File.Exists(Path.Combine(Directory, FfmpegRuntime.FileName("avcodec"))),
            "FFmpeg isn't in tools\\ffmpeg; run tools\\get-ffmpeg.ps1.");
        var runtime = new FfmpegRuntime(Directory, NullLogger<FfmpegRuntime>.Instance);
        runtime.Problem.Should().BeNull();
        return runtime;
    }
}
