using CashGear.Capture.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.Core.Tests.Settings;

/// <summary>Hand edits to settings.json (e.g. in Notepad) apply live.</summary>
public sealed class SettingsReloadTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cashgear-tests", Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;

    public SettingsReloadTests()
    {
        _paths = new AppPaths(Path.Combine(_root, "settings"), Path.Combine(_root, "logs"), Path.Combine(_root, "captures"));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task External_edit_is_reloaded_and_raises_Changed()
    {
        using var service = Create();
        var changed = new TaskCompletionSource();
        service.Changed += (_, _) => changed.TrySetResult();

        await File.WriteAllTextAsync(_paths.SettingsFile, """{ "hotkeys": { "captureRegion": "Ctrl+F9" } }""", Ct);

        await changed.Task.WaitAsync(Wait, Ct);
        service.Current.Hotkeys.CaptureRegion.Should().Be("Ctrl+F9");
        service.Current.Hotkeys.CaptureFullScreen.Should().Be(HotkeySettings.DefaultCaptureFullScreen);
    }

    [Fact]
    public async Task Invalid_edit_is_ignored_and_not_overwritten()
    {
        using var service = Create();
        var raised = 0;
        service.Changed += (_, _) => raised++;

        await File.WriteAllTextAsync(_paths.SettingsFile, "{ broken", Ct);
        await Task.Delay(1500, Ct);

        raised.Should().Be(0);
        service.Current.Should().Be(new AppSettings());
        (await File.ReadAllTextAsync(_paths.SettingsFile, Ct)).Should().Be("{ broken", "the user's file must be left for them to fix");
        Directory.GetFiles(_paths.SettingsDirectory, "*.corrupt-*").Should().BeEmpty();
    }

    [Fact]
    public async Task Own_writes_raise_Changed_exactly_once()
    {
        using var service = Create();
        var raised = 0;
        service.Changed += (_, _) => Interlocked.Increment(ref raised);

        await service.UpdateAsync(s => s with { Capture = s.Capture with { HideDelayMs = 321 } }, Ct);
        await Task.Delay(1500, Ct); // give the watcher time to (not) fire

        raised.Should().Be(1);
    }

    [Fact]
    public async Task Watching_can_be_turned_off()
    {
        using var service = new JsonSettingsService(_paths, NullLogger<JsonSettingsService>.Instance, TimeProvider.System, watchForExternalChanges: false);
        var raised = 0;
        service.Changed += (_, _) => raised++;

        await File.WriteAllTextAsync(_paths.SettingsFile, """{ "capture": { "hideDelayMs": 5 } }""", Ct);
        await Task.Delay(1000, Ct);

        raised.Should().Be(0);
        service.Current.Capture.HideDelayMs.Should().Be(CaptureSettings.DefaultHideDelayMs);
    }

    private JsonSettingsService Create() =>
        new(_paths, NullLogger<JsonSettingsService>.Instance, TimeProvider.System);
}
