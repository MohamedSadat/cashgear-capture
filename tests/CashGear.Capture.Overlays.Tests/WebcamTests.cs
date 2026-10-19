using CashGear.Capture.Overlays.Webcam;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.Overlays.Tests;

/// <summary>The real cameras through Windows.Media.Capture. This PC has none: what matters here is that nothing throws.</summary>
public sealed class WebcamTests
{
    [Fact]
    public async Task Cameras_are_listed_and_a_missing_one_is_explained()
    {
        var webcams = new MediaCaptureWebcams(NullLoggerFactory.Instance);
        var ct = TestContext.Current.CancellationToken;

        var cameras = await webcams.ListAsync(ct);
        var missing = await webcams.OpenAsync("no such camera", 1280, 720, 30, ct);

        cameras.Should().OnlyContain(c => c.Id.Length > 0 && c.Name.Length > 0);
        missing.Source.Should().BeNull();
        missing.Note.Should().Be("The webcam chosen in Settings isn't connected, so the recording has none.");
        if (cameras.Count == 0)
        {
            var first = await webcams.OpenAsync(null, 1280, 720, 30, ct);
            first.Note.Should().Be("No webcam is connected, so the recording has none.");
        }
    }
}
