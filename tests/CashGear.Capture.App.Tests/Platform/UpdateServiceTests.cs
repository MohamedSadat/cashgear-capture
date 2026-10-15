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

    private UpdateService Create(string? builtInFeed = Feed) =>
        new(_backend, _settings, _notifier, new UiReadySignal(), TimeProvider.System, NullLogger<UpdateService>.Instance, builtInFeed);

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

        public Exception? Failure { get; set; }

        public List<string> Checks { get; } = [];

        public int Installs { get; private set; }

        public Task<string?> DownloadNewerAsync(string feed, CancellationToken ct)
        {
            Checks.Add(feed);
            return Failure is not null ? Task.FromException<string?>(Failure) : Task.FromResult(Newer);
        }

        public void InstallAfterExit() => Installs++;
    }
}
