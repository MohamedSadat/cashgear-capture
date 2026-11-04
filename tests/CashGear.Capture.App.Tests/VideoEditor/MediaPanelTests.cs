using System.IO;
using CashGear.Capture.App.Editor;
using CashGear.Capture.App.VideoEditor;
using CashGear.Capture.Media.Tests;
using CashGear.Capture.VideoEditor.Model;
using SkiaSharp;

namespace CashGear.Capture.App.Tests.VideoEditor;

/// <summary>The media panel, through its view model: adding files, putting them on the timeline, taking them out again.</summary>
public sealed class MediaPanelTests : VideoEditorTestBase
{
    private string Png(string name)
    {
        using var surface = SKSurface.Create(new SKImageInfo(40, 20, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Orange);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var path = Path.Combine(Root, name);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    [Fact]
    public async Task Files_are_listed_and_undone_and_go_on_the_timeline_at_the_playhead()
    {
        TestMedia.Require();
        using var vm = Open();
        vm.Media.Items.Should().ContainSingle("the recording");
        Dialogs.Media = Png("logo.png");
        await vm.Media.ImportCommand.ExecuteAsync(null);
        vm.Media.Items.Should().HaveCount(2);
        var row = vm.Media.Items[1];
        row.Name.Should().Be("logo.png");
        row.Details.Should().Contain("40").And.Contain("20");
        row.AccessibleName.Should().Contain("logo.png");
        vm.Tracks.Should().NotContain(t => t.Track.Kind == TrackKind.Media, "adding a file doesn't put it on the timeline");

        vm.History.Undo();
        vm.Media.Items.Should().ContainSingle();
        vm.History.Redo();
        vm.Media.Items.Should().HaveCount(2);

        vm.Media.Selected = vm.Media.Items[1];
        vm.Seek(0.5);
        vm.Media.AddToTimelineCommand.Execute(null);
        var clip = vm.Timeline.Selected.Should().BeOfType<Clip>().Subject;
        clip.Start.Should().Be(0.5);
        vm.Project.Find(clip.Id)!.Value.Track.Kind.Should().Be(TrackKind.Media);
        vm.Media.Selected!.Details.Should().NotBe(row.AccessibleName);
        vm.PropertiesTitle.Should().NotBeEmpty();

        // Still on the timeline: it stays in the project, and says why.
        vm.Media.RemoveCommand.Execute(null);
        Dialogs.Errors.Should().ContainSingle(e => e.Contains("logo.png"));
        vm.Project.Asset(clip.AssetId).Should().NotBeNull();
        vm.History.Undo();
        vm.Media.RemoveCommand.Execute(null);
        vm.Project.Asset(clip.AssetId).Should().BeNull();
    }

    [Fact]
    public async Task A_file_dropped_on_a_row_lands_there_and_at_that_time()
    {
        TestMedia.Require();
        using var vm = Open();
        var screen = vm.Project.Tracks.Single(t => t.Kind == TrackKind.Video);
        await vm.ImportFilesAsync([Png("a.png"), Png("b.png")], screen.End + 1, screen.Id);
        var added = vm.Project.Track(screen.Id)!.Clips.Where(c => c.IsFreeze).ToList();
        added.Should().HaveCount(2);
        added[0].Start.Should().Be(screen.End + 1);
        added[1].Start.Should().Be(added[0].End, "several files go one after another");
    }

    [Fact]
    public async Task A_file_that_cannot_be_read_says_so()
    {
        using var vm = Open();
        var path = Path.Combine(Root, "not.png");
        File.WriteAllText(path, "not a picture");
        await vm.ImportFilesAsync([path], 0, null);
        Dialogs.Errors.Should().ContainSingle(e => e.Contains("not.png"));
        vm.Project.Assets.Should().ContainSingle();
    }

    [Fact]
    public void A_colour_is_added_with_its_settings_in_the_panel()
    {
        TestMedia.Require();
        using var vm = Open();
        vm.Media.AddColourCommand.Execute(null);
        var clip = vm.Timeline.Selected.Should().BeOfType<Clip>().Subject;
        clip.IsSolid.Should().BeTrue();
        vm.Properties.OfType<ColorParameter>().First().Value = new ColorSwatch(SKColors.Purple);
        ((Clip)vm.Timeline.Selected!).Solid.Should().Be("#800080");
        var length = vm.Properties.OfType<NumberParameter>().First();
        length.Value = 2;
        vm.Timeline.Selected!.Duration.Should().Be(2);
        vm.History.Undo();
        vm.Timeline.Selected!.Duration.Should().Be(clip.Duration);
    }
}
