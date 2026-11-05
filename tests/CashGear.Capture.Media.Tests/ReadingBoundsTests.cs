using System.IO;
using CashGear.Capture.Core.Recording;

namespace CashGear.Capture.Media.Tests;

/// <summary>
/// A picture read from a video stays inside the memory it was given, whatever its width. FFmpeg's fast conversions work in
/// blocks of pixels, and a width that isn't a whole number of them is where one could write past the end. A buffer with
/// nothing after it is a managed array or a frame of exactly that size, so that would corrupt the process's memory.
/// </summary>
public sealed class ReadingBoundsTests : IDisposable
{
    private const int Guard = 512;
    private const byte Untouched = 0xA5;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cashgear-tests", "reading-bounds", Guid.NewGuid().ToString("N"));

    public ReadingBoundsTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // a decoder may still be closing a file
        }
    }

    public static TheoryData<int, int> Sizes => new()
    {
        { 320, 180 },
        { 322, 182 },
        { 484, 272 },
        { 720, 406 },
        { 1000, 562 },
        { 1016, 572 },
        { 1080, 1920 },
        { 1368, 768 },
        { 1366, 768 },
        { 1920, 1080 },
    };

    [Theory]
    [MemberData(nameof(Sizes))]
    public void A_picture_stays_inside_its_memory(int width, int height)
    {
        TestMedia.Require();
        var path = Path.Combine(_folder, $"{width}x{height}.mp4");
        new StampedRecording { Path = path, Seconds = 0.2, Format = new VideoFormat(width, height, 30) }.Write();
        using var reader = new VideoStreamReader(path, decoding: VideoDecoding.Software);
        var size = reader.Width * reader.Height * 4;
        var memory = new byte[size + Guard];
        Array.Fill(memory, Untouched, size, Guard);

        var read = 0;
        while (reader.Read(memory.AsSpan(0, size), reader.Width, reader.Height) is not null)
        {
            read++;
        }

        read.Should().BeGreaterThan(0);
        var past = memory.AsSpan(size).ToArray().Count(b => b != Untouched);
        past.Should().Be(0, "{0} × {1} wrote nothing past the end of its picture", width, height);
    }
}
