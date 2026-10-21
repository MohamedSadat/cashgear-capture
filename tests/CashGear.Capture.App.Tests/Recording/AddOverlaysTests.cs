using System.IO;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Recording;
using CashGear.Capture.App.Tray;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Recording;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.App.Tests.Recording;

public sealed class AddOverlaysTests
{
    private const string Video = @"D:\Videos\Rec.mp4";

    private readonly FakeNotifier _notifier = new();
    private readonly List<string> _shown = [];
    private readonly Progress _progress = new();

    private sealed class Picker(string? choice) : IRecordingPicker
    {
        public string? Folder { get; private set; }

        public string? Pick(string? folder)
        {
            Folder = folder;
            return choice;
        }
    }

    private sealed class Renderer(string? metadata, Func<IProgress<double>?, CancellationToken, Task<string>> render) : IOverlayRenderer
    {
        public int Renders { get; private set; }

        public string? FindMetadata(string video) => metadata;

        public Task<string> RenderAsync(string video, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            Renders++;
            return render(progress, ct);
        }
    }

    private sealed class Progress : IOverlayProgress
    {
        public Action? Cancel { get; set; }

        public List<double> Reported { get; } = [];

        public bool Closed { get; private set; }

        public void Report(double progress) => Reported.Add(progress);

        public void Dispose() => Closed = true;
    }

    private sealed class Shell(List<string> shown) : IShell
    {
        public void ShowInFolder(string path) => shown.Add(path);

        public void OpenLink(string url) => throw new NotSupportedException();
    }

    private AddOverlaysController Create(IRecordingPicker picker, IOverlayRenderer renderer) => new(
        renderer,
        picker,
        new MutableSettings(),
        TestFfmpeg.Paths(Path.Combine(Path.GetTempPath(), "cashgear-tests", "add-overlays")),
        _notifier,
        new Shell(_shown),
        new InlineUiDispatcher(),
        NullLogger<AddOverlaysController>.Instance,
        (_, cancel) =>
        {
            _progress.Cancel = cancel;
            return _progress;
        });

    [Fact]
    public async Task A_picked_recording_gets_its_overlays_and_the_new_file_is_shown()
    {
        var picker = new Picker(Video);
        var renderer = new Renderer(@"D:\Videos\Rec.cgmeta.json", (progress, _) =>
        {
            progress!.Report(0.5);
            return Task.FromResult(@"D:\Videos\Rec (overlays).mp4");
        });

        await Create(picker, renderer).RunAsync();

        picker.Folder.Should().NotBeNull("it opens in the capture folder");
        _notifier.Messages.Should().ContainSingle().Which.Should().Be(("Overlays added", "Rec (overlays).mp4\nin D:\\Videos", NotificationKind.Info));
        _notifier.ClickActions.Single()!();
        _shown.Should().Equal(@"D:\Videos\Rec (overlays).mp4");
        _progress.Closed.Should().BeTrue();
    }

    [Fact]
    public async Task A_recording_without_metadata_is_explained_and_nothing_is_cancelled_or_kept()
    {
        var none = new Renderer(null, (_, _) => Task.FromResult("never"));
        await Create(new Picker(Video), none).RunAsync();
        none.Renders.Should().Be(0);
        _notifier.Messages.Single().Kind.Should().Be(NotificationKind.Warning);

        await Create(new Picker(null), none).RunAsync();
        _notifier.Messages.Should().HaveCount(1, "cancelling the picker does nothing");
    }

    [Fact]
    public async Task A_failure_is_reported_and_cancelling_says_nothing()
    {
        await Create(new Picker(Video), new Renderer("m", (_, _) => throw new CaptureException("Rec.mp4 already has its overlays drawn in."))).RunAsync();
        _notifier.Messages.Single().Should().Be(("Overlays couldn't be added", "Rec.mp4 already has its overlays drawn in.", NotificationKind.Error));

        var cancelled = new Renderer("m", async (_, ct) =>
        {
            _progress.Cancel!();
            await Task.Delay(Timeout.Infinite, ct);
            return "never";
        });
        await Create(new Picker(Video), cancelled).RunAsync();
        _notifier.Messages.Should().HaveCount(1, "a cancel isn't an error");
        _progress.Closed.Should().BeTrue();
    }
}
