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

    /// <summary>A recording whose first sound clip has two volume keyframes, at 10 pixels a second.</summary>
    private static (TimelineSession Session, Clip Clip, TrackRow Row) WithVolumeKeys()
    {
        var project = TestProjects.Project(60);
        var clip = project.Tracks.First(t => t.HasSound).Clips.First() with { VolumeKeys = [new(10, 1), new(20, 0.25)] };
        project = project.WithItem(clip);
        var view = new TimelineView { PixelsPerSecond = 10, Width = 1000, Height = 400 };
        var session = new TimelineSession(new ProjectHistory(project), view);
        return (session, clip, TimelineView.Rows(project).Single(r => r.Track.Items.Any(i => i.Id == clip.Id)));
    }

    [Fact]
    public void Volume_keyframes_are_drawn_on_the_clip_in_the_palettes_colour()
    {
        var (session, clip, row) = WithVolumeKeys();
        using var bitmap = new SKBitmap(1000, 400);
        using (var canvas = new SKCanvas(bitmap))
        using (var renderer = new TimelineRenderer { Colors = Palette })
        {
            renderer.Render(canvas, session);
        }

        var (top, bottom) = session.View.ItemBox(row);
        foreach (var key in clip.VolumeKeys!)
        {
            var x = (int)session.View.X(key.Time);
            var y = (int)TimelineView.VolumeY(top, bottom, key.Value);
            bitmap.GetPixel(x, y).Should().Be(Palette.Selection, "the diamond of the key at {0} s", key.Time);
        }

        // The line between them goes down from one to the other, and holds on after the last.
        bitmap.GetPixel(400, (int)TimelineView.VolumeY(top, bottom, 0.25)).Should().Be(Palette.Selection);
        TimelineView.VolumeY(top, bottom, 2).Should().Be(top);
        TimelineView.VolumeY(top, bottom, 0).Should().Be(bottom);
        TimelineView.VolumeAt(top, bottom, TimelineView.VolumeY(top, bottom, 0.5)).Should().BeApproximately(0.5, 1e-9);
    }

    [Fact]
    public void A_selected_clips_volume_keyframe_is_dragged_in_time_and_level_and_Shift_click_adds_one()
    {
        var (session, clip, row) = WithVolumeKeys();
        var view = session.View;
        var (top, bottom) = view.ItemBox(row);
        var (x, y) = (view.X(20), TimelineView.VolumeY(top, bottom, 0.25));

        // Not selected: it is the clip that is taken.
        view.VolumeKeyAt(session.History.Project, x, y, null).Should().BeNull();
        session.Select(clip.Id);
        view.VolumeKeyAt(session.History.Project, x, y, clip.Id).Should().Be((clip.Id, 20.0));
        view.VolumeKeyAt(session.History.Project, x + 20, y, clip.Id).Should().BeNull();

        var (toX, toY) = (view.X(30), TimelineView.VolumeY(top, bottom, 1.5));
        session.PointerDown(x, y, shift: false);
        session.PointerMove(toX, toY, alt: true);
        session.PointerUp(toX, toY);
        var moved = (Clip)session.History.Project.Find(clip.Id)!.Value.Item;
        moved.Start.Should().Be(clip.Start, "the clip stays where it is");
        moved.VolumeKeys![1].Time.Should().BeApproximately(30, 1e-3);
        moved.VolumeKeys![1].Value.Should().BeApproximately(1.5, 0.05);
        session.Playhead.Should().BeApproximately(30, 1e-3, "the playhead goes along, so what it is at can be heard");
        session.History.Undo();
        ((Clip)session.History.Project.Find(clip.Id)!.Value.Item).VolumeKeys![1].Time.Should().Be(20, "one drag is one undo step");

        var addY = TimelineView.VolumeY(top, bottom, 0.5);
        session.PointerDown(view.X(40), addY, shift: true);
        session.PointerUp(view.X(40), addY);
        var added = ((Clip)session.History.Project.Find(clip.Id)!.Value.Item).VolumeKeys!;
        added.Should().HaveCount(3);
        added[2].Time.Should().BeApproximately(40, 1e-3);
        added[2].Value.Should().BeApproximately(0.5, 0.05);
    }

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
