using System.IO;
using CashGear.Capture.App.VideoEditor;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Media;
using CashGear.Capture.Media.Tests;
using CashGear.Capture.Outputs;
using CashGear.Capture.VideoEditor.Export;

namespace CashGear.Capture.App.Tests.VideoEditor;

public sealed class GifEditorTests : VideoEditorTestBase
{
    [Fact]
    public async Task The_export_window_makes_a_gif_with_its_choices_and_remembers_them()
    {
        using var vm = Open();
        vm.ExportCommand.Execute(null);
        var export = Dialogs.Export!;
        export.SelectedPreset = ExportPresets.Gif;
        (export.IsGif, export.HasQuality).Should().Be((true, false));
        export.Path.Should().EndWith(".gif");
        export.GifWidths.Should().Equal([320], "never wider than the project");
        export.GifFps = 10;
        export.GifLoop = false;
        export.GifDithering = export.GifDithers.Single(d => d.Value == GifDither.None);
        export.SizeText.Should().Be("320 × 180, 10 frames a second, 0:04 long, no sound, plays once");

        await export.StartCommand.ExecuteAsync(null);
        export.Error.Should().BeNull();
        var gif = GifProbe.Of(export.Result!);
        gif.Should().Match<GifProbe>(p => p.Frames == 40 && p.Width == 320 && !p.Loops);
        State.Current.VideoEditor!.Should().Match<VideoEditorState>(s => s.ExportPreset == "gif" && s.GifFps == 10 && !s.GifLoop && s.GifDither == "None");

        // The next window starts from them.
        vm.ExportCommand.Execute(null);
        var again = Dialogs.Export!;
        (again.IsGif, again.GifFps, again.GifLoop, again.GifDithering.Value).Should().Be((true, 10, false, GifDither.None));
    }

    [Fact]
    public async Task A_recording_can_be_made_into_a_gif_by_the_recording_workflow()
    {
        var recording = Recording("workflow.mp4");
        await State.UpdateAsync(s => s with { VideoEditor = new VideoEditorState { GifFps = 10, GifWidth = 160 } }, TestContext.Current.CancellationToken);
        var destination = new GifDestination(Services.Exporter, TestMedia.Require(), State);
        (destination.Id, destination.DisplayName, destination.SupportsVideo).Should().Be(("gif", "Make a GIF", true));

        using (var video = OutputItem.ForVideo(recording, DateTimeOffset.Now))
        {
            var result = await destination.SendAsync(video, TestContext.Current.CancellationToken);
            result.Succeeded.Should().BeTrue(result.Message);
            result.Path.Should().Be(Path.ChangeExtension(recording, ".gif"));
            GifProbe.Of(result.Path!).Should().Match<GifProbe>(p => p.Width == 160 && p.Frames == 40 && p.Loops);
        }

        using (var again = OutputItem.ForVideo(recording, DateTimeOffset.Now))
        {
            (await destination.SendAsync(again, TestContext.Current.CancellationToken)).Path.Should().EndWith("workflow (2).gif", "the first is kept");
        }
    }
}
