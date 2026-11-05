using System.Text.Json;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.Core.Tests.Settings;

/// <summary>The destinations with a sign-in in settings.json: what is there, what isn't, and what an older file gets.</summary>
public sealed class SharingSettingsTests
{
    [Fact]
    public void Nothing_is_public_or_shared_until_the_user_says_so()
    {
        var sharing = new AppSettings().Normalize().Destinations.Sharing!;

        sharing.YouTube!.Privacy.Should().Be(VideoPrivacy.Private);
        sharing.GoogleDrive!.AnyoneWithLink.Should().BeFalse();
        sharing.OneDrive!.AnyoneWithLink.Should().BeFalse();
        sharing.Dropbox!.AnyoneWithLink.Should().BeFalse();
        sharing.Dropbox.Folder.Should().Be("CashGear Capture");
    }

    [Fact]
    public void Values_are_cleaned_up()
    {
        new CloudFolderSettings(@" Work\ Videos / ").Normalize().Folder.Should().Be("Work/Videos");
        new CloudFolderSettings("").Normalize().Folder.Should().BeEmpty("the drive's top");

        var youtube = new YouTubeSettings((VideoPrivacy)99, "text", [" a ", "", "A", "b"], "  ").Normalize();
        youtube.Privacy.Should().Be(VideoPrivacy.Private);
        youtube.Tags.Should().Equal("a", "b");
        youtube.Playlist.Should().BeNull();

        new SharingClients(" id ", " ", "", null).Normalize().Should().Be(new SharingClients("id"));
    }

    [Fact]
    public void Settings_compare_by_content()
    {
        new YouTubeSettings(Tags: ["a"]).Should().Be(new YouTubeSettings(Tags: ["a"]));
        new YouTubeSettings(Tags: ["a"]).Should().NotBe(new YouTubeSettings(Tags: ["b"]));
        var one = new DestinationSettings(Sharing: new SharingSettings(new YouTubeSettings(Tags: ["a"]))).Normalize();
        var two = new DestinationSettings(Sharing: new SharingSettings(new YouTubeSettings(Tags: ["a"]))).Normalize();
        var other = new DestinationSettings(Sharing: new SharingSettings(new YouTubeSettings(Tags: ["b"]))).Normalize();
        one.Should().Be(two);
        one.Should().NotBe(other);
    }

    [Fact]
    public void They_go_through_the_settings_file_and_a_file_from_before_gets_the_defaults()
    {
        var settings = new AppSettings
        {
            Destinations = new DestinationSettings(Sharing: new SharingSettings(
                new YouTubeSettings(VideoPrivacy.Unlisted, "وصف", ["a"], "PL1", true),
                new CloudFolderSettings("Docs", true),
                Clients: new SharingClients(Dropbox: "key"))),
        }.Normalize();

        var json = JsonSerializer.Serialize(settings, SettingsJsonContext.Readable.AppSettings);
        var read = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings)!.Normalize();

        read.Destinations.Sharing!.YouTube.Should().Be(settings.Destinations.Sharing!.YouTube);
        read.Destinations.Sharing.GoogleDrive.Should().Be(new CloudFolderSettings("Docs", true));
        read.Destinations.Sharing.Clients!.Dropbox.Should().Be("key");
        json.Should().Contain("\"sharing\"");

        var old = JsonSerializer.Deserialize("""{ "schemaVersion": 5, "destinations": { "workflow": [] } }""", SettingsJsonContext.Default.AppSettings)!.Normalize();
        old.Destinations.Sharing!.YouTube!.Privacy.Should().Be(VideoPrivacy.Private);
        old.Destinations.Sharing.Dropbox!.Folder.Should().Be("CashGear Capture");
    }

    [Fact]
    public void An_override_of_a_client_doesnt_touch_the_others()
    {
        var built = new SharingClients("g", "s", "m", "d");

        new SharingClients(Microsoft: "mine").Over(built).Should().Be(new SharingClients("g", "s", "mine", "d"));
    }

    [Fact]
    public void Export_choices_default_to_what_the_window_starts_with()
    {
        var choices = new ExportChoices();

        choices.Preset.Should().Be("mp4-1080");
        choices.Quality.Should().Be(QualityPreset.High);
        choices.LoudnessLufs.Should().Be(-16);
        choices.Chapters.Should().BeTrue();
        choices.SendTo.Should().BeNull("nothing is sent unless asked");
    }
}
