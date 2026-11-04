using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Recording;

namespace CashGear.Capture.Core.Tests.Recording;

public sealed class RecordingTargetTests
{
    [Fact]
    public void A_picture_of_the_places_own_size_is_the_place_exactly() =>
        LetterboxFit.Fit(640, 400, new PixelRect(10, 20, 640, 400)).Should().Be(new PixelRect(10, 20, 640, 400));

    [Theory]
    [InlineData(320, 400, 640, 400, 160, 0, 320, 400)] // narrower: bars at the sides
    [InlineData(800, 200, 640, 400, 0, 120, 640, 160)] // wider: bars above and below
    [InlineData(1280, 800, 640, 400, 0, 0, 640, 400)] // the same shape, larger
    [InlineData(100, 100, 640, 400, 120, 0, 400, 400)]
    public void A_picture_keeps_its_shape_centred_in_the_place(int width, int height, int placeWidth, int placeHeight, int x, int y, int fittedWidth, int fittedHeight) =>
        LetterboxFit.Fit(width, height, new PixelRect(0, 0, placeWidth, placeHeight)).Should().Be(new PixelRect(x, y, fittedWidth, fittedHeight));

    [Fact]
    public void Nothing_to_fit_has_no_size() =>
        LetterboxFit.Fit(0, 0, new PixelRect(5, 5, 100, 100)).IsEmpty.Should().BeTrue();

    [Fact]
    public void A_place_on_the_screen_follows_the_window_and_its_bars()
    {
        // A window of 320 × 400 at (1000, 500), fitted at (160, 0) in a recording of 640 × 400.
        var map = LetterboxFit.Map(new PixelRect(1000, 500, 320, 400), new PixelRect(160, 0, 320, 400));
        map.X(1000).Should().Be(160);
        map.Y(500).Should().Be(0);
        map.X(1160).Should().Be(320);

        // Twice the size: half the scale.
        var half = LetterboxFit.Map(new PixelRect(0, 0, 1280, 800), new PixelRect(0, 0, 640, 400));
        half.X(640).Should().Be(320);
        half.Y(400).Should().Be(200);
    }

    [Fact]
    public void Two_monitors_on_a_canvas_keep_their_layout()
    {
        // The reference PC: 1920 × 1080 with 1280 × 1024 to its right.
        var canvas = MonitorCanvas.Of([new PixelRect(0, 0, 1920, 1080), new PixelRect(1920, 0, 1280, 1024)]);
        (canvas.Width, canvas.Height).Should().Be((3200, 1080));
        canvas.Scale.Should().Be(1);
        canvas.Places.Should().Equal(new PixelRect(0, 0, 1920, 1080), new PixelRect(1920, 0, 1280, 1024));
        canvas.Area.Should().Be(new PixelRect(0, 0, 3200, 1080));
    }

    [Fact]
    public void A_canvas_of_a_chosen_height_scales_them_all_with_even_sides()
    {
        var canvas = MonitorCanvas.Of([new PixelRect(0, 0, 1920, 1080), new PixelRect(1920, 0, 1280, 1024)], 720);
        canvas.Height.Should().Be(720);
        canvas.Width.Should().Be(2132);
        canvas.Scale.Should().BeApproximately(2.0 / 3, 1e-9);
        canvas.Places[0].Should().Be(new PixelRect(0, 0, 1280, 720));
        canvas.Places[1].X.Should().Be(1280);
        canvas.Places[1].Right.Should().BeLessThanOrEqualTo(canvas.Width);
        canvas.Map.X(1920).Should().BeApproximately(1280, 1e-9);
    }

    [Fact]
    public void A_monitor_to_the_left_or_above_moves_the_origin()
    {
        var canvas = MonitorCanvas.Of([new PixelRect(0, 0, 1920, 1080), new PixelRect(-1280, -200, 1280, 1024)]);
        canvas.Area.Should().Be(new PixelRect(-1280, -200, 3200, 1280));
        canvas.Places[0].Should().Be(new PixelRect(1280, 200, 1920, 1080));
        canvas.Places[1].Should().Be(new PixelRect(0, 0, 1280, 1024));
        canvas.Map.X(0).Should().Be(1280);
        canvas.Map.Y(0).Should().Be(200);
    }

    [Fact]
    public void A_canvas_is_never_larger_than_the_monitors_or_than_encoders_take()
    {
        MonitorCanvas.Of([new PixelRect(0, 0, 1920, 1080)], 2160).Height.Should().Be(1080);
        var wide = MonitorCanvas.Of([new PixelRect(0, 0, 5120, 1440), new PixelRect(5120, 0, 5120, 1440)]);
        wide.Width.Should().BeLessThanOrEqualTo(MonitorCanvas.MaxSide);
        (wide.Width % 2).Should().Be(0);
        (wide.Height % 2).Should().Be(0);
    }

    [Fact]
    public void A_recording_stopped_at_its_length_has_exactly_that_many_frames()
    {
        var grid = new FrameGrid(30);
        grid.FinishAtLength(TimeSpan.FromSeconds(5));
        grid.End.Should().Be(150);

        // Frames already made are kept, should the stop come very late.
        var late = new FrameGrid(30);
        for (var i = 0; i < 160; i++)
        {
            late.Produced(repeated: false);
        }

        late.FinishAtLength(TimeSpan.FromSeconds(5));
        late.End.Should().Be(160);
    }

    [Fact]
    public void A_recordings_extra_tracks_are_told_by_their_titles()
    {
        RecordedTracks.ScreenTitle(2).Should().Be("Screen 2");
        RecordedTracks.IsScreen("Screen 2").Should().BeTrue();
        RecordedTracks.IsScreen("Webcam").Should().BeFalse();
        RecordedTracks.IsScreen(null).Should().BeFalse();
        RecordedTracks.ScreenNumber("Screen 3").Should().Be(3);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 0.50808)] // BT.2100's table
    [InlineData(203, 0.58069)] // BT.2408: 58 %
    [InlineData(1000, 0.75183)]
    [InlineData(10000, 1)]
    public void The_PQ_curve_is_the_standards(double nits, double signal)
    {
        HdrVideo.PqEncode(nits).Should().BeApproximately(signal, 0.0002);
        HdrVideo.PqDecode(signal).Should().BeApproximately(nits, Math.Max(0.5, nits * 0.002));
    }

    [Fact]
    public void The_PQ_curve_comes_back_to_where_it_started()
    {
        for (var nits = 0.01; nits < 10000; nits *= 1.5)
        {
            HdrVideo.PqDecode(HdrVideo.PqEncode(nits)).Should().BeApproximately(nits, nits * 1e-6);
        }
    }

    [Fact]
    public void The_primaries_matrices_undo_each_other_and_white_stays_white()
    {
        var (r, g, b) = HdrVideo.Bt709To2020(1, 1, 1);
        r.Should().BeApproximately(1, 1e-4);
        g.Should().BeApproximately(1, 1e-4);
        b.Should().BeApproximately(1, 1e-4);
        var wide = HdrVideo.Bt709To2020(0.8, 0.1, 0.3);
        var back = HdrVideo.Bt2020To709(wide.R, wide.G, wide.B);
        back.R.Should().BeApproximately(0.8, 1e-4);
        back.G.Should().BeApproximately(0.1, 1e-4);
        back.B.Should().BeApproximately(0.3, 1e-4);
    }

    [Fact]
    public void A_float_pixel_goes_to_10_bit_codes_and_back_within_a_level()
    {
        (double R, double G, double B)[] pixels =
        [
            (1, 1, 1),
            (0.5, 0.5, 0.5),
            (1, 0, 0),
            (0, 1, 0),
            (0, 0, 1),
            (0.2, 0.4, 0.8),
            (0.01, 0.02, 0.005),
        ];
        foreach (var (r, g, b) in pixels)
        {
            var codes = HdrVideo.ScRgbToCodes(r, g, b, 1);
            var signal = HdrVideo.CodesToSignal(codes.Y, codes.Cb, codes.Cr);
            var sdr = HdrVideo.SignalToSdr(signal.R, signal.G, signal.B);

            // In light: PQ's codes are fine steps for the eye, so a channel that is nearly nothing beside a bright one can look far off in sRGB's numbers.
            HdrVideo.SrgbDecode(sdr.R / 255.0).Should().BeApproximately(r, 0.012, $"red of {(r, g, b)}");
            HdrVideo.SrgbDecode(sdr.G / 255.0).Should().BeApproximately(g, 0.012, $"green of {(r, g, b)}");
            HdrVideo.SrgbDecode(sdr.B / 255.0).Should().BeApproximately(b, 0.012, $"blue of {(r, g, b)}");
        }
    }

    [Fact]
    public void Highlights_come_down_to_white_keeping_their_hue_as_the_tone_mapper_does()
    {
        var white = HdrVideo.SignalToSdr(HdrVideo.PqEncode(1000), HdrVideo.PqEncode(1000), HdrVideo.PqEncode(1000));
        white.Should().Be(((byte)255, (byte)255, (byte)255));

        // Twice as bright as SDR white, as a whole: orange stays orange.
        var (r, g, b) = HdrVideo.FitSdr(2, 1, 0.25);
        (r, g, b).Should().Be((1, 0.5, 0.125));
    }

    [Fact]
    public void White_has_no_colour_in_its_codes()
    {
        var (y, cb, cr) = HdrVideo.ScRgbToCodes(1, 1, 1, 1);
        cb.Should().Be(512);
        cr.Should().Be(512);
        y.Should().Be((int)Math.Round(64 + (876 * HdrVideo.PqEncode(HdrVideo.ReferenceWhiteNits))));
    }
}
