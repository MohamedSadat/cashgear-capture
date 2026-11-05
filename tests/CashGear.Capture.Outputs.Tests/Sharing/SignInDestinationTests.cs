using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Outputs.Credentials;
using CashGear.Capture.Outputs.Sharing;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.Outputs.Tests.Sharing;

/// <summary>Each destination's acceptance against the local fakes: sign-in, token refresh, an upload resumed after a dropped connection.</summary>
public sealed class SignInDestinationTests : IAsyncLifetime
{
    private const int Mb = 1024 * 1024;

    public static TheoryData<string> Services => new() { DestinationIds.YouTube, DestinationIds.GoogleDrive, DestinationIds.OneDrive, DestinationIds.Dropbox };

    private readonly TempFolder _temp = new();
    private SharingHarness _sharing = null!;

    public async ValueTask InitializeAsync() => _sharing = await SharingHarness.StartAsync();

    public async ValueTask DisposeAsync()
    {
        await _sharing.DisposeAsync();
        _temp.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static UploadWork Work(string path, IUploadCheckpoint? checkpoint = null, UploadDetails? details = null, IProgress<long>? progress = null) =>
        new(path, Path.GetFileName(path), checkpoint ?? new MemoryCheckpoint(), progress, details);

    [Theory]
    [MemberData(nameof(Services))]
    public async Task Signing_in_goes_through_the_browser_with_PKCE_and_keeps_only_the_refresh_token(string id)
    {
        var destination = _sharing.Named(id);
        destination.IsSignedIn.Should().BeFalse();

        await destination.SignInAsync(Ct);

        destination.IsSignedIn.Should().BeTrue();
        destination.Account.Should().Be(id == DestinationIds.YouTube ? "My Channel" : "me@example.test");
        var query = _sharing.Server.Authorizes.Single();
        query["code_challenge_method"].ToString().Should().Be("S256");
        var redirect = new Uri(query["redirect_uri"].ToString());
        redirect.Scheme.Should().Be("http");
        redirect.Host.Should().BeOneOf("127.0.0.1", "localhost");
        _sharing.Credentials.Values.Keys.Should().Contain(CredentialKeys.SignIn(id));
        _sharing.Credentials.Values.Values.Should().NotContain(v => v.StartsWith("access-", StringComparison.Ordinal), "access tokens stay in memory");
    }

    [Fact]
    public async Task Google_is_asked_for_a_lasting_permission_and_given_its_desktop_secret()
    {
        _sharing.Server.GoogleSecret = "not-really-secret";
        var youtube = _sharing.YouTube();

        await youtube.SignInAsync(Ct);

        var query = _sharing.Server.Authorizes.Single();
        query["access_type"].ToString().Should().Be("offline");
        query["scope"].ToString().Should().Contain("/auth/youtube");
        youtube.IsSignedIn.Should().BeTrue("the server checks the secret");
    }

    [Fact]
    public async Task A_declined_sign_in_says_why_and_keeps_nothing()
    {
        _sharing.Server.DeclineSignIn = true;
        var drive = _sharing.Drive();

        var act = () => drive.SignInAsync(Ct);

        (await act.Should().ThrowAsync<SignInRequiredException>()).Which.Message.Should().Contain("The user said no");
        drive.IsSignedIn.Should().BeFalse();
        _sharing.Credentials.Values.Should().BeEmpty();
    }

    [Fact]
    public async Task A_request_with_the_wrong_state_is_turned_away_and_the_right_one_still_gets_through()
    {
        var browser = new FollowingBrowser();
        await using var sharing = await SharingHarness.StartAsync(browser);
        HttpStatusCode poked = default;
        browser.Before = async back =>
        {
            using var client = new HttpClient();
            using var response = await client.GetAsync(new UriBuilder(back) { Query = "code=stolen&state=wrong" }.Uri);
            poked = response.StatusCode;
        };

        await sharing.OneDrive().SignInAsync(Ct);

        poked.Should().Be(HttpStatusCode.NotFound);
        sharing.OneDrive().IsSignedIn.Should().BeTrue();
    }

    [Fact]
    public async Task The_redirect_listens_on_the_loopback_address_only()
    {
        using var redirect = new LoopbackRedirect("127.0.0.1", 0);
        var port = new Uri(redirect.RedirectUri).Port;
        var addresses = await Dns.GetHostAddressesAsync(Dns.GetHostName(), AddressFamily.InterNetwork, Ct);
        foreach (var address in addresses.Where(a => !IPAddress.IsLoopback(a)))
        {
            using var other = new TcpClient();
            var connect = () => other.ConnectAsync(address, port, Ct).AsTask();
            await connect.Should().ThrowAsync<SocketException>();
        }
    }

    [Fact]
    public async Task Cancelling_a_sign_in_nobody_finishes_stops_listening()
    {
        await using var sharing = await SharingHarness.StartAsync(new NeverBrowser());
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(200));

        var act = () => sharing.YouTube().SignInAsync(cancel.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [MemberData(nameof(Services))]
    public async Task A_token_that_expired_is_refreshed_without_asking(string id)
    {
        var destination = _sharing.Named(id);
        await destination.SignInAsync(Ct);
        _sharing.Server.ExpireAccessTokens();
        _sharing.OAuth.Invalidate(new OAuthApp(id, id, "", "", "", null, []));

        var tested = await destination.TestAsync(Ct);

        tested.Succeeded.Should().BeTrue(tested.Message);
        _sharing.Server.TokenRefreshes.Should().Be(1);
        _sharing.Server.Authorizes.Should().ContainSingle("no second trip to the browser");
    }

    [Fact]
    public async Task A_new_refresh_token_replaces_the_old_one()
    {
        _sharing.Server.RotateRefreshTokens = true;
        var onedrive = _sharing.OneDrive();
        await onedrive.SignInAsync(Ct);
        var first = _sharing.Credentials.Get(CredentialKeys.SignIn(DestinationIds.OneDrive));
        _sharing.Server.ExpireAccessTokens();
        _sharing.OAuth.Invalidate(new OAuthApp(DestinationIds.OneDrive, "", "", "", "", null, []));

        (await onedrive.TestAsync(Ct)).Succeeded.Should().BeTrue();

        _sharing.Credentials.Get(CredentialKeys.SignIn(DestinationIds.OneDrive)).Should().NotBe(first);
    }

    [Fact]
    public async Task A_revoked_sign_in_asks_for_a_new_one()
    {
        var dropbox = _sharing.Dropbox();
        await dropbox.SignInAsync(Ct);
        _sharing.Server.RevokeRefreshTokens();
        _sharing.Server.ExpireAccessTokens();
        _sharing.OAuth.Invalidate(new OAuthApp(DestinationIds.Dropbox, "", "", "", "", null, []));

        var tested = await dropbox.TestAsync(Ct);

        tested.Succeeded.Should().BeFalse();
        tested.Message.Should().Contain("Sign in again");
    }

    [Fact]
    public async Task Sending_without_a_sign_in_says_so()
    {
        var (path, _) = SharingHarness.RandomFile(_temp.Path, "a.mp4", 1000);

        var result = await _sharing.Drive().UploadAsync(Work(path), Ct);

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("Not signed in");
    }

    [Fact]
    public async Task Signing_out_forgets_everything_on_this_PC()
    {
        var drive = _sharing.Drive();
        await drive.SignInAsync(Ct);

        drive.SignOut();

        drive.IsSignedIn.Should().BeFalse();
        _sharing.Credentials.Values.Should().BeEmpty();
        (await drive.TestAsync(Ct)).Succeeded.Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(Services))]
    public async Task An_upload_goes_up_whole_and_gives_a_link(string id)
    {
        var (path, hash) = SharingHarness.RandomFile(_temp.Path, "a.mp4", 5 * Mb);
        var destination = _sharing.Named(id);
        await destination.SignInAsync(Ct);
        var told = new List<long>();

        var result = await destination.UploadAsync(Work(path, progress: new SyncProgress(told)), Ct);

        result.Succeeded.Should().BeTrue(result.Message);
        result.Link.Should().StartWith("https://");
        var upload = _sharing.Server.OnlyUpload();
        upload.Finished.Should().BeTrue();
        upload.Hash.Should().Equal(hash);
        told.Should().NotBeEmpty();
        told[^1].Should().Be(5 * Mb);
        told.Should().BeInAscendingOrder();
    }

    [Theory]
    [MemberData(nameof(Services))]
    public async Task A_dropped_connection_resumes_without_starting_over(string id)
    {
        const int size = 40 * Mb;
        var (path, hash) = SharingHarness.RandomFile(_temp.Path, "a.mp4", size);
        var destination = _sharing.Named(id);
        await destination.SignInAsync(Ct);
        _sharing.Server.Add(new Fault(5 * Mb, FaultKind.DropMidBody, AfterBytes: 3 * Mb));

        var result = await destination.UploadAsync(Work(path), Ct);

        result.Succeeded.Should().BeTrue(result.Message);
        var upload = _sharing.Server.OnlyUpload();
        upload.Hash.Should().Equal(hash, "every byte, in order");
        _sharing.Server.BytesOnTheWire.Should().BeLessThan(size + (20 * Mb), "only what was lost went twice");
    }

    [Theory]
    [MemberData(nameof(Services))]
    public async Task A_connection_lost_before_the_answer_finds_its_place(string id)
    {
        const int size = 30 * Mb;
        var (path, hash) = SharingHarness.RandomFile(_temp.Path, "a.mp4", size);
        var destination = _sharing.Named(id);
        await destination.SignInAsync(Ct);
        _sharing.Server.Add(new Fault(0, FaultKind.DropBeforeReply));

        var result = await destination.UploadAsync(Work(path), Ct);

        result.Succeeded.Should().BeTrue(result.Message);
        _sharing.Server.OnlyUpload().Hash.Should().Equal(hash);
        _sharing.Server.BytesOnTheWire.Should().Be(size, "what arrived isn't sent again");
    }

    [Fact]
    public async Task The_last_chunk_arriving_with_its_answer_lost_doesnt_upload_twice()
    {
        const int size = 2 * Mb;
        var (path, hash) = SharingHarness.RandomFile(_temp.Path, "a.mp4", size);
        var drive = _sharing.Drive();
        await drive.SignInAsync(Ct);
        _sharing.Server.Add(new Fault(0, FaultKind.DropBeforeReply));

        var result = await drive.UploadAsync(Work(path), Ct);

        result.Succeeded.Should().BeTrue(result.Message);
        _sharing.Server.Uploads.Should().ContainSingle();
        _sharing.Server.OnlyUpload().Hash.Should().Equal(hash);
        _sharing.Server.BytesOnTheWire.Should().Be(size);
    }

    [Theory]
    [MemberData(nameof(Services))]
    public async Task A_server_having_a_bad_moment_is_tried_again(string id)
    {
        const int size = 20 * Mb;
        var (path, hash) = SharingHarness.RandomFile(_temp.Path, "a.mp4", size);
        var destination = _sharing.Named(id);
        await destination.SignInAsync(Ct);
        _sharing.Server.Add(new Fault(0, FaultKind.Status, 503));
        _sharing.Server.Add(new Fault(0, FaultKind.Status, 429, RetryAfterSeconds: 0));

        var result = await destination.UploadAsync(Work(path), Ct);

        result.Succeeded.Should().BeTrue(result.Message);
        _sharing.Server.OnlyUpload().Hash.Should().Equal(hash);
    }

    [Theory]
    [MemberData(nameof(Services))]
    public async Task A_session_the_service_forgot_starts_a_new_one(string id)
    {
        const int size = 30 * Mb;
        var (path, hash) = SharingHarness.RandomFile(_temp.Path, "a.mp4", size);
        var destination = _sharing.Named(id);
        await destination.SignInAsync(Ct);
        _sharing.Server.Add(new Fault(1, FaultKind.LoseSession));

        var result = await destination.UploadAsync(Work(path), Ct);

        result.Succeeded.Should().BeTrue(result.Message);
        _sharing.Server.Uploads.Values.Should().HaveCount(2);
        _sharing.Server.Uploads.Values.Single(u => !u.Lost).Hash.Should().Equal(hash);
    }

    [Fact]
    public async Task A_token_that_ran_out_in_the_middle_is_replaced_and_it_goes_on()
    {
        const int size = 40 * Mb;
        var (path, hash) = SharingHarness.RandomFile(_temp.Path, "a.mp4", size);
        var youtube = _sharing.YouTube();
        await youtube.SignInAsync(Ct);
        _sharing.Server.Add(new Fault(10 * Mb, FaultKind.DropMidBody, AfterBytes: Mb));
        var expired = false;
        var progress = new CallbackProgress(sent =>
        {
            if (!expired && sent > 12 * Mb)
            {
                expired = true;
                _sharing.Server.ExpireAccessTokens();
            }
        });

        var result = await youtube.UploadAsync(Work(path, progress: progress), Ct);

        result.Succeeded.Should().BeTrue(result.Message);
        _sharing.Server.TokenRefreshes.Should().BeGreaterThan(0);
        _sharing.Server.OnlyUpload().Hash.Should().Equal(hash);
    }

    [Fact]
    public async Task A_refusal_is_not_tried_again_and_says_what_the_service_said()
    {
        var (path, _) = SharingHarness.RandomFile(_temp.Path, "a.mp4", Mb);
        var drive = _sharing.Drive();
        await drive.SignInAsync(Ct);
        _sharing.Server.Add(new Fault(0, FaultKind.Status, 403));

        var result = await drive.UploadAsync(Work(path), Ct);

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("403");
    }

    [Fact]
    public async Task An_empty_file_is_refused_at_once()
    {
        var path = _temp.File("empty.mp4");
        File.WriteAllBytes(path, []);
        var drive = _sharing.Drive();
        await drive.SignInAsync(Ct);

        var result = await drive.UploadAsync(Work(path), Ct);

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("empty");
    }

    [Fact]
    public async Task A_finished_upload_isnt_sent_again_when_only_what_follows_failed()
    {
        var (path, _) = SharingHarness.RandomFile(_temp.Path, "a.mp4", Mb);
        var youtube = _sharing.YouTube();
        await youtube.SignInAsync(Ct);
        var checkpoint = new MemoryCheckpoint { Finished = """{ "id": "already" }""" };

        var result = await youtube.UploadAsync(Work(path, checkpoint), Ct);

        result.Link.Should().Be("https://youtu.be/already");
        _sharing.Server.Uploads.Should().BeEmpty();
    }

    [Fact]
    public async Task YouTube_gets_the_title_description_tags_and_privacy()
    {
        var (path, _) = SharingHarness.RandomFile(_temp.Path, "تسجيل.mp4", Mb);
        var youtube = _sharing.YouTube(new YouTubeSettings(VideoPrivacy.Unlisted, "From settings", ["one"]));
        await youtube.SignInAsync(Ct);
        var details = new UploadDetails("عنوان <b>", "0:00 مقدمة\n1:00 Part 2", ["a", "b c"], VideoPrivacy.Public);

        var result = await youtube.UploadAsync(Work(path, details: details), Ct);

        result.Succeeded.Should().BeTrue(result.Message);
        result.Link.Should().StartWith("https://youtu.be/video-");
        using var sent = JsonDocument.Parse(_sharing.Server.OnlyUpload().Metadata!);
        var snippet = sent.RootElement.GetProperty("snippet");
        snippet.GetProperty("title").GetString().Should().Be("عنوان  b", "no angle brackets");
        snippet.GetProperty("description").GetString().Should().Be("0:00 مقدمة\n1:00 Part 2");
        snippet.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).Should().Equal("a", "b c");
        sent.RootElement.GetProperty("status").GetProperty("privacyStatus").GetString().Should().Be("public");
    }

    [Fact]
    public async Task A_workflows_upload_says_what_settings_say_and_is_private_by_default()
    {
        var (path, _) = SharingHarness.RandomFile(_temp.Path, "My recording.mp4", Mb);
        var youtube = _sharing.YouTube(new YouTubeSettings(Description: "From settings", Tags: ["one"]));
        await youtube.SignInAsync(Ct);

        await youtube.UploadAsync(Work(path), Ct);

        using var sent = JsonDocument.Parse(_sharing.Server.OnlyUpload().Metadata!);
        var snippet = sent.RootElement.GetProperty("snippet");
        snippet.GetProperty("title").GetString().Should().Be("My recording", "the file's name");
        snippet.GetProperty("description").GetString().Should().Be("From settings");
        sent.RootElement.GetProperty("status").GetProperty("privacyStatus").GetString().Should().Be("private");
    }

    [Fact]
    public async Task YouTube_also_gets_the_playlist_and_the_thumbnail()
    {
        var (path, _) = SharingHarness.RandomFile(_temp.Path, "a.mp4", Mb);
        var thumbnail = _temp.File("thumb.png");
        File.WriteAllBytes(thumbnail, Png.Solid(16, 9));
        var youtube = (YouTubeDestination)_sharing.YouTube();
        await youtube.SignInAsync(Ct);

        var result = await youtube.UploadAsync(Work(path, details: new UploadDetails(Playlist: "PL1", ThumbnailPath: thumbnail)), Ct);

        result.Succeeded.Should().BeTrue(result.Message);
        _sharing.Server.Thumbnail.Should().Equal(File.ReadAllBytes(thumbnail));
        var item = _sharing.Server.Bodies.Single(b => b.Key.EndsWith("playlistItems", StringComparison.Ordinal)).Value;
        using var parsed = JsonDocument.Parse(item);
        parsed.RootElement.GetProperty("snippet").GetProperty("playlistId").GetString().Should().Be("PL1");
        parsed.RootElement.GetProperty("snippet").GetProperty("resourceId").GetProperty("videoId").GetString().Should().StartWith("video-");
        (await youtube.PlaylistsAsync(Ct)).Should().ContainSingle().Which.Should().Be(("PL1", "Tutorials"));
    }

    [Fact]
    public async Task A_thumbnail_that_cant_be_read_doesnt_lose_the_video()
    {
        var (path, _) = SharingHarness.RandomFile(_temp.Path, "a.mp4", Mb);
        var youtube = _sharing.YouTube();
        await youtube.SignInAsync(Ct);

        var result = await youtube.UploadAsync(Work(path, details: new UploadDetails(ThumbnailPath: _temp.File("gone.png"))), Ct);

        result.Succeeded.Should().BeTrue();
        result.Link.Should().NotBeNull();
        result.Message.Should().Contain("thumbnail");
    }

    [Theory]
    [InlineData("PL123", "PL123")]
    [InlineData("https://www.youtube.com/playlist?list=PLabc_-1", "PLabc_-1")]
    [InlineData("https://www.youtube.com/watch?v=x&list=PLabc", "PLabc")]
    [InlineData("  ", null)]
    public void A_playlist_is_found_in_what_was_typed(string typed, string? id) => YouTubeDestination.PlaylistId(typed).Should().Be(id);

    [Fact]
    public void What_YouTube_refuses_is_cleaned_up()
    {
        YouTubeDestination.CleanTitle(new string('x', 150), "f").Should().HaveLength(100);
        YouTubeDestination.CleanTitle("  <>  ", "fallback").Should().Be("fallback");
        Encoding.UTF8.GetByteCount(YouTubeDestination.CleanDescription(new string('ا', 4000))).Should().BeLessThanOrEqualTo(5000);
        YouTubeDestination.CleanDescription("a<b>c").Should().Be("a b c");
        var tags = YouTubeDestination.CleanTags(Enumerable.Range(0, 200).Select(i => $"tag number {i}"));
        (tags.Sum(t => t.Length + 3) - 1).Should().BeLessThanOrEqualTo(500);
    }

    [Fact]
    public async Task Drive_makes_the_folder_once_and_the_file_goes_in_it()
    {
        var drive = _sharing.Drive(new CloudFolderSettings("Work/Videos"));
        await drive.SignInAsync(Ct);

        var first = await drive.UploadAsync(Work(SharingHarness.RandomFile(_temp.Path, "a.mp4", Mb).Path), Ct);
        var second = await drive.UploadAsync(Work(SharingHarness.RandomFile(_temp.Path, "b.mp4", Mb).Path), Ct);

        first.Succeeded.Should().BeTrue(first.Message);
        second.Succeeded.Should().BeTrue(second.Message);
        _sharing.Server.DriveFolders.Keys.Should().BeEquivalentTo("Work", "Videos");
        using var metadata = JsonDocument.Parse(_sharing.Server.Uploads.Values.First().Metadata!);
        metadata.RootElement.GetProperty("parents")[0].GetString().Should().Be(_sharing.Server.DriveFolders["Videos"]);
        _sharing.Server.Requests.Count(r => r == "POST /google/api/drive/v3/files").Should().Be(2, "each level once");
    }

    [Fact]
    public async Task Drive_shares_the_file_only_when_told_to()
    {
        var (path, _) = SharingHarness.RandomFile(_temp.Path, "a.mp4", Mb);
        var closed = _sharing.Drive();
        await closed.SignInAsync(Ct);

        var kept = await closed.UploadAsync(Work(path), Ct);

        kept.Link.Should().StartWith("https://drive.example.test/");
        _sharing.Server.Bodies.Keys.Should().NotContain(k => k.EndsWith("/permissions", StringComparison.Ordinal));

        var open = _sharing.Drive(new CloudFolderSettings(AnyoneWithLink: true));
        await open.UploadAsync(Work(path), Ct);

        var permission = _sharing.Server.Bodies.Single(b => b.Key.EndsWith("/permissions", StringComparison.Ordinal)).Value;
        permission.Should().Contain("anyone").And.Contain("reader");
    }

    [Fact]
    public async Task OneDrive_sends_chunks_without_the_token_and_escapes_the_path()
    {
        var (path, _) = SharingHarness.RandomFile(_temp.Path, "تسجيل #1.mp4", 11 * Mb);
        var onedrive = _sharing.OneDrive(new CloudFolderSettings("My Videos"));
        await onedrive.SignInAsync(Ct);

        var result = await onedrive.UploadAsync(Work(path), Ct);

        result.Succeeded.Should().BeTrue(result.Message);
        result.Link.Should().StartWith("https://onedrive.example.test/");
        var upload = _sharing.Server.OnlyUpload();
        upload.SawToken.Should().BeFalse("the session's address is the permission");
        upload.Path.Should().Be("My Videos/تسجيل #1.mp4");
    }

    [Theory]
    [InlineData("a:b*c?.mp4", "a_b_c_.mp4")]
    [InlineData("  name. ", "name")]
    [InlineData("...", "_")]
    public void What_the_services_refuse_in_a_name_is_replaced(string name, string cleaned) => CloudNames.Clean(name).Should().Be(cleaned);

    [Fact]
    public async Task OneDrive_makes_a_link_for_anyone_only_when_told_to()
    {
        var (path, _) = SharingHarness.RandomFile(_temp.Path, "a.mp4", Mb);
        var open = _sharing.OneDrive(new CloudFolderSettings(AnyoneWithLink: true));
        await open.SignInAsync(Ct);

        var result = await open.UploadAsync(Work(path), Ct);

        result.Link.Should().Be("https://1drv.example.test/shared");
        _sharing.Server.Bodies.Single(b => b.Key.EndsWith("/createLink", StringComparison.Ordinal)).Value.Should().Contain("anonymous");
    }

    [Fact]
    public async Task Dropbox_escapes_its_header_and_adds_without_overwriting()
    {
        var (path, _) = SharingHarness.RandomFile(_temp.Path, "تسجيل.mp4", Mb);
        var dropbox = _sharing.Dropbox(new CloudFolderSettings("فيديو"));
        await dropbox.SignInAsync(Ct);

        var result = await dropbox.UploadAsync(Work(path), Ct);

        result.Succeeded.Should().BeTrue(result.Message);
        var finish = _sharing.Server.Bodies.Single(b => b.Key.EndsWith("/finish", StringComparison.Ordinal)).Value;
        finish.Should().MatchRegex(@"^[\x20-\x7e]+$", "a header holds ASCII");
        using var parsed = JsonDocument.Parse(finish);
        var commit = parsed.RootElement.GetProperty("commit");
        commit.GetProperty("path").GetString().Should().Be("/فيديو/تسجيل.mp4");
        commit.GetProperty("mode").GetString().Should().Be("add");
        commit.GetProperty("autorename").GetBoolean().Should().BeTrue();
        result.Link.Should().StartWith("https://www.dropbox.com/home/");
    }

    [Fact]
    public async Task Dropbox_shares_only_when_told_to()
    {
        var (path, _) = SharingHarness.RandomFile(_temp.Path, "a.mp4", Mb);
        var open = _sharing.Dropbox(new CloudFolderSettings(AnyoneWithLink: true));
        await open.SignInAsync(Ct);

        var result = await open.UploadAsync(Work(path), Ct);

        result.Link.Should().Be("https://dropbox.example.test/s/shared");
        _sharing.Server.Bodies.Single(b => b.Key.EndsWith("create_shared_link_with_settings", StringComparison.Ordinal)).Value.Should().Contain("public");
    }

    [Fact]
    public void Each_is_offered_where_its_registration_is_known_and_settings_can_override_it()
    {
        var none = new SignInDestinations(_sharing.Context, new BuiltInClients(SharingClients.None)).Create(new AppSettings().Normalize(), _sharing.Credentials).ToList();
        none.Should().BeEmpty();

        var built = new SignInDestinations(_sharing.Context, new BuiltInClients(new SharingClients("g", "s", "m", "d"))).Create(new AppSettings().Normalize(), _sharing.Credentials).ToList();
        built.Select(d => d.Id).Should().Equal(DestinationIds.YouTube, DestinationIds.GoogleDrive, DestinationIds.OneDrive, DestinationIds.Dropbox);
        built.Should().OnlyContain(d => d.SupportsVideo && d.IsUpload);
        built.Single(d => d.Id == DestinationIds.YouTube).SupportsImages.Should().BeFalse();

        var overridden = new AppSettings
        {
            Destinations = new DestinationSettings(Sharing: new SharingSettings(Clients: new SharingClients(Dropbox: "mine"))),
        }.Normalize();
        new SignInDestinations(_sharing.Context, new BuiltInClients(SharingClients.None)).Create(overridden, _sharing.Credentials).Should().ContainSingle(d => d.Id == DestinationIds.Dropbox);
    }

    [Fact]
    public void A_client_override_doesnt_borrow_the_secret_of_another_client()
    {
        new SharingClients(Google: "mine").Over(new SharingClients("built", "builtsecret")).GoogleSecret.Should().BeNull();
        new SharingClients().Over(new SharingClients("built", "builtsecret")).GoogleSecret.Should().Be("builtsecret");
    }

    [Fact]
    public async Task The_real_store_keeps_a_refresh_token_encrypted()
    {
        var path = _temp.File("secrets.json");
        var store = new DpapiCredentialStore(path, NullLogger<DpapiCredentialStore>.Instance);
        using var http = new SharingHttp();
        var oauth = new OAuthClient(store, http, new FollowingBrowser(), NullLogger<OAuthClient>.Instance);
        var uploader = new ResumableUploader(_sharing.Wake, NullLogger<ResumableUploader>.Instance);
        var drive = new GoogleDriveDestination(new SharingContext(oauth, http, uploader, _sharing.Context.Endpoints), new CloudFolderSettings(), SharingHarness.GoogleId, null);

        await drive.SignInAsync(Ct);

        var file = await File.ReadAllTextAsync(path, Ct);
        file.Should().NotContain("refresh-").And.Contain("signin:google-drive:refresh");
        store.Get(CredentialKeys.SignIn(DestinationIds.GoogleDrive)).Should().StartWith("refresh-");
    }

    private sealed class SyncProgress(List<long> told) : IProgress<long>
    {
        public void Report(long value)
        {
            lock (told)
            {
                told.Add(value);
            }
        }
    }

    private sealed class CallbackProgress(Action<long> told) : IProgress<long>
    {
        public void Report(long value) => told(value);
    }
}
