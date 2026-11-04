using System.Text.Json;
using CashGear.Capture.Core.Instance;
using CashGear.Capture.Core.Overlays;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.Core.Tests.Recording;

public sealed class RecordingPlanTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 14, 0, 0, TimeSpan.FromHours(3));

    private static RecordingPlan? Parse(params string[] args)
    {
        var problem = RecordingCommandLine.Parse(args, Now, out var plan, n => n is 1 or 2 ? $"DISPLAY{n}" : null);
        problem.Should().BeNull();
        return plan;
    }

    [Fact]
    public void A_plain_record_is_not_a_plan() => Parse().Should().BeNull();

    [Fact]
    public void A_length_is_the_plans_example()
    {
        var plan = Parse("--duration", "00:30:00")!;
        plan.Stop!.After.Should().Be(TimeSpan.FromMinutes(30));
        plan.Area.Should().Be(RecordingAreaKind.Monitor, "nobody is there to pick an area");
        plan.StartAt.Should().BeNull();
    }

    [Theory]
    [InlineData("00:30:00", 1800)]
    [InlineData("30:00", 1800)]
    [InlineData("90", 90)]
    [InlineData("1:02:03", 3723)]
    public void Lengths_are_read_as_people_write_them(string text, int seconds)
    {
        RecordingCommandLine.TryLength(text, out var length).Should().BeTrue();
        length.Should().Be(TimeSpan.FromSeconds(seconds));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("")]
    [InlineData("half an hour")]
    [InlineData("1:2:3:4")]
    [InlineData("-5")]
    public void Nonsense_is_no_length(string text) => RecordingCommandLine.TryLength(text, out _).Should().BeFalse();

    [Fact]
    public void A_time_later_today_is_today_and_an_earlier_one_is_tomorrow()
    {
        Parse("--start-at", "14:30")!.StartAt.Should().Be(Now.AddMinutes(30));
        Parse("--start-at", "13:00")!.StartAt.Should().Be(Now.AddHours(23));
    }

    [Fact]
    public void A_stop_time_is_after_the_start()
    {
        var plan = Parse("--start-at", "23:00", "--stop-at", "01:00")!;
        plan.StartAt.Should().Be(new DateTimeOffset(2026, 11, 4, 23, 0, 0, Now.Offset));
        plan.Stop!.At.Should().Be(new DateTimeOffset(2026, 11, 5, 1, 0, 0, Now.Offset));

        // Given the other way round, it reads the same.
        Parse("--stop-at", "01:00", "--start-at", "23:00")!.Stop!.At.Should().Be(plan.Stop.At);
    }

    [Fact]
    public void The_other_options_say_what_to_record()
    {
        Parse("--all-monitors")!.Area.Should().Be(RecordingAreaKind.AllMonitors);
        var window = Parse("--window", "Notepad", "--until-closes", "notepad.exe")!;
        window.Area.Should().Be(RecordingAreaKind.Window);
        window.WindowTitle.Should().Be("Notepad");
        window.Stop!.AppClosed.Should().Be("notepad.exe");
        var monitor = Parse("--monitor", "2")!;
        monitor.Area.Should().Be(RecordingAreaKind.Monitor);
        monitor.Monitor.Should().Be("DISPLAY2");
        var profile = Parse("--profile", "Tutorial")!;
        profile.ProfileId.Should().Be("Tutorial");
        profile.Area.Should().BeNull("the profile says what it records");
    }

    [Theory]
    [InlineData("--duration")]
    [InlineData("--duration", "soon")]
    [InlineData("--start-at", "later")]
    [InlineData("--stop-at", "never")]
    [InlineData("--monitor", "9")]
    public void What_cant_be_followed_says_why(params string[] args)
    {
        RecordingCommandLine.Parse(args, Now, out var plan, n => n == 1 ? "DISPLAY1" : null).Should().Contain("--");
        plan.Should().BeNull();
    }

    [Fact]
    public void The_record_switch_carries_its_options_down_the_pipe()
    {
        string[] args = ["--record", "--duration", "00:30:00", "--window", "Two words"];
        var command = InstanceCommandParser.FromArgs(args);
        command.Should().Be(InstanceCommand.Record);
        var wire = InstanceCommandParser.ToWire(command, InstanceCommandParser.ArgumentsFor(command, args));
        InstanceCommandParser.TryParseWire(wire + "\r\n", out var back, out var arguments).Should().BeTrue();
        back.Should().Be(InstanceCommand.Record);
        arguments.Should().Equal("--duration", "00:30:00", "--window", "Two words");
    }

    [Fact]
    public void Other_commands_and_a_plain_record_have_no_options()
    {
        InstanceCommandParser.ArgumentsFor(InstanceCommand.CaptureRegion, ["--capture-region", "--whatever"]).Should().BeEmpty();
        InstanceCommandParser.ArgumentsFor(InstanceCommand.Record, ["--record"]).Should().BeEmpty();
        InstanceCommandParser.TryParseWire("record", out _, out var none).Should().BeTrue();
        none.Should().BeEmpty();
    }

    [Fact]
    public void A_schedule_is_missed_only_when_long_gone()
    {
        RecordingPlan.IsMissed(Now, Now.AddSeconds(30)).Should().BeFalse();
        RecordingPlan.IsMissed(Now, Now.AddMinutes(2)).Should().BeTrue();
        RecordingPlan.IsMissed(Now, Now.AddSeconds(-30)).Should().BeFalse();
    }

    [Fact]
    public void A_new_user_finds_three_profiles_named_in_their_language()
    {
        var profiles = new AppSettings().Normalize().RecordingProfiles;
        profiles.Profiles!.Select(p => p.Id).Should().Equal(RecordingProfiles.TutorialId, RecordingProfiles.MeetingId, RecordingProfiles.BugReportId);
        profiles.Profiles!.Should().AllSatisfy(p => p.ShownName.Should().NotBe(p.Id));
        profiles.Find(RecordingProfiles.BugReportId)!.Area.Should().Be(RecordingAreaKind.Window);
        profiles.Find(profiles.Profiles![0].ShownName)!.Id.Should().Be(RecordingProfiles.TutorialId, "by its name too");
        profiles.Find("no such").Should().BeNull();
        profiles.Find(null).Should().BeNull();
    }

    [Fact]
    public void A_profile_sets_what_it_names_and_leaves_the_rest()
    {
        var settings = new AppSettings().Normalize() with
        {
            Recording = new RecordingSettings(Encoder: "software", MicrophoneGain: 2),
            Overlays = new OverlaySettings(Webcam: new WebcamSettings(Device: "camera", Size: 40)),
        };
        var profile = new RecordingProfile("p", "Mine", Fps: 60, Quality: QualityPreset.High, SystemAudio: false, Microphone: "default", Tracks: AudioTrackLayout.Both, BurnIn: false, Clicks: false, Keystrokes: KeystrokeMode.AllKeys, Webcam: true, WebcamBackground: WebcamLiveBackground.Blur);

        var applied = profile.Apply(settings);

        applied.Recording.Fps.Should().Be(60);
        applied.Recording.Quality.Should().Be(QualityPreset.High);
        applied.Recording.SystemAudio.Should().BeFalse();
        applied.Recording.Microphone.Should().Be("default");
        applied.Recording.Tracks.Should().Be(AudioTrackLayout.Both);
        applied.Recording.Encoder.Should().Be("software", "it doesn't name the encoder");
        applied.Recording.MicrophoneGain.Should().Be(2);
        applied.Overlays.BurnIn.Should().BeFalse();
        applied.Overlays.ClickSettings.Enabled.Should().BeFalse();
        applied.Overlays.KeystrokeSettings.Mode.Should().Be(KeystrokeMode.AllKeys);
        applied.Overlays.WebcamSettings.Enabled.Should().BeTrue();
        applied.Overlays.WebcamSettings.LiveBackground.Should().Be(WebcamLiveBackground.Blur);
        applied.Overlays.WebcamSettings.Device.Should().Be("camera");
        applied.Overlays.WebcamSettings.Size.Should().Be(40);
    }

    [Fact]
    public void A_profile_of_the_settings_as_they_are_gives_those_settings_back()
    {
        var settings = new AppSettings().Normalize() with { Recording = new RecordingSettings(Fps: 15, Quality: QualityPreset.Small, Microphone: "mic", SystemAudio: false) };
        var profile = RecordingProfile.From(settings, "Now");
        profile.Name.Should().Be("Now");
        profile.Apply(new AppSettings().Normalize()).Recording.Should().BeEquivalentTo(settings.Recording);
    }

    [Fact]
    public void Odd_profiles_are_put_right_and_duplicates_dropped()
    {
        var profiles = new RecordingProfiles([new RecordingProfile("a", " Name ", Fps: 33, Area: (RecordingAreaKind)99), new RecordingProfile("A", "Other"), new RecordingProfile(" ", "No id")]).Normalize();
        profiles.Profiles.Should().HaveCount(2);
        profiles.Profiles![0].Should().Match<RecordingProfile>(p => p.Name == "Name" && p.Fps == 30 && p.Area == RecordingAreaKind.Ask);
        profiles.Profiles![1].Id.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Old_settings_keep_working_and_new_ones_come_back_the_same()
    {
        var old = JsonSerializer.Deserialize<RecordingSettings>("""{"fps":60}""", new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        old.Monitors.Should().Be(SeveralMonitors.OneCanvas);
        old.HdrVideo.Should().BeFalse();
        new RecordingSettings(Monitors: SeveralMonitors.TrackEach, CanvasHeight: 777).Normalize().CanvasHeight.Should().Be(0, "only the heights offered");
        new RecordingSettings(CanvasHeight: 1080).Normalize().CanvasHeight.Should().Be(1080);
        new WebcamSettings(SecondDevice: "  ", LiveBackground: (WebcamLiveBackground)9).Normalize().Should().Match<WebcamSettings>(w => w.SecondDevice == null && w.LiveBackground == WebcamLiveBackground.None);
    }

    [Fact]
    public void Two_settings_with_the_same_profiles_are_equal() =>
        new RecordingProfiles([.. RecordingProfiles.BuiltIn]).Should().Be(new RecordingProfiles([.. RecordingProfiles.BuiltIn]));

    [Fact]
    public void A_place_on_the_screen_in_the_journal_follows_the_map_and_the_look_is_kept()
    {
        var path = Path.GetTempFileName();
        var saved = Path.ChangeExtension(path, ".mp4");
        try
        {
            using (var journal = new MetadataJournal(path, new MetadataHeader(30, new MetadataArea(1000, 500, 640, 400), 1, true, false, KeystrokeMode.Off, Look: "soft")))
            {
                journal.Frame(0, new Core.Geometry.PixelPoint(1010, 510), 0, true);
                journal.Map = new PointMap(2000, 600, 0.5, 160, 0);
                journal.Frame(1, new Core.Geometry.PixelPoint(2010, 620), 0, true);
                journal.ButtonDown(TimeSpan.FromSeconds(1), PointerButton.Left, new Core.Geometry.PixelPoint(2100, 700));
            }

            var metadata = MetadataJournal.Read(path, saved, [new MetadataPart(saved, 0)])!;
            metadata.Look.Should().Be("soft");
            metadata.Frames[0][1..3].Should().Equal(10, 10);
            metadata.Frames[1][1..3].Should().Equal(165, 10);
            metadata.Clicks[0].X.Should().Be(210);
            metadata.Clicks[0].Y.Should().Be(50);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
