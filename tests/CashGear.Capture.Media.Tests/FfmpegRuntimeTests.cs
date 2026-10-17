using CashGear.Capture.Media;
using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.Media.Tests;

public sealed class FfmpegRuntimeTests
{
    [Fact]
    public void The_bundled_libraries_load_and_match_the_bindings()
    {
        var runtime = TestMedia.Require();

        runtime.IsAvailable.Should().BeTrue();
        runtime.Version.Should().StartWith("n9.0");
        (ffmpeg.avcodec_version() >> 16).Should().Be(63u);
        (ffmpeg.avformat_version() >> 16).Should().Be(63u);
        (ffmpeg.avutil_version() >> 16).Should().Be(61u);
    }

    [Fact]
    public void File_names_carry_the_major_versions_of_the_bindings()
    {
        FfmpegRuntime.Libraries.Select(FfmpegRuntime.FileName).Should().Equal(
            "avutil-61.dll", "swresample-7.dll", "swscale-10.dll", "avcodec-63.dll", "avformat-63.dll");
    }

    [Fact]
    public void A_folder_without_the_libraries_says_what_is_missing()
    {
        var empty = Directory.CreateTempSubdirectory("cashgear-media-");
        try
        {
            var runtime = new FfmpegRuntime(empty.FullName, NullLogger<FfmpegRuntime>.Instance);

            runtime.IsAvailable.Should().BeFalse();
            runtime.Problem.Should().Contain("avcodec-63.dll").And.Contain(empty.FullName);
            runtime.Invoking(r => r.EnsureAvailable()).Should().Throw<FfmpegException>().WithMessage("*avcodec-63.dll*");
        }
        finally
        {
            empty.Delete(recursive: true);
        }
    }

    [Fact]
    public void Once_loaded_another_folder_is_refused()
    {
        TestMedia.Require();
        var other = Directory.CreateTempSubdirectory("cashgear-media-");
        try
        {
            foreach (var library in FfmpegRuntime.Libraries)
            {
                File.WriteAllBytes(Path.Combine(other.FullName, FfmpegRuntime.FileName(library)), []);
            }

            var runtime = new FfmpegRuntime(other.FullName, NullLogger<FfmpegRuntime>.Instance);

            runtime.Problem.Should().Contain("already loaded");
        }
        finally
        {
            other.Delete(recursive: true);
        }
    }

    [Fact]
    public void Errors_carry_FFmpegs_own_description()
    {
        TestMedia.Require();

        FfmpegException.Check(5, "read").Should().Be(5);
        var error = FluentActions.Invoking(() => FfmpegException.Check(ffmpeg.AVERROR_EOF, "read")).Should().Throw<FfmpegException>().Which;
        error.Message.Should().Be($"read failed: End of file ({ffmpeg.AVERROR_EOF}).");
        error.ErrorCode.Should().Be(ffmpeg.AVERROR_EOF);
        error.Operation.Should().Be("read");
    }
}

public sealed class FfmpegLogTests
{
    [Fact]
    public unsafe void Warnings_logged_on_this_thread_are_captured()
    {
        TestMedia.Require();

        using (var scope = FfmpegLog.Capture())
        {
            ffmpeg.av_log(null, ffmpeg.AV_LOG_WARNING, "first warning\n");
            ffmpeg.av_log(null, ffmpeg.AV_LOG_ERROR, "then an error\n");
            ffmpeg.av_log(null, ffmpeg.AV_LOG_INFO, "information is not captured\n");
            ffmpeg.av_log(null, ffmpeg.AV_LOG_DEBUG, "nor is debug\n");

            scope.Messages.Should().Equal("first warning", "then an error");
            scope.Summary().Should().Be("first warning then an error");
        }

        using var after = FfmpegLog.Capture();
        after.Messages.Should().BeEmpty();
    }

    [Fact]
    public unsafe void Other_threads_are_not_captured()
    {
        TestMedia.Require();

        using var scope = FfmpegLog.Capture();
        var thread = new Thread(() => ffmpeg.av_log(null, ffmpeg.AV_LOG_WARNING, "from another thread\n"));
        thread.Start();
        thread.Join();

        scope.Messages.Should().BeEmpty();
    }
}
