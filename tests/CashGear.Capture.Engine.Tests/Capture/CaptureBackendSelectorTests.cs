using CashGear.Capture.Core.Capture;
using CashGear.Capture.Engine.Capture;

namespace CashGear.Capture.Engine.Tests.Capture;

public sealed class CaptureBackendSelectorTests
{
    public static TheoryData<string, CaptureBackend, CaptureBackend, CaptureBackend> Cases => new()
    {
        // capabilities, preference, expected screen backend, expected window backend
        { "win10", CaptureBackend.Auto, CaptureBackend.Gdi, CaptureBackend.Wgc },
        { "win11", CaptureBackend.Auto, CaptureBackend.Wgc, CaptureBackend.Wgc },
        { "nowgc", CaptureBackend.Auto, CaptureBackend.Gdi, CaptureBackend.Gdi },
        { "win10", CaptureBackend.Wgc, CaptureBackend.Wgc, CaptureBackend.Wgc },
        { "nowgc", CaptureBackend.Wgc, CaptureBackend.Gdi, CaptureBackend.Gdi },
        { "win11", CaptureBackend.Gdi, CaptureBackend.Gdi, CaptureBackend.Gdi },
        { "win10", CaptureBackend.Gdi, CaptureBackend.Gdi, CaptureBackend.Gdi },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Resolves_preference_against_capabilities(
        string os, CaptureBackend preference, CaptureBackend expectedScreen, CaptureBackend expectedWindow)
    {
        var capabilities = os switch
        {
            "win10" => FakeCapabilities.Windows10,
            "win11" => FakeCapabilities.Windows11,
            _ => FakeCapabilities.NoWgc,
        };
        var selector = new CaptureBackendSelector(capabilities);

        selector.ForScreen(preference).Should().Be(expectedScreen);
        selector.ForWindow(preference).Should().Be(expectedWindow);
    }
}
