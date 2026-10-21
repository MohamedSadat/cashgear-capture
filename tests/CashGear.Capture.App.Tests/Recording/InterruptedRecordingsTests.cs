using System.IO;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Recording;
using CashGear.Capture.App.Tray;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Recording;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.App.Tests.Recording;

public sealed class InterruptedRecordingsTests
{
    private readonly FakeRecovery _recovery = new();
    private readonly FakeNotifier _notifier = new();
    private readonly Shell _shell = new();

    private InterruptedRecordings Create() => new(_recovery, _notifier, _shell, NullLogger<InterruptedRecordings>.Instance);

    private static InterruptedRecording Recording(string name) => new(
        Path.Combine(@"C:\Temp\Recordings", name),
        new RecordingManifest(name, new DateTimeOffset(2026, 10, 17, 9, 30, 0, TimeSpan.Zero), $@"D:\Videos\{name}.mp4", 1920, 1080, 30, ["segment-001.mp4"], ["Mixed"]));

    [Fact]
    public async Task Nothing_found_means_nothing_said()
    {
        var interrupted = Create();

        await interrupted.CheckAsync();

        interrupted.Count.Should().Be(0);
        _notifier.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task One_found_is_offered_and_clicking_saves_it_and_says_where()
    {
        _recovery.Waiting.Add(Recording("Demo"));
        var interrupted = Create();
        var changes = 0;
        interrupted.Changed += (_, _) => changes++;

        await interrupted.CheckAsync();

        interrupted.Count.Should().Be(1);
        changes.Should().Be(1);
        _notifier.Messages.Should().ContainSingle().Which.Should().Be((
            "Interrupted recording found",
            "A recording wasn't saved because CashGear Capture closed while recording. Click here (or use the tray menu) to save what was recorded.",
            NotificationKind.Warning));

        await interrupted.RecoverAllAsync();

        _recovery.Recovered.Should().Equal("Demo");
        interrupted.Count.Should().Be(0);
        changes.Should().Be(2);
        var (title, text, kind) = _notifier.Messages[^1];
        (title, kind).Should().Be(("Recording recovered", NotificationKind.Info));
        text.Should().StartWith("Demo.mp4\n0:42 · started").And.EndWith("in D:\\Videos\nThe last second or so before the interruption is missing.");
        _notifier.ClickActions[^1]!();
        _shell.Shown.Should().Equal(@"D:\Videos\Demo.mp4");
    }

    [Fact]
    public async Task Several_are_counted_and_one_that_fails_is_kept_and_explained()
    {
        _recovery.Waiting.AddRange([Recording("A"), Recording("Broken"), Recording("C")]);
        var interrupted = Create();

        await interrupted.CheckAsync();
        _notifier.Messages[0].Title.Should().Be("3 interrupted recordings found");
        await interrupted.RecoverAllAsync();

        _recovery.Recovered.Should().Equal("A", "C");
        interrupted.Count.Should().Be(1, "the one that failed is still there");
        _notifier.Messages.Should().ContainSingle(m => m.Kind == NotificationKind.Error)
            .Which.Text.Should().Be("The disk is full. It is kept; try again from the tray menu.");
    }

    [Fact]
    public async Task A_recording_with_nothing_readable_is_reported_and_gone()
    {
        _recovery.Waiting.Add(Recording("Empty"));
        _recovery.NothingIn = "Empty";
        var interrupted = Create();

        await interrupted.RecoverAllAsync();

        _notifier.Messages.Should().ContainSingle().Which.Title.Should().Be("Nothing to save");
        interrupted.Count.Should().Be(0);
    }

    private sealed class FakeRecovery : IRecordingRecovery
    {
        public List<InterruptedRecording> Waiting { get; } = [];

        public List<string> Recovered { get; } = [];

        public string? NothingIn { get; set; }

        public IReadOnlyList<InterruptedRecording> Find() => [.. Waiting];

        public Task<RecoveredRecording> RecoverAsync(InterruptedRecording recording, CancellationToken ct = default)
        {
            var name = recording.Manifest.Id;
            if (name == "Broken")
            {
                throw new IOException("The disk is full.");
            }

            Waiting.Remove(recording);
            if (name == NothingIn)
            {
                return Task.FromResult(new RecoveredRecording([], TimeSpan.Zero, true));
            }

            Recovered.Add(name);
            return Task.FromResult(new RecoveredRecording([recording.Manifest.OutputPath], TimeSpan.FromSeconds(42), Truncated: true));
        }

        public void Discard(InterruptedRecording recording) => Waiting.Remove(recording);
    }

    private sealed class Shell : IShell
    {
        public List<string> Shown { get; } = [];

        public void ShowInFolder(string path) => Shown.Add(path);

        public void OpenLink(string url) => throw new NotSupportedException();

        public void OpenFile(string path) => throw new NotSupportedException();
    }
}
