using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Stitching;

namespace CashGear.Capture.Core.Tests.Stitching;

public sealed class ScrollStitcherTests
{
    private const int View = 500;

    /// <summary>Feeds the frames at <paramref name="tops"/> and checks each step and the composed result row for row.</summary>
    private static void StitchAndCompare(SyntheticPage page, int[] tops, int ignoreRight = 0)
    {
        using var stitcher = new ScrollStitcher(ignoreRight);
        for (var i = 0; i < tops.Length; i++)
        {
            var step = stitcher.Add(page.Frame(tops[i], View));
            if (i == 0)
            {
                step.Result.Should().Be(StitchResult.First);
            }
            else
            {
                step.Should().Be(new StitchStep(StitchResult.Appended, tops[i] - tops[i - 1]), $"frame {i} scrolled from {tops[i - 1]} to {tops[i]}");
            }
        }

        // Scrolling at the end changes nothing: the capture loop's stop signal.
        stitcher.Add(page.Frame(tops[^1], View)).Result.Should().Be(StitchResult.Unchanged);

        using var expected = page.Expected(tops[^1] + View);
        using var result = stitcher.Compose(SyntheticPage.Metadata);
        result.Width.Should().Be(expected.Width);
        result.Height.Should().Be(expected.Height);
        stitcher.Height.Should().Be(expected.Height);
        FirstDifferentRow(result, expected).Should().Be(-1, "no row may be duplicated, missing or altered");
    }

    private static int FirstDifferentRow(CapturedImage actual, CapturedImage expected)
    {
        for (var y = 0; y < expected.Height; y++)
        {
            if (!actual.GetRow(y).SequenceEqual(expected.GetRow(y)))
            {
                return y;
            }
        }

        return -1;
    }

    [Fact]
    public void A_page_scrolled_in_70_percent_steps_is_reassembled_exactly()
    {
        var page = new SyntheticPage(600, 3000);

        StitchAndCompare(page, [0, 350, 700, 1050, 1400, 1750, 2100, 2450, 2500]);
    }

    [Fact]
    public void Uneven_steps_and_a_short_last_scroll_still_line_up()
    {
        var page = new SyntheticPage(480, 2000, seed: 5);

        StitchAndCompare(page, [0, 337, 690, 1013, 1360, 1477, 1500]);
    }

    [Fact]
    public void A_toolbar_and_status_bar_appear_once_and_the_scrollbar_is_cut_off()
    {
        var page = new SyntheticPage(600, 2600, seed: 3) { HeaderRows = 48, FooterRows = 26, Scrollbar = true };

        StitchAndCompare(page, [0, 340, 680, 1020, 1360, 1700, 2040, 2100], ignoreRight: 24);
    }

    [Fact]
    public void Repeated_lines_do_not_confuse_the_offset()
    {
        // Every other line is a copy of the same line, so several offsets match some rows; only the true one matches all.
        var page = new SyntheticPage(500, 2400, seed: 9, repeatEvery: 2);

        StitchAndCompare(page, [0, 342, 684, 1026, 1368, 1710, 1900]);
    }

    [Fact]
    public void Line_aligned_scrolling_with_blank_rows_at_the_same_place_in_every_frame_is_exact()
    {
        // Wheel scrolling moves whole lines, so every frame starts and ends at the same point of a line (here in a blank gap).
        var page = new SyntheticPage(500, 2700, seed: 11) { FooterRows = 20 };
        var step = 19 * SyntheticPage.LineHeight;

        StitchAndCompare(page, [0, step, 2 * step, 3 * step, 4 * step, 5 * step, 6 * step, 2700 - View]);
    }

    [Fact]
    public void A_blinking_caret_is_not_a_scroll()
    {
        var page = new SyntheticPage(500, 1500);
        using var stitcher = new ScrollStitcher();
        stitcher.Add(page.Frame(100, View));

        var withCaret = page.Frame(100, View, (pixels, width) =>
        {
            for (var y = 40; y < 56; y++)
            {
                pixels[(y * width) + 200] = 0xFF000000;
            }
        });

        stitcher.Add(withCaret).Result.Should().Be(StitchResult.Unchanged);
        stitcher.FrameCount.Should().Be(1);
    }

    [Fact]
    public void A_scroll_too_far_to_overlap_is_rejected()
    {
        var page = new SyntheticPage(500, 2000);
        using var stitcher = new ScrollStitcher();
        stitcher.Add(page.Frame(0, View));

        stitcher.Add(page.Frame(View + 50, View)).Result.Should().Be(StitchResult.NoOverlap);

        stitcher.FrameCount.Should().Be(1);
        stitcher.Height.Should().Be(View);
    }

    [Fact]
    public void A_resized_window_is_rejected()
    {
        var page = new SyntheticPage(500, 2000);
        using var stitcher = new ScrollStitcher();
        stitcher.Add(page.Frame(0, View));

        stitcher.Add(page.Frame(300, View - 10)).Result.Should().Be(StitchResult.SizeChanged);
    }

    [Fact]
    public void Blank_frames_count_as_unchanged()
    {
        var page = new SyntheticPage(500, 2000);
        using var stitcher = new ScrollStitcher();
        stitcher.Add(page.Frame(0, View, (pixels, _) => Array.Fill(pixels, 0xFFFFFFFF)));

        stitcher.Add(page.Frame(0, View, (pixels, _) => Array.Fill(pixels, 0xFFFFFFFF))).Result.Should().Be(StitchResult.Unchanged);
    }

    [Fact]
    public void Composing_needs_a_frame_and_keeps_the_capture_metadata()
    {
        var page = new SyntheticPage(300, 900);
        using var stitcher = new ScrollStitcher();
        var empty = () => stitcher.Compose(SyntheticPage.Metadata);
        empty.Should().Throw<InvalidOperationException>();

        stitcher.Add(page.Frame(0, 400));
        stitcher.Add(page.Frame(250, 400));
        using var result = stitcher.Compose(SyntheticPage.Metadata with { WindowTitle = "Notes" });

        result.Metadata.Kind.Should().Be(CaptureKind.Scrolling);
        result.Metadata.WindowTitle.Should().Be("Notes");
        result.Metadata.SourceBounds.Should().Be(new PixelRect(10, 20, 300, 650));
    }
}

public sealed class RowSignatureTests
{
    [Fact]
    public void Rows_are_fingerprinted_without_the_ignored_band_and_blank_rows_are_flagged()
    {
        var page = new SyntheticPage(200, 400) { Scrollbar = true };
        using var a = page.Frame(0, 100);
        using var b = page.Frame(0, 100, (pixels, width) => pixels[(5 * width) + width - 3] = 0xFF00FF00);

        var full = RowSignature.Compute(b);
        var ignoring = RowSignature.Compute(b, ignoreRight: SyntheticPage.ScrollbarWidth);

        full.Hashes[5].Should().NotBe(RowSignature.Compute(a).Hashes[5], "the change is in the scrollbar band");
        ignoring.Hashes[5].Should().Be(RowSignature.Compute(a, SyntheticPage.ScrollbarWidth).Hashes[5]);
        ignoring.Detailed[0].Should().BeTrue("a line of text");
        ignoring.Detailed[SyntheticPage.LineHeight - 1].Should().BeFalse("the blank gap under a line");
    }

    [Fact]
    public void The_true_offset_scores_highest()
    {
        var page = new SyntheticPage(300, 1200, seed: 4);
        using var a = page.Frame(100, 400);
        using var b = page.Frame(373, 400);

        var overlap = Overlap.Find(RowSignature.Compute(a), RowSignature.Compute(b), ScrollStitcher.MinMatchingRows);

        overlap.Offset.Should().Be(273);
        overlap.Mismatches.Should().Be(0);
        overlap.Matches.Should().BeGreaterThan(50);
    }
}
