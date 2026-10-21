using System.IO;
using CashGear.Capture.App.Editor;
using CashGear.Capture.App.Tests.ImageTools;
using CashGear.Capture.App.VideoEditor;
using CashGear.Capture.Core.Instance;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Media.Tests;
using CashGear.Capture.Outputs;
using CashGear.Capture.VideoEditor.Export;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Playback;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace CashGear.Capture.App.Tests.VideoEditor;

/// <summary>Encoder detection for the tests: the software encoders, without probing the graphics card's.</summary>
internal sealed class SoftwareEncoders : IRecordingEncoders
{
    public string? Problem => null;

    public string? FfmpegVersion => "test";

    public IReadOnlyList<EncoderProbe>? Detected { get; } = [new(EncoderPresets.WindowsSoftwareH264.Id, true), new(EncoderPresets.OpenH264.Id, true)];

    public Task<IReadOnlyList<EncoderProbe>> DetectAsync(bool force = false, CancellationToken ct = default) => Task.FromResult(Detected!);
}

/// <summary>What the editor would ask the user: answers set by the test, questions noted.</summary>
internal sealed class FakeVideoDialogs : IVideoEditorDialogs
{
    public string? ProjectPath { get; set; }

    public string? Media { get; set; }

    public SaveChangesChoice Choice { get; set; } = SaveChangesChoice.Cancel;

    public List<string> Errors { get; } = [];

    public int SaveQuestions { get; private set; }

    public ExportViewModel? Export { get; private set; }

    public string? AskProjectPath(string suggestedName, string? folder) => ProjectPath;

    public string? PickMedia(string title, string filter, string? folder) => Media;

    public SaveChangesChoice AskSaveChanges(string name)
    {
        SaveQuestions++;
        return Choice;
    }

    public void ShowError(string message) => Errors.Add(message);

    public void ShowExport(ExportViewModel viewModel) => Export = viewModel;
}

/// <summary>A test folder with the services a video editor window gets, and a stamped recording.</summary>
public abstract class VideoEditorTestBase : IDisposable
{
    protected VideoEditorTestBase()
    {
        Root = Directory.CreateTempSubdirectory("cashgear-video-editor-").FullName;
        Paths = new AppPaths(Path.Combine(Root, "settings"), Path.Combine(Root, "data", "logs"), Path.Combine(Root, "captures"));
        State = new JsonAppStateStore(Paths, NullLogger<JsonAppStateStore>.Instance);
        Previews = new MediaPreviews(Path.Combine(Root, "cache"), NullLogger.Instance);
        Services = new VideoEditorServices(
            new ProjectExporter(new SoftwareEncoders(), NullLogger<ProjectExporter>.Instance),
            Previews,
            Settings,
            State,
            Paths,
            Shell,
            NullLoggerFactory.Instance,
            () => null); // no sound device: tests never play sound
    }

    protected string Root { get; }

    protected AppPaths Paths { get; }

    private protected MutableSettings Settings { get; } = new();

    protected JsonAppStateStore State { get; }

    private protected ImageToolTestBase.ShellSpy Shell { get; } = new();

    protected MediaPreviews Previews { get; }

    protected VideoEditorServices Services { get; }

    private protected FakeVideoDialogs Dialogs { get; } = new();

    public void Dispose()
    {
        Previews.Dispose();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // a decoder may still be closing a file
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>A stamped 4 s recording (320 × 180, 30 fps) with a microphone and a system audio track.</summary>
    protected string Recording(string name = "stamped.mp4")
    {
        TestMedia.Require();
        var path = Path.Combine(Root, "recordings", name);
        if (!File.Exists(path))
        {
            new StampedRecording { Path = path, Seconds = 4, Tracks = ["Microphone", "System audio"], ToneTracks = new HashSet<int> { 0, 1 } }.Write();
        }

        return path;
    }

    private protected VideoEditorViewModel Open(string? recording = null)
    {
        var project = ProjectFactory.FromRecording(ProjectFactory.ReadAsset(recording ?? Recording()));
        return new VideoEditorViewModel(project, null, Services, Dialogs);
    }

    /// <summary>Draws the preview at 320 × 180 until every picture is there; the stamp shown.</summary>
    private protected static int Shown(VideoEditorViewModel vm)
    {
        using var surface = SKSurface.Create(new SKImageInfo(320, 180, SKColorType.Bgra8888, SKAlphaType.Premul));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        vm.Tick();
        while (vm.RenderPreview(surface.Canvas, 320, 180, SKColors.Black) > 0 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(5);
            vm.Tick();
        }

        using var pixmap = surface.PeekPixels();
        return StampedRecording.ReadStamp(pixmap.GetPixelSpan(), 320);
    }
}

public sealed class VideoEditorTests : VideoEditorTestBase
{
    [Fact]
    public void A_recording_opens_with_its_tracks_and_shows_the_frame_at_the_playhead()
    {
        using var vm = Open();
        vm.Tracks.Select(t => (t.Name, t.Kind)).Should().Equal(("Video", "Picture"), ("Microphone", "Sound"), ("System audio", "Sound"));
        vm.Title.Should().Be("stamped — Video Editor");
        vm.TimeText.Should().Be("0:00.00 / 0:04.00");
        Shown(vm).Should().Be(0);

        vm.Seek(1);
        vm.TimeText.Should().Be("0:01.00 / 0:04.00");
        Shown(vm).Should().Be(30);
        vm.StepForwardCommand.Execute(null);
        Shown(vm).Should().Be(31);
        vm.GoToEndCommand.Execute(null);
        vm.Position.Should().Be(4);
    }

    [Fact]
    public void Splitting_cutting_out_a_range_and_undoing_show_in_the_title()
    {
        using var vm = Open();
        vm.Seek(1);
        vm.SplitCommand.Execute(null);
        vm.Project.Tracks.Should().OnlyContain(t => t.Clips.Count() == 2);
        vm.Title.Should().StartWith("• ");
        vm.UndoText.Should().Be("Undo split");

        vm.Timeline.SetRange((2, 3));
        vm.SelectionText.Should().StartWith("Range 0:02.00 – 0:03.00");
        vm.DeleteCommand.Execute(null);
        vm.Project.Duration.Should().BeApproximately(3, 1e-9);
        vm.Timeline.Range.Should().BeNull();
        vm.Position.Should().Be(2);
        Shown(vm).Should().Be(90, "after the cut, 2 s shows the source at 3 s");

        vm.UndoCommand.Execute(null);
        vm.UndoCommand.Execute(null);
        vm.IsDirty.Should().BeFalse();
        vm.Project.Tracks.Should().OnlyContain(t => t.Clips.Count() == 1);
        vm.RedoText.Should().Be("Redo split");
    }

    [Fact]
    public void The_selected_clip_is_deleted_with_its_linked_clips_and_ripple_delete_closes_the_gap()
    {
        using var vm = Open();
        vm.Seek(1);
        vm.SplitCommand.Execute(null);
        var first = vm.Project.Tracks[2].Clips.First();
        vm.Timeline.Select(first.Id);
        vm.SelectionText.Should().Contain("stamped.mp4: 0:00.00 – 0:01.00");
        vm.RippleDeleteCommand.Execute(null);
        vm.Project.Duration.Should().BeApproximately(3, 1e-9);
        vm.Project.Tracks.Should().OnlyContain(t => t.Clips.Single().Start == 0 && t.Clips.Single().SourceIn == 1);

        vm.Timeline.Select(vm.Project.Tracks[0].Clips.Single().Id);
        vm.DeleteCommand.Execute(null);
        vm.Project.Tracks.Should().OnlyContain(t => !t.Items.Any());
    }

    [Fact]
    public void Track_switches_are_edits_that_can_be_undone()
    {
        using var vm = Open();
        var microphone = vm.Tracks.Single(t => t.Name == "Microphone");
        microphone.IsMuted = true;
        vm.Project.Tracks.Single(t => t.Name == "Microphone").Muted.Should().BeTrue();
        vm.UndoText.Should().Be("Undo mute");
        vm.Tracks.Single(t => t.Name == "Microphone").IsMuted.Should().BeTrue("the headers follow the project");

        vm.Tracks.Single(t => t.Name == "Video").IsShown = false;
        vm.Project.Tracks.Single(t => t.Name == "Video").Hidden.Should().BeTrue();
        vm.UndoCommand.Execute(null);
        vm.UndoCommand.Execute(null);
        vm.Project.Tracks.Should().OnlyContain(t => !t.Muted && !t.Hidden);
    }

    [Fact]
    public async Task Saving_writes_the_project_and_closing_asks_only_about_unsaved_changes()
    {
        using var vm = Open();
        Dialogs.ProjectPath = Path.Combine(Root, "projects", "Edit.cgcap");
        vm.Seek(1);
        vm.SplitCommand.Execute(null);
        (await vm.ConfirmCloseAsync()).Should().BeFalse("the dialog was cancelled");
        Dialogs.SaveQuestions.Should().Be(1);

        await vm.SaveProjectCommand.ExecuteAsync(null);
        File.Exists(Dialogs.ProjectPath).Should().BeTrue();
        vm.IsDirty.Should().BeFalse();
        vm.Title.Should().Be("Edit — Video Editor");
        vm.ProjectPath.Should().Be(Dialogs.ProjectPath);
        State.Current.VideoEditor!.RecentProjects.Should().Equal(Dialogs.ProjectPath);
        (await vm.ConfirmCloseAsync()).Should().BeTrue();
        Dialogs.SaveQuestions.Should().Be(1, "nothing to ask about");

        var reopened = ProjectFile.Open(Dialogs.ProjectPath, Path.Combine(Root, "unpacked"));
        reopened.Missing.Should().BeEmpty();
        reopened.Project.Tracks.Should().OnlyContain(t => t.Clips.Count() == 2);

        vm.SplitCommand.Execute(null);
        vm.Seek(2);
        vm.SplitCommand.Execute(null);
        Dialogs.Choice = SaveChangesChoice.Discard;
        (await vm.ConfirmCloseAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task Exporting_makes_the_file_beside_others_and_remembers_the_choices()
    {
        using var vm = Open();
        vm.Seek(1);
        vm.SplitCommand.Execute(null);
        vm.Timeline.SetRange((1, 2));
        vm.DeleteCommand.Execute(null);
        vm.ExportCommand.Execute(null);
        var export = Dialogs.Export;
        export.Should().NotBeNull();
        export!.SelectedPreset.Should().Be(ExportPresets.Mp4H264Hd, "the first time");
        export.SelectedPreset = ExportPresets.Mp4H264Project;
        export.Quality = export.Qualities.Single(q => q.Value == QualityPreset.Balanced);
        export.SizeText.Should().Be("320 × 180, 30 fps, 0:03 long");
        export.Path.Should().Be(Path.Combine(Paths.DefaultCaptureDirectory, "stamped.mp4"));
        Directory.CreateDirectory(Paths.DefaultCaptureDirectory);
        File.WriteAllText(export.Path, "taken");

        await export.StartCommand.ExecuteAsync(null);
        export.Error.Should().BeNull();
        export.Result.Should().Be(Path.Combine(Paths.DefaultCaptureDirectory, "stamped (2).mp4"), "an existing file is never replaced without asking");
        export.IsDone.Should().BeTrue();
        export.ProgressText.Should().StartWith("Done in");
        File.ReadAllText(Path.Combine(Paths.DefaultCaptureDirectory, "stamped.mp4")).Should().Be("taken");
        using (var reader = new Media.MediaReader(export.Result!))
        {
            reader.Duration.Should().BeApproximately(3, 0.05);
        }

        export.OpenCommand.Execute(null);
        Shell.Opened.Should().Equal(export.Result);
        State.Current.VideoEditor!.ExportPreset.Should().Be("mp4");
        State.Current.VideoEditor.ExportQuality.Should().Be(QualityPreset.Balanced);

        vm.ExportCommand.Execute(null);
        Dialogs.Export!.SelectedPreset.Should().Be(ExportPresets.Mp4H264Project, "remembered");
        Dialogs.Export.Path.Should().EndWith(".mp4");
        Dialogs.Export.SelectedPreset = ExportPresets.Mp3;
        Dialogs.Export.Path.Should().EndWith("stamped.mp3");
        Dialogs.Export.SizeText.Should().StartWith("Sound only");
    }

    [Fact]
    public async Task Another_recording_is_added_at_the_end()
    {
        using var vm = Open();
        Dialogs.Media = Recording("second.mp4");
        await vm.AddRecordingCommand.ExecuteAsync(null);
        vm.Project.Duration.Should().BeApproximately(8, 0.05);
        vm.Project.Tracks.Should().HaveCount(6);
        vm.UndoText.Should().Be("Undo add recording");
    }

    [Fact]
    public async Task The_recording_workflow_can_open_the_video_editor()
    {
        var launcher = new Launcher();
        var destination = new VideoEditorDestination(launcher);
        destination.SupportsVideo.Should().BeTrue();
        using (var video = OutputItem.ForVideo(@"C:\recordings\Rec.mp4", DateTimeOffset.Now))
        {
            (await destination.SendAsync(video, CancellationToken.None)).Succeeded.Should().BeTrue();
        }

        launcher.Opened.Should().Equal(@"C:\recordings\Rec.mp4");
        DestinationIds.VideoEditor.Should().Be("video-editor");
        InstanceCommandParser.FromArgs(["--video-editor"]).Should().Be(InstanceCommand.VideoEditor);
    }

    private sealed class Launcher : IVideoEditorLauncher
    {
        public List<string> Opened { get; } = [];

        public Task OpenAsync(string path, CancellationToken ct = default)
        {
            Opened.Add(path);
            return Task.CompletedTask;
        }
    }
}
