using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Overlays;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Tests.Recording;

namespace CashGear.Capture.Core.Tests.Overlays;

public sealed class TryMapTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void Events_map_to_recording_time_and_those_from_a_pause_are_left_out()
    {
        var time = new ManualTime();
        var clock = new RecordingClock(time);
        var t0 = time.Now;
        long At(double seconds) => t0 + S(seconds).Ticks;

        clock.TryMap(At(-1), out _).Should().BeFalse("before the start");
        clock.Start();
        time.Now = At(1);
        clock.TryMap(At(0.5), out var first).Should().BeTrue();
        first.Should().Be(S(0.5));

        clock.Stop(); // paused from 1 s to 3 s
        clock.TryMap(At(2), out _).Should().BeFalse("a click during the pause isn't shown at the resume");
        time.Now = At(3);
        clock.Start();
        time.Now = At(4);
        clock.TryMap(At(3.25), out var later).Should().BeTrue();
        later.Should().Be(S(1.25), "the paused time is left out");
        clock.TryMap(At(1.5), out _).Should().BeFalse("still in the pause, asked about later");

        clock.Stop();
        clock.TryMap(At(5), out _).Should().BeFalse("after the stop");
    }
}

public sealed class InputTimelineTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void The_pointer_at_a_time_is_its_last_position_before_it_and_how_long_it_stood()
    {
        var timeline = new InputTimeline(new PixelPoint(5, 5));
        timeline.Move(S(1), new PixelPoint(10, 20));
        timeline.Move(S(2), new PixelPoint(30, 40));
        timeline.Move(S(1.5), new PixelPoint(15, 25)); // arrives late: put in its place

        timeline.Pointer(S(0.5)).At.Should().Be(new PixelPoint(5, 5), "where it was at the start");
        timeline.Pointer(S(1.2)).At.Should().Be(new PixelPoint(10, 20));
        timeline.Pointer(S(1.7)).Should().Be(new PointerState(new PixelPoint(15, 25), S(1.5), 0, true));
        timeline.Pointer(S(9)).StillSince.Should().Be(S(2), "it hasn't moved since 2 s");
    }

    [Fact]
    public void A_click_shows_while_held_and_for_a_while_after_its_release()
    {
        var timeline = new InputTimeline();
        timeline.ButtonDown(S(1), PointerButton.Left, new PixelPoint(1, 1));
        timeline.ButtonUp(S(1.1), PointerButton.Left);
        timeline.ButtonDown(S(2), PointerButton.Right, new PixelPoint(2, 2));

        var window = S(0.5);
        timeline.Clicks(S(0.9), window).Should().BeEmpty();
        timeline.Clicks(S(1.05), window).Should().ContainSingle().Which.Up.Should().BeNull("not released yet at that time");
        timeline.Clicks(S(1.5), window).Should().ContainSingle().Which.Up.Should().Be(S(1.1));
        timeline.Clicks(S(1.7), window).Should().BeEmpty("released more than the window ago");
        timeline.Clicks(S(30), window).Should().ContainSingle().Which.Button.Should().Be(PointerButton.Right, "still held");
    }

    [Fact]
    public void Captions_read_as_their_latest_version_and_go_after_the_hold()
    {
        var timeline = new InputTimeline();
        timeline.Caption(new KeyCaption(1, S(1), "Ctrl + Z"));
        timeline.Caption(new KeyCaption(1, S(1.4), "Ctrl + Z ×2"));
        timeline.Caption(new KeyCaption(2, S(2), "Ctrl + S"));

        var hold = S(1);
        timeline.Captions(S(1.2), hold).Should().ContainSingle().Which.Text.Should().Be("Ctrl + Z");
        timeline.Captions(S(1.5), hold).Should().ContainSingle().Which.Should().Be((1, "Ctrl + Z ×2", S(1), S(1.4)));
        timeline.Captions(S(2.2), hold).Select(c => c.Text).Should().Equal("Ctrl + Z ×2", "Ctrl + S");
        timeline.Captions(S(2.5), hold).Select(c => c.Text).Should().Equal("Ctrl + S");
        timeline.Captions(S(3.1), hold).Should().BeEmpty();
    }

    [Fact]
    public void Clicks_and_keys_made_while_drawing_are_not_overlays()
    {
        var timeline = new InputTimeline();
        timeline.Drawing(S(1), on: true);
        timeline.ButtonDown(S(1.5), PointerButton.Left, default);
        timeline.Caption(new KeyCaption(1, S(1.6), "Ctrl + Z"));
        timeline.Drawing(S(2), on: false);
        timeline.ButtonDown(S(2.5), PointerButton.Left, default);

        timeline.IsDrawing(S(1.5)).Should().BeTrue();
        timeline.Clicks(S(1.7), S(1)).Should().BeEmpty();
        timeline.Captions(S(1.7), S(1)).Should().BeEmpty();
        timeline.Clicks(S(2.6), S(1)).Should().ContainSingle().Which.Down.Should().Be(S(2.5));
    }

    [Fact]
    public void Pruning_keeps_what_later_times_still_need()
    {
        var timeline = new InputTimeline();
        timeline.Move(S(1), new PixelPoint(1, 1));
        timeline.Move(S(2), new PixelPoint(2, 2));
        timeline.Shape(new PointerShapeChange(S(1), 3, true));
        timeline.ButtonDown(S(1), PointerButton.Left, default); // still held
        timeline.ButtonDown(S(1.5), PointerButton.Right, default);
        timeline.ButtonUp(S(1.6), PointerButton.Right);
        timeline.Caption(new KeyCaption(1, S(1), "a"));
        timeline.Caption(new KeyCaption(1, S(12), "ab"));

        timeline.Prune(S(10));

        timeline.Pointer(S(11)).Should().Be(new PointerState(new PixelPoint(2, 2), S(2), 3, true));
        timeline.Clicks(S(11), S(0.5)).Should().ContainSingle().Which.Button.Should().Be(PointerButton.Left);
        timeline.Captions(S(12), S(1)).Should().ContainSingle().Which.Started.Should().Be(S(1), "a caption still being typed keeps its start");
    }
}
