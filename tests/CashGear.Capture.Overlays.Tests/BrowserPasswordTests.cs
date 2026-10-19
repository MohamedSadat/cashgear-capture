using System.Diagnostics;
using System.IO;
using CashGear.Capture.Core.Overlays;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Engine.Tests;
using CashGear.Capture.Interop.Native;
using CashGear.Capture.Overlays.Input;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.Overlays.Tests;

/// <summary>
/// Acceptance in a real browser: Microsoft Edge opens a page whose password field has the focus, and keys typed
/// into it are never shown or kept. Explicit: it opens a browser window and types into it (run it while not using
/// the PC: <c>dotnet test --project tests/CashGear.Capture.Overlays.Tests -- --explicit only</c>).
/// </summary>
[Trait("Category", InteractiveDesktop.Category)]
[Collection(DesktopCollection.Name)]
public sealed class BrowserPasswordTests
{
    private const string Title = "CashGear password test";

    [Fact(Explicit = true, Timeout = 60_000)]
    public async Task Typing_in_a_browser_password_field_shows_no_keys()
    {
        InteractiveDesktop.Require();
        using var exclusive = TestInput.Exclusive();
        var ct = TestContext.Current.CancellationToken;
        var edge = new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles }
            .Select(f => Path.Combine(Environment.GetFolderPath(f), "Microsoft", "Edge", "Application", "msedge.exe"))
            .FirstOrDefault(File.Exists);
        Assert.SkipWhen(edge is null, "Microsoft Edge isn't installed.");
        var profile = Directory.CreateTempSubdirectory("cashgear-edge-").FullName;
        var page = "data:text/html,<title>" + Uri.EscapeDataString(Title) + "</title><input type=password autofocus>";
        using var browser = Process.Start(new ProcessStartInfo(edge!)
        {
            ArgumentList = { "--new-window", "--no-first-run", "--user-data-dir=" + profile, page },
            UseShellExecute = false,
        })!;
        var clock = new RecordingClock(TimeProvider.System);
        var timeline = new InputTimeline();
        try
        {
            var window = await FindWindowAsync(ct);
            Assert.SkipWhen(window == 0, "The Edge window didn't appear.");
            User32.SetForegroundWindow(window);
            await Task.Delay(1500, ct); // the page loads and its password field takes the focus
            Assert.SkipUnless(User32.GetForegroundWindow() == window, "Edge didn't come to the front, so the keys would go elsewhere.");

            clock.Start();
            using var recorder = InputRecorder.Start(clock, timeline, null, new KeystrokeOverlaySettings(KeystrokeMode.AllKeys), new CursorLibrary(), NullLoggerFactory.Instance);
            await Task.Delay(500, ct);
            TestInput.Keys([0x53, 0x45, 0x43, 0x52, 0x45, 0x54]); // "secret"
            TestInput.Keys([0x41], 0xA2); // Ctrl+A in the field too
            await Task.Delay(1500, ct);

            recorder.KeysShown.Should().Be(0, "nothing typed into the password field is shown");
            recorder.KeysHidden.Should().Be(7);
            timeline.Captions(clock.Elapsed, TimeSpan.FromMinutes(1)).Should().BeEmpty();
            recorder.Guard!.Last!.Value.Should().Match<FocusCheck>(c => c.Private && c.App == "msedge.exe" && c.Reason == "password field");
        }
        finally
        {
            browser.CloseMainWindow();
            if (!browser.WaitForExit(5000))
            {
                browser.Kill(entireProcessTree: true);
            }

            try
            {
                Directory.Delete(profile, recursive: true);
            }
            catch (IOException)
            {
                // Edge may still hold its profile for a moment
            }
        }
    }

    private static async Task<nint> FindWindowAsync(CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(15))
        {
            foreach (var process in Process.GetProcessesByName("msedge"))
            {
                using (process)
                {
                    if (process.MainWindowTitle.StartsWith(Title, StringComparison.Ordinal))
                    {
                        return process.MainWindowHandle;
                    }
                }
            }

            await Task.Delay(250, ct);
        }

        return 0;
    }
}
