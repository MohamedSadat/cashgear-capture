using CashGear.Capture.Core.Settings;
using CashGear.Capture.Outputs.Credentials;
using CashGear.Capture.Outputs.Http;

namespace CashGear.Capture.Outputs.Tests.Http;

public sealed class HttpUploadDestinationTests : IAsyncLifetime
{
    private readonly TempFolder _temp = new();
    private readonly MemoryCredentials _credentials = new();
    private readonly UploadHttpClient _http = new();
    private TestHttpServer _server = null!;

    public async ValueTask InitializeAsync() => _server = await TestHttpServer.StartAsync();

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        _http.Dispose();
        _temp.Dispose();
    }

    private HttpUploadDestination Destination(HttpDestination upload) => new(upload.Normalize(), _credentials, _http);

    private HttpDestination Upload(string path = "/api/upload", string? linkPath = "data.link", string? authHeader = "Authorization") => new(
        "imgs",
        "Images",
        _server.Address + path,
        "image",
        Fields: [new NameValue("album", "screens"), new NameValue("private", "true")],
        Headers: [new NameValue("X-Client", "cashgear")],
        AuthHeader: authHeader,
        LinkPath: linkPath);

    [Fact]
    public async Task Posts_the_capture_with_fields_headers_and_the_secret_and_takes_the_link_from_the_reply()
    {
        var upload = Upload();
        _credentials.Set(CredentialKeys.Token(upload.Normalize()), "Bearer s3cr3t");
        using var image = TestCaptures.Image(20, 10);
        using var item = TestCaptures.Item(image, _temp.File("outgoing"));

        var result = await Destination(upload).SendAsync(item, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.Message);
        result.Link.Should().Be($"https://img.example.test/{item.Name}.png");
        result.Message.Should().Contain("Images").And.Contain(result.Link);
        var received = _server.Received.Single();
        received.Method.Should().Be("POST");
        received.Path.Should().Be("/api/upload");
        received.FileField.Should().Be("image");
        received.FileName.Should().Be(item.Name + ".png");
        received.FileType.Should().Be("image/png");
        received.FileBytes.Should().Equal(File.ReadAllBytes(await item.GetFileAsync(TestContext.Current.CancellationToken)));
        received.Fields.Should().Contain("album", "screens").And.Contain("private", "true");
        received.Headers["X-Client"].Should().Be("cashgear");
        received.Headers["Authorization"].Should().Be("Bearer s3cr3t");
        received.Headers["User-Agent"].Should().StartWith("CashGear-Capture/");
    }

    [Fact]
    public async Task Without_a_path_the_whole_plain_text_reply_is_the_link()
    {
        _server.Reply = _ => (201, "text/plain", "  https://i.example.test/abc.png\n");
        using var image = TestCaptures.Image(4, 4);
        using var item = TestCaptures.Item(image, _temp.File("outgoing"));

        var result = await Destination(Upload(linkPath: null, authHeader: null)).SendAsync(item, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.Message);
        result.Link.Should().Be("https://i.example.test/abc.png");
        _server.Received.Single().Headers.Should().NotContainKey("Authorization");
    }

    [Fact]
    public async Task An_error_status_is_reported_with_the_start_of_the_reply()
    {
        _server.Reply = _ => (401, "application/json", """{ "error": "bad token" }""");
        _credentials.Set(CredentialKeys.Token(Upload().Normalize()), "Bearer nope");
        using var image = TestCaptures.Image(4, 4);
        using var item = TestCaptures.Item(image, _temp.File("outgoing"));

        var result = await Destination(Upload()).SendAsync(item, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("401").And.Contain("bad token");
    }

    [Theory]
    [InlineData("""{ "data": { "id": 5 } }""", "The link isn't in the reply. \"data\" has no \"link\"")]
    [InlineData("""{ "data": { "link": "not a link" } }""", "isn't a web address")]
    [InlineData("<html>Bad gateway</html>", "isn't JSON")]
    public async Task A_reply_without_a_link_is_reported(string reply, string message)
    {
        _server.Reply = _ => (200, "application/json", reply);
        using var image = TestCaptures.Image(4, 4);
        using var item = TestCaptures.Item(image, _temp.File("outgoing"));

        var result = await Destination(Upload(authHeader: null)).SendAsync(item, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain(message);
    }

    [Fact]
    public async Task A_secret_header_without_a_saved_value_isnt_sent()
    {
        using var image = TestCaptures.Image(4, 4);
        using var item = TestCaptures.Item(image, _temp.File("outgoing"));

        var result = await Destination(Upload()).SendAsync(item, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("No value is saved for the Authorization header");
        _server.Received.Should().BeEmpty();
    }

    [Theory]
    [InlineData("ftp://example.test/up")]
    [InlineData("example.test/upload")]
    [InlineData("")]
    public async Task Only_web_addresses_are_posted_to(string url)
    {
        var result = await Destination(new HttpDestination("x", Url: url)).TestAsync(TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("https://");
    }

    [Fact]
    public async Task Test_uploads_a_one_pixel_picture_and_shows_the_link()
    {
        var result = await Destination(Upload(authHeader: null)).TestAsync(TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.Message);
        result.Message.Should().Contain("https://img.example.test/cashgear-test.png");
        var received = _server.Received.Single();
        received.FileName.Should().Be("cashgear-test.png");
        received.FileBytes.Take(8).Should().Equal(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A);
    }

    [Fact]
    public async Task A_recording_is_posted_as_mp4()
    {
        var video = _temp.File("Recording.mp4");
        File.WriteAllBytes(video, [0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70]);
        using var item = OutputItem.ForVideo(video, DateTimeOffset.Now);

        var result = await Destination(Upload(authHeader: null)).SendAsync(item, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue(result.Message);
        _server.Received.Single().FileType.Should().Be("video/mp4");
        _server.Received.Single().FileName.Should().Be("Recording.mp4");
    }

    [Fact]
    public async Task A_server_that_isnt_there_is_reported()
    {
        var result = await Destination(new HttpDestination("x", Url: $"http://127.0.0.1:{Network.FreePort()}/up")).TestAsync(TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("Couldn't reach 127.0.0.1");
    }
}
