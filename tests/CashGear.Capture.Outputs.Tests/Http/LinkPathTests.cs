using CashGear.Capture.Outputs.Http;

namespace CashGear.Capture.Outputs.Tests.Http;

public sealed class LinkPathTests
{
    private const string Reply = """
        {
          "data": { "link": "https://i.example.test/a.png", "id": 42, "deleted": false, "tags": ["x"] },
          "files": [ { "url": "https://f.example.test/1" }, { "URL": " https://f.example.test/2 " } ],
          "status": null
        }
        """;

    [Theory]
    [InlineData("data.link", "https://i.example.test/a.png")]
    [InlineData("$.data.link", "https://i.example.test/a.png")]
    [InlineData("$data.link", "https://i.example.test/a.png")]
    [InlineData(" files[0].url ", "https://f.example.test/1")]
    [InlineData("files[1].url", "https://f.example.test/2")]
    [InlineData("data.id", "42")]
    [InlineData("data.tags[0]", "x")]
    [InlineData("DATA.Link", "https://i.example.test/a.png")]
    public void Finds_the_text_at_the_path(string path, string expected) =>
        LinkPath.Extract(Reply, path).Should().Be(expected);

    [Fact]
    public void A_list_at_the_top_is_indexed_first() =>
        LinkPath.Extract("""[ { "src": "https://a.test/1" } ]""", "[0].src").Should().Be("https://a.test/1");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_path_takes_the_whole_reply(string? path) =>
        LinkPath.Extract("  https://plain.test/x \r\n", path).Should().Be("https://plain.test/x");

    [Theory]
    [InlineData("data.missing", "\"data\" has no \"missing\"")]
    [InlineData("nothing", "The reply has no \"nothing\"")]
    [InlineData("data[0]", "\"data\" isn't a list")]
    [InlineData("files[5].url", "\"files\" has 2 item(s), so there is no [5]")]
    [InlineData("data", "\"data\" isn't text but an object")]
    [InlineData("data.deleted", "\"data.deleted\" isn't text but true or false")]
    [InlineData("status", "\"status\" isn't text but null")]
    public void Says_where_the_path_went_wrong(string path, string message)
    {
        var act = () => LinkPath.Extract(Reply, path);

        act.Should().Throw<FormatException>().WithMessage($"*{message}*");
    }

    [Theory]
    [InlineData("data..link")]
    [InlineData("data.")]
    [InlineData(".data")]
    [InlineData("files[x]")]
    [InlineData("files[0")]
    [InlineData("files[-1]")]
    public void A_malformed_path_is_refused(string path)
    {
        LinkPath.TryParse(path, out _, out var error).Should().BeFalse();
        error.Should().Contain(path);
        var act = () => LinkPath.Extract(Reply, path);
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void A_reply_that_isnt_json_is_named()
    {
        var act = () => LinkPath.Extract("<html><body>502 Bad Gateway</body></html>", "data.link");

        act.Should().Throw<FormatException>().WithMessage("The reply isn't JSON: <html>*");
    }

    [Fact]
    public void Paths_parse_into_names_and_indexes()
    {
        LinkPath.TryParse("$.a.b[2][0].c", out var segments, out var error).Should().BeTrue(error);

        segments.Should().Equal("a", "b", 2, 0, "c");
    }
}
