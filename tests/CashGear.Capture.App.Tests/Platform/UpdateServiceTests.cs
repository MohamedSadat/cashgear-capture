using System.IO;
using CashGear.Capture.App.Hosting;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Tray;
using CashGear.Capture.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.App.Tests.Platform;

public sealed class UpdateServiceTests
{
    private const string Feed = "https://downloads.example.com/capture";

    private readonly FakeBackend _backend = new();
    private readonly MutableSettings _settings = new();
    private readonly FakeNotifier _notifier = new();

    private readonly string _notes = Path.Combine(Path.GetTempPath(), "cashgear-tests", "notes-" + Guid.NewGuid().ToString("N")[..8]);

    private UpdateService Create(string? builtInFeed = Feed) =>
        new(_backend, _settings, _notifier, new UiReadySignal(), TimeProvider.System, NullLogger<UpdateService>.Instance, builtInFeed, _notes);

    [Fact]
    public async Task A_development_build_or_one_without_an_address_cannot_update()
    {
        _backend.IsInstalled = false;
        var notInstalled = Create();
        notInstalled.State.Should().Be(UpdateState.Unavailable);
        notInstalled.StatusText.Should().Contain("installed app");

        _backend.IsInstalled = true;
        var noFeed = Create(builtInFeed: null);
        noFeed.IsAvailable.Should().BeFalse();
        noFeed.StatusText.Should().Contain("no update address");

        await noFeed.CheckAsync(userAsked: true, TestContext.Current.CancellationToken);
        _backend.Checks.Should().BeEmpty();
        _notifier.Messages.Should().ContainSingle().Which.Text.Should().Contain("no update address");
    }

    [Fact]
    public void The_address_in_settings_wins_over_the_built_in_one()
    {
        _settings.Current = new AppSettings { Updates = new UpdateSettings(FeedUrl: @"D:\releases") };
        Create().Feed.Should().Be(@"D:\releases");
    }

    [Fact]
    public async Task Up_to_date_is_only_announced_when_the_user_asked()
    {
        var updates = Create();

        await updates.CheckAsync(userAsked: false, TestContext.Current.CancellationToken);
        updates.State.Should().Be(UpdateState.UpToDate);
        _notifier.Messages.Should().BeEmpty();

        await updates.CheckAsync(userAsked: true, TestContext.Current.CancellationToken);
        _notifier.Messages.Should().ContainSingle().Which.Text.Should().StartWith("You have the latest version");
        _backend.Checks.Should().Equal(Feed, Feed);
    }

    [Fact]
    public async Task A_downloaded_update_is_announced_and_clicking_restarts_into_it()
    {
        _backend.Newer = "1.0.1";
        var updates = Create();
        var restarts = 0;
        var changes = 0;
        updates.RestartRequested += (_, _) => restarts++;
        updates.StateChanged += (_, _) => changes++;

        await updates.CheckAsync(userAsked: false, TestContext.Current.CancellationToken);

        updates.State.Should().Be(UpdateState.Ready);
        updates.ReadyVersion.Should().Be("1.0.1");
        updates.StatusText.Should().Contain("1.0.1 is ready");
        changes.Should().Be(2, "checking, then ready");
        _notifier.Messages.Should().ContainSingle().Which.Title.Should().Be("Update ready");

        _notifier.ClickActions.Single()!.Invoke();
        restarts.Should().Be(1);

        updates.InstallAfterExit();
        _backend.Installs.Should().Be(1);
    }

    [Fact]
    public async Task Failures_are_quiet_unless_the_user_asked()
    {
        _backend.Failure = new System.Net.Http.HttpRequestException("No such host is known.");
        var updates = Create();

        await updates.CheckAsync(userAsked: false, TestContext.Current.CancellationToken);
        updates.State.Should().Be(UpdateState.Failed);
        _notifier.Messages.Should().BeEmpty();

        await updates.CheckAsync(userAsked: true, TestContext.Current.CancellationToken);
        _notifier.Messages.Should().ContainSingle().Which.Should().Be(("Updates", "Couldn't check for updates: No such host is known.", NotificationKind.Warning));
    }

    [Fact]
    public void An_update_downloaded_before_a_restart_is_still_ready()
    {
        _backend.Pending = "1.0.2";
        var updates = Create();

        updates.State.Should().Be(UpdateState.Ready);
        updates.ReadyVersion.Should().Be("1.0.2");
    }

    [Fact]
    public async Task The_channel_chosen_in_settings_is_checked_and_the_installed_one_otherwise()
    {
        var updates = Create();
        updates.InstalledChannel.Should().Be("stable");

        await updates.CheckAsync(userAsked: false, TestContext.Current.CancellationToken);
        _settings.Current = new AppSettings { Updates = new UpdateSettings(Channel: UpdateChannel.Beta) };
        await updates.CheckAsync(userAsked: false, TestContext.Current.CancellationToken);
        _settings.Current = new AppSettings { Updates = new UpdateSettings(Channel: UpdateChannel.Stable) };
        await updates.CheckAsync(userAsked: false, TestContext.Current.CancellationToken);

        _backend.Channels.Should().Equal(null, "beta", "stable");
    }

    [Fact]
    public async Task Back_on_stable_after_a_beta_the_older_stable_is_not_installed()
    {
        _backend.InstalledChannel = "beta";
        _backend.Older = "1.0.9";
        _settings.Current = new AppSettings { Updates = new UpdateSettings(Channel: UpdateChannel.Stable) };
        var updates = Create();

        await updates.CheckAsync(userAsked: true, TestContext.Current.CancellationToken);

        updates.State.Should().Be(UpdateState.OnlyOlder);
        updates.ReadyVersion.Should().BeNull();
        updates.StatusText.Should().Contain("The stable channel's newest is 1.0.9, which is older");
        _notifier.Messages.Should().ContainSingle().Which.Text.Should().Contain("the next release after");
        updates.InstallAfterExit();
        _backend.Installs.Should().Be(0);
    }

    [Fact]
    public async Task A_downloaded_updates_notes_are_kept_for_whats_new()
    {
        _backend.Newer = "1.2.0";
        _backend.NewerNotes = "# 1.2.0\n- Faster.";
        var updates = Create();
        updates.NotesFor("1.2.0").Should().BeNull();

        await updates.CheckAsync(userAsked: false, TestContext.Current.CancellationToken);

        updates.NotesFor("1.2.0").Should().Be("# 1.2.0\n- Faster.");
        Directory.Delete(_notes, recursive: true);
    }

    [Fact]
    public void Nothing_is_installed_when_nothing_was_downloaded()
    {
        Create().InstallAfterExit();
        _backend.Installs.Should().Be(0);
    }

    private sealed class FakeBackend : IUpdateBackend
    {
        public bool IsInstalled { get; set; } = true;

        public string? PendingVersion => Pending;

        public string? Pending { get; set; }

        public string? Newer { get; set; }

        public string? NewerNotes { get; set; }

        public string? Older { get; set; }

        public string? InstalledChannel { get; set; } = "stable";

        public Exception? Failure { get; set; }

        public List<string> Checks { get; } = [];

        public List<string?> Channels { get; } = [];

        public int Installs { get; private set; }

        public Task<UpdateCheck> DownloadNewerAsync(string feed, string? channel, CancellationToken ct)
        {
            Checks.Add(feed);
            Channels.Add(channel);
            return Failure is not null ? Task.FromException<UpdateCheck>(Failure)
                : Task.FromResult(
                    Newer is not null ? new UpdateCheck(UpdateFound.Downloaded, Newer, NewerNotes)
                    : Older is not null ? new UpdateCheck(UpdateFound.OnlyOlder, Older)
                    : UpdateCheck.Nothing);
        }

        public void InstallAfterExit() => Installs++;
    }
}
