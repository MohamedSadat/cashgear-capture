using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Timeline;
using SkiaSharp;

namespace CashGear.Capture.VideoEditor.Tests;

/// <summary>The timeline draws only with its palette's colours (so a high-contrast palette reaches every part), and names its items.</summary>
public sealed class TimelinePaletteTests
{
    private static readonly TimelineColors Palette = new(
        Background: new SKColor(1, 2, 3), RowA: new SKColor(10, 10, 10), RowB: new SKColor(20, 20, 20), Ruler: new SKColor(30, 30, 30),
        Text: new SKColor(40, 40, 40), Tick: new SKColor(50, 50, 50), Picture: new SKColor(60, 60, 200), Webcam: new SKColor(70, 70, 70),
        Sound: new SKColor(60, 200, 60), Other: new SKColor(80, 80, 80), Selection: new SKColor(250, 0, 0), Range: new SKColor(0, 0, 0, 0x40),
        Playhead: new SKColor(0, 0, 250))
    {
        ItemOutline = new SKColor(255, 255, 0),
        SelectedItem = new SKColor(0, 250, 250),
        SelectedLabel = SKColors.Black,
        Label = new SKColor(90, 90, 90),
        LabelShadow = SKColors.Transparent,
    };

    [Fact]
    public void The_renderer_fills_the_background_items_selection_and_playhead_with_the_palette()
    {
        // A 60 s recording split at 20 s, at 10 pixels per second: the screen's row is 26 to 80 px down.
        var session = new TimelineSession(new ProjectHistory(TimelineEdits.Split(TestProjects.Project(60), 20)), new TimelineView { PixelsPerSecond = 10, Width = 1000, Height = 300 });
        var screen = session.History.Project.Tracks.Single(t => t.HasPictures);
        session.Select(screen.Clips.Last().Id);
        session.Playhead = 30;

        using var bitmap = new SKBitmap(1000, 300);
        using (var canvas = new SKCanvas(bitmap))
        using (var renderer = new TimelineRenderer { Colors = Palette })
        {
            renderer.Render(canvas, session);
        }

        bitmap.GetPixel(500, 250).Should().Be(Palette.Background, "below the rows");
        bitmap.GetPixel(55, 5).Should().Be(Palette.Ruler);
        bitmap.GetPixel(100, 70).Should().Be(Palette.Picture, "the first part, not selected");
        bitmap.GetPixel(400, 70).Should().Be(Palette.SelectedItem, "the selected part takes the selection's fill");
        bitmap.GetPixel(300, 4).Should().Be(Palette.Playhead, "the playhead's head at 30 s");
        bitmap.GetPixel(100, 110).Should().Be(Palette.Sound, "the microphone's first part");
    }

    [Fact]
    public void Items_are_named_as_the_timeline_writes_them()
    {
        var project = TestProjects.Project(10);
        var clip = project.Tracks[2].Clips.Single();
        TimelineRenderer.Label(project, clip).Should().Be("Rec.mp4");
        TimelineRenderer.Label(project, clip with { Speed = 2 }).Should().Be("2× · Rec.mp4");
        (project, var arrow) = AnnotationEdits.Add(project, AnnotationShape.Arrow, 1);
        TimelineRenderer.Label(project, project.Find(arrow)!.Value.Item).Should().Be("Arrow");
    }
}
