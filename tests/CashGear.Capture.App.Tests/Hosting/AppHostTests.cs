using System.IO;
using CashGear.Capture.App.Hosting;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Recording;
using CashGear.Capture.App.Tray;
using CashGear.Capture.Recording;
using Microsoft.Extensions.DependencyInjection;

namespace CashGear.Capture.App.Tests.Hosting;

public sealed class AppHostTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cashgear-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>Every service the app registers can be made (the host checks each one's constructor when it is built).</summary>
    [Fact]
    public void The_app_host_builds_with_every_service()
    {
        var paths = Recording.TestFfmpeg.Paths(_root);

        using var host = AppHost.Build([], paths, InstallEvents.None);

        host.Services.GetRequiredService<IOverlayRenderer>().Should().NotBeNull();
        host.Services.GetRequiredService<AddOverlaysController>().IsBusy.Should().BeFalse();
        host.Services.GetRequiredService<TrayViewModel>().AddOverlaysCommand.Should().NotBeNull();
    }
}
