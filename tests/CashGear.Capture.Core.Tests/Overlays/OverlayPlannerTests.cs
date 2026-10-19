using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Overlays;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.Core.Tests.Overlays;

public sealed class OverlayPlannerTests
{
    private static readonly PixelPoint Origin = new(1000, 500);

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static OverlayPlanner Planner(OverlaySettings settings, double scale = 1, bool drawPointer = false, bool webcam = false) =>
        new(settings, Origin, scale, 1280, 720, drawPointer, webcam);

    private static OverlaySettings Settings(
        ClickOverlaySettings? clicks = null, PointerOverlaySettings? pointer = null, KeystrokeOverlaySettings? keys = null, WebcamSettings? webcam = null) =>
        new(Clicks: clicks ?? new ClickOverlaySettings(), Pointer: pointer, Keystrokes: keys, Webcam: webcam);

    [Fact]
    public void A_click_ring_starts_small_at_the_press_grows_and_fades_within_its_time()
    {
        var timeline = new InputTimeline(new PixelPoint(1100, 600));
        timeline.ButtonDown(S(1), PointerButton.Left, new PixelPoint(1100, 600));
        timeline.ButtonUp(S(1.05), PointerButton.Left);
        var planner = Planner(Settings(), scale: 1.5);

        planner.Plan(timeline, S(0.99)).IsEmpty.Should().BeTrue("nothing before the press");
        var first = planner.Plan(timeline, S(1)).Items.OfType<RingItem>().Single();
        var later = planner.Plan(timeline, S(1.3)).Items.OfType<RingItem>().Single();

        (first.X, first.Y).Should().Be((100, 100), "on the frame, where the press was");
        first.Radius.Should().BeApproximately(ClickOverlaySettings.DefaultSize * 1.5 * 0.3, 0.01, "it starts at 30% of its size");
        first.Color.Should().Be(OverlayColor.Parse(ClickOverlaySettings.DefaultLeftColor));
        later.Radius.Should().BeGreaterThan(first.Radius);
        later.Color.A.Should().BeLessThan(255, "fading");
        planner.Plan(timeline, S(1) + OverlayPlanner.RingTime).Items.OfType<RingItem>().Should().BeEmpty("gone after its time");
    }

    [Fact]
    public void A_held_button_shows_a_dot_that_follows_a_drag_and_buttons_have_their_colours()
    {
        var timeline = new InputTimeline();
        timeline.ButtonDown(S(1), PointerButton.Right, new PixelPoint(1010, 510));
        timeline.Move(S(1.5), new PixelPoint(1200, 700));
        timeline.ButtonDown(S(2), PointerButton.Middle, new PixelPoint(1200, 700));
        var planner = Planner(Settings());

        var held = planner.Plan(timeline, S(1.6)).Items;
        held.OfType<DotItem>().Single().Should().Match<DotItem>(d => d.X == 200 && d.Y == 200 && d.Color.A < 255);
        held.OfType<RingItem>().Should().BeEmpty("the ring of a press is over after its time");
        planner.Plan(timeline, S(2.1)).Items.OfType<RingItem>().Single().Color.Should().Be(OverlayColor.Parse(ClickOverlaySettings.DefaultMiddleColor));
        planner.Plan(timeline, S(1.0)).Items.OfType<RingItem>().Single().Color.Should().Be(OverlayColor.Parse(ClickOverlaySettings.DefaultRightColor));
    }

    [Fact]
    public void Spotlight_and_halo_follow_the_pointer_and_scale_with_the_monitor()
    {
        var timeline = new InputTimeline(new PixelPoint(1300, 800));
        var spotlight = Planner(Settings(new ClickOverlaySettings(Enabled: false), new PointerOverlaySettings(PointerEffect.Spotlight, Radius: 80, Dim: 60)), scale: 2);
        var halo = Planner(Settings(new ClickOverlaySettings(Enabled: false), new PointerOverlaySettings(PointerEffect.Halo, Color: "#FF0000", Radius: 50)));

        spotlight.Plan(timeline, S(1)).Items.Should().Equal(new SpotlightItem(300, 300, 160, 0.6));
        var ring = halo.Plan(timeline, S(1)).Items.OfType<HaloItem>().Single();
        (ring.X, ring.Y, ring.Radius).Should().Be((300, 300, 50));
        ring.Color.Should().Be(new OverlayColor(110, 255, 0, 0), "an opaque colour becomes translucent");
    }

    [Fact]
    public void The_app_draws_the_pointer_bigger_and_fades_it_out_when_idle()
    {
        var timeline = new InputTimeline(new PixelPoint(1050, 550));
        timeline.Shape(new PointerShapeChange(S(0), 3, true));
        timeline.Move(S(1), new PixelPoint(1060, 560));
        var planner = Planner(Settings(new ClickOverlaySettings(Enabled: false), new PointerOverlaySettings(Scale: 2, HideWhenIdle: true, IdleSeconds: 2)), drawPointer: true);

        planner.Plan(timeline, S(2)).Items.Should().Equal(new CursorItem(3, 60, 60, 2, 1));
        planner.Plan(timeline, S(3) + (OverlayPlanner.FadeTime / 2)).Items.OfType<CursorItem>().Single().Opacity.Should().BeApproximately(0.5, 0.01);
        planner.Plan(timeline, S(4)).IsEmpty.Should().BeTrue("hidden after standing still");
        timeline.Move(S(5), new PixelPoint(1070, 570));
        planner.Plan(timeline, S(5)).Items.OfType<CursorItem>().Single().Opacity.Should().Be(1, "back as soon as it moves");

        timeline.Shape(new PointerShapeChange(S(6), 3, false));
        planner.Plan(timeline, S(6.1)).IsEmpty.Should().BeTrue("an app hid the pointer");
        Planner(Settings(new ClickOverlaySettings(Enabled: false), new PointerOverlaySettings(Scale: 2)), drawPointer: false).Plan(timeline, S(5)).IsEmpty
            .Should().BeTrue("Windows drew it into the picture");
    }

    [Fact]
    public void Captions_stack_newest_nearest_the_edge_and_fade_after_the_last_key()
    {
        var timeline = new InputTimeline();
        timeline.Caption(new KeyCaption(1, S(1), "Ctrl + C"));
        timeline.Caption(new KeyCaption(2, S(1.2), "Ctrl + V"));
        var keys = new KeystrokeOverlaySettings(KeystrokeMode.Shortcuts, OverlayAnchor.TopLeft, FontSize: 18, FadeSeconds: 2);
        var planner = Planner(Settings(new ClickOverlaySettings(Enabled: false), keys: keys), scale: 1.25);

        var captions = planner.Plan(timeline, S(1.5)).Items.OfType<CaptionItem>().ToList();

        captions.Select(c => (c.Text, c.Row)).Should().Equal(("Ctrl + C", 1), ("Ctrl + V", 0));
        captions[0].Should().Match<CaptionItem>(c => c.Anchor == OverlayAnchor.TopLeft && c.Opacity == 1 && Math.Abs(c.FontSize - (18 * 96.0 / 72 * 1.25)) < 0.001);
        planner.Plan(timeline, S(3) - (OverlayPlanner.FadeTime / 2)).Items.OfType<CaptionItem>().First().Opacity.Should().BeApproximately(0.5, 0.01);
        planner.Plan(timeline, S(3.3)).Items.OfType<CaptionItem>().Should().BeEmpty();
    }

    [Fact]
    public void At_most_three_captions_show_and_nothing_when_keys_are_off()
    {
        var timeline = new InputTimeline();
        for (var i = 1; i <= 5; i++)
        {
            timeline.Caption(new KeyCaption(i, S(i * 0.1), $"F{i}"));
        }

        Planner(Settings(new ClickOverlaySettings(Enabled: false), keys: new KeystrokeOverlaySettings(KeystrokeMode.AllKeys))).Plan(timeline, S(0.6))
            .Items.OfType<CaptionItem>().Select(c => c.Text).Should().Equal("F3", "F4", "F5");
        var off = Planner(Settings(new ClickOverlaySettings(Enabled: false)));
        off.Plan(timeline, S(0.6)).IsEmpty.Should().BeTrue();
        off.DrawsAnything.Should().BeFalse("a recording needn't compose its frames");
    }

    [Fact]
    public void The_webcam_is_placed_by_share_of_the_frame()
    {
        var webcam = new WebcamSettings(Enabled: true, Shape: WebcamShape.RoundedRectangle, Size: 25, CenterX: 80, CenterY: 75, BorderWidth: 4, Mirror: false);
        var planner = Planner(Settings(new ClickOverlaySettings(Enabled: false), webcam: webcam), scale: 1.5, webcam: true);

        planner.Plan(new InputTimeline(), S(1)).Items.Should().Equal(
            new WebcamItem(1024, 540, 180, WebcamShape.RoundedRectangle, false, OverlayColor.Parse(WebcamSettings.DefaultBorderColor), 6));
        Planner(Settings(webcam: webcam), webcam: false).Plan(new InputTimeline(), S(1)).Items.OfType<WebcamItem>().Should().BeEmpty("no camera");
    }

    [Fact]
    public void Click_sounds_are_short_and_differ_by_button()
    {
        var left = ClickSound.For(PointerButton.Left).ToArray();
        var right = ClickSound.For(PointerButton.Right).ToArray();

        left.Length.Should().Be((int)(ClickSound.Length.TotalSeconds * AudioMixer.SampleRate) * AudioMixer.Channels);
        left.Max(Math.Abs).Should().BeInRange(0.15f, 0.4f);
        left.Should().NotEqual(right);
        left[^2..].Max(Math.Abs).Should().BeLessThan(0.01f, "it has died away by its end");
    }
}
