using System.IO;
using System.Security.Cryptography;
using CashGear.Capture.App.Output;
using CashGear.Capture.App.Settings;
using CashGear.Capture.App.Tray;
using CashGear.Capture.App.VideoEditor;
using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Editor.Presets;
using CashGear.Capture.Licensing;
using CashGear.Capture.Media;
using CashGear.Capture.Media.Tests;
using CashGear.Capture.Outputs;
using CashGear.Capture.Outputs.Credentials;
using CashGear.Capture.Outputs.Sharing;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Export;
using CashGear.Capture.VideoEditor.Model;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.App.Tests.VideoEditor;

/// <summary>The export window's presets and sending, batches, thumbnails, the background window and the sign-in rows.</summary>
public sealed class SharingEditorTests : VideoEditorTestBase
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ProjectExporter Exporter => new(new SoftwareEncoders(), NullLogger<ProjectExporter>.Instance);

    private ExportViewModel Window(VideoProject project, IExportSender? sender = null, IPresetStore? presets = null, IFeatureGate? gate = null, TimeRange? range = null) =>
        new(project, "My video", Root, Exporter, Settings, State, Shell, NullLogger.Instance, range, gate, presets, sender);

    private ExportQueue Queue(FakeNotifier? notifier = null, IExportSender? sender = null, IFeatureGate? gate = null) =>
        new(Exporter, Settings, notifier ?? new FakeNotifier(), Shell, Paths, NullLogger<ExportQueue>.Instance, gate, sender);

    private VideoProject Project(params (double Time, string Name)[] markers)
    {
        var project = ProjectFactory.FromRecording(ProjectFactory.ReadAsset(Recording()));
        return markers.Length == 0 ? project : project with { Markers = [.. markers.Select((m, i) => new TimelineMarker($"m{i}", m.Time, m.Name))] };
    }

    /// <summary>Every picture of a video, hashed: two files with the same pictures say so whatever else is in them.</summary>
    private static List<string> PictureHashes(string path)
    {
        using var reader = new VideoStreamReader(path, decoding: VideoDecoding.Software);
        var pixels = new byte[reader.Width * reader.Height * 4];
        var hashes = new List<string>();
        while (reader.Read(pixels, reader.Width, reader.Height) is not null)
        {
            hashes.Add(Convert.ToHexString(MD5.HashData(pixels)));
        }

        return hashes;
    }

    private async Task<ExportViewModel> ExportAlone(VideoProject project, NamedExport preset, string path)
    {
        var window = Window(project);
        window.Apply(preset.Choices);
        window.Path = path;
        await window.StartCommand.ExecuteAsync(null);
        window.Error.Should().BeNull();
        window.Result.Should().Be(path);
        return window;
    }

    [Fact]
    public async Task Three_presets_of_one_project_export_in_one_go_each_the_same_as_a_single_export()
    {
        TestMedia.Require();
        var project = Project((0, "One"), (2, "Two"));
        var queue = Queue();
        var presets = BuiltInExports.All;
        presets.Should().HaveCount(3);
        var folder = Path.Combine(Root, "batch");
        var alone = Path.Combine(Root, "alone");
        Directory.CreateDirectory(alone);

        await CashGear.Capture.Tests.MachineTurn.RunAsync(async () => // the 1080p and vertical pictures work every core
        {
            queue.Add(new BatchSource("My video", null, project), presets, folder);
            await queue.WhenIdleAsync(Ct);
            foreach (var preset in presets)
            {
                await ExportAlone(project, preset, Path.Combine(alone, preset.Id + (ExportPresets.Find(preset.Choices.Preset)!.Extension)));
            }

            return 0;
        });

        queue.Jobs.Should().HaveCount(3).And.OnlyContain(j => j.State == ExportJobState.Done);
        foreach (var (job, preset) in queue.Jobs.Zip(presets))
        {
            Path.GetFileName(job.Path).Should().StartWith($"My video - {preset.Name.Replace(':', '_')}", "named for what it is, in what a file's name can hold");
            var single = Path.Combine(alone, preset.Id + Path.GetExtension(job.Path));
            new FileInfo(job.Path).Length.Should().BeGreaterThan(0);
            if (Path.GetExtension(job.Path) == ".gif")
            {
                File.ReadAllBytes(job.Path).Should().Equal(File.ReadAllBytes(single), "the GIF is made the same way byte for byte");
            }
            else
            {
                var pictures = PictureHashes(job.Path);
                pictures.Should().NotBeEmpty();
                pictures.Should().Equal(PictureHashes(single), "{0} makes the same pictures from a batch", preset.Name);
                Sound(job.Path).Should().Equal(Sound(single), "and the same sound");
            }
        }

        static List<float> Sound(string path)
        {
            using var reader = new AudioStreamReader(path);
            var block = new float[96_000];
            var all = new List<float>();
            int read;
            while ((read = reader.Read(block)) > 0)
            {
                all.AddRange(block.Take(read * 2));
            }

            return all;
        }
    }

    [Fact]
    public async Task A_queue_of_projects_goes_one_after_another_and_one_failing_doesnt_stop_the_others()
    {
        TestMedia.Require();
        var good = Recording();
        var bad = Path.Combine(Root, "broken.cgcap");
        File.WriteAllText(bad, "not a project");
        var notifier = new FakeNotifier();
        var queue = Queue(notifier);
        var preset = new NamedExport("small", "Small", new ExportChoices(ExportPresets.Mp4H264Small.Id, QualityPreset.Small, 0));
        var folder = Path.Combine(Root, "out");

        queue.Add(new BatchSource("broken", bad), [preset], folder);
        queue.Add(new BatchSource("stamped", good), [preset], folder);
        await queue.WhenIdleAsync(Ct);

        queue.Jobs.Select(j => j.State).Should().Equal(ExportJobState.Failed, ExportJobState.Done);
        File.Exists(queue.Jobs[1].Path).Should().BeTrue();
        var (title, text, kind) = notifier.Messages.Single();
        title.Should().Be("Exports: not everything worked");
        text.Should().Contain("1 file made");
        kind.Should().Be(NotificationKind.Warning);
    }

    [Fact]
    public async Task A_batchs_names_never_overwrite_and_cancelling_leaves_no_file()
    {
        TestMedia.Require();
        var project = Project();
        var preset = new NamedExport("small", "Small", new ExportChoices(ExportPresets.Mp4H264Small.Id, QualityPreset.Small, 0));
        var folder = Path.Combine(Root, "names");
        Directory.CreateDirectory(folder);
        var taken = Path.Combine(folder, "A - Small.mp4");
        File.WriteAllText(taken, "mine");
        var queue = Queue();

        queue.Add(new BatchSource("A", null, project), [preset, preset], folder);
        queue.Cancel(queue.Jobs[1].Id);
        queue.Cancel(queue.Jobs[0].Id);
        await queue.WhenIdleAsync(Ct);

        File.ReadAllText(taken).Should().Be("mine");
        queue.Jobs.Select(j => Path.GetFileName(j.Path)).Should().Equal("A - Small (2).mp4", "A - Small (3).mp4");
        queue.Jobs.Should().OnlyContain(j => j.State == ExportJobState.Cancelled);
        Directory.GetFiles(folder).Should().ContainSingle("nothing half made");
    }

    [Fact]
    public async Task A_cancelled_export_can_be_tried_again_and_ended_ones_cleared()
    {
        TestMedia.Require();
        var project = Project();
        var preset = new NamedExport("small", "Small", new ExportChoices(ExportPresets.Mp4H264Small.Id, QualityPreset.Small, 0));
        var queue = Queue();
        queue.Add(new BatchSource("A", null, project), [preset], Path.Combine(Root, "again"));
        queue.Cancel(queue.Jobs[0].Id);
        await queue.WhenIdleAsync(Ct);

        queue.Retry(queue.Jobs[0].Id);
        await queue.WhenIdleAsync(Ct);

        queue.Jobs.Single().State.Should().Be(ExportJobState.Done);
        queue.ClearEnded();
        queue.Jobs.Should().BeEmpty();
    }

    [Fact]
    public async Task A_preset_with_a_destination_sends_what_it_made_with_the_chapters()
    {
        TestMedia.Require();
        var sender = new Sender();
        var queue = Queue(sender: sender);
        var preset = new NamedExport("send", "Send", new ExportChoices(ExportPresets.Mp4H264Small.Id, QualityPreset.Small, 0, SendTo: DestinationIds.YouTube));

        queue.Add(new BatchSource("A", null, Project((0, "One"), (2, "Two"))), [preset], Path.Combine(Root, "send"));
        await queue.WhenIdleAsync(Ct);

        var sent = sender.Sent.Single();
        sent.Destination.Should().Be(DestinationIds.YouTube);
        File.Exists(sent.Path).Should().BeTrue();
        sent.Chapters.Select(c => c.Title).Should().Equal("One", "Two");
    }

    [Fact]
    public void Batches_are_behind_the_gate_and_free_in_this_version()
    {
        FeatureGate.Open.IsEnabled(ProFeature.BatchExport).Should().BeTrue();
        FeaturePolicy.AllFree.Should().NotBeNull();
        var queue = Queue(gate: FeatureGate.Closed);

        queue.IsAllowed.Should().BeFalse();
        queue.Add(new BatchSource("A", null, VideoProject.Empty(new ProjectCanvas(320, 180, 30))), BuiltInExports.All, Root);
        queue.Jobs.Should().BeEmpty();

        Queue().IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void Single_exports_with_presets_stay_free()
    {
        var window = Window(VideoProject.Empty(new ProjectCanvas(320, 180, 30)), gate: FeatureGate.Closed);

        window.NamedExports.Should().NotBeEmpty();
    }

    [Fact]
    public void The_batch_window_makes_each_project_in_each_ticked_preset()
    {
        var queue = Queue();
        var project = new BatchSource("One", null, VideoProject.Empty(new ProjectCanvas(320, 180, 30)));
        var pickers = new Pickers { Files = [Path.Combine(Root, "two.mp4"), Path.Combine(Root, "three.cgcap")], Folder = Path.Combine(Root, "picked") };
        var window = new BatchExportViewModel(queue, null, [project], Root, pickers);

        window.Presets.Should().HaveCountGreaterThanOrEqualTo(3);
        window.StartCommand.CanExecute(null).Should().BeFalse("no preset is ticked");
        window.AddProjectsCommand.Execute(null);
        window.AddProjectsCommand.Execute(null);
        window.Projects.Select(p => p.Name).Should().Equal("One", "two", "three");
        window.SelectedProject = window.Projects[2];
        window.RemoveProjectCommand.Execute(null);
        window.Presets[0].IsChosen = true;
        window.Presets[2].IsChosen = true;
        window.CountText.Should().Contain("4");
        window.BrowseCommand.Execute(null);
        window.Folder.Should().Be(pickers.Folder);

        var closed = false;
        window.CloseRequested += (_, _) => closed = true;
        var started = false;
        window.Started += (_, _) => started = true;
        window.StartCommand.CanExecute(null).Should().BeTrue();

        window.StartCommand.Execute(null);

        started.Should().BeTrue();
        closed.Should().BeTrue();
        queue.Jobs.Should().HaveCount(4);
        queue.Stop();
    }

    [Fact]
    public async Task A_preset_sets_every_choice_and_saved_ones_come_back_by_name()
    {
        var presets = new MemoryPresets();
        var project = VideoProject.Empty(new ProjectCanvas(1920, 1080, 30));
        var window = Window(project, presets: presets);
        window.AskName = (_, _) => "Mine";

        window.SelectedNamedExport = window.NamedExports.Single(e => e.Name == "Docs GIF");

        window.SelectedPreset.Should().Be(ExportPresets.Gif);
        window.GifFps.Should().Be(12);
        window.GifLimit.Megabytes.Should().Be(10);
        window.Path.Should().EndWith(".gif");

        window.SelectedNamedExport = window.NamedExports.Single(e => e.Name == "Shorts 9:16");
        window.SelectedPreset.Should().Be(ExportPresets.Vertical);
        window.Loudness.Lufs.Should().Be(-14);

        window.Quality = window.Qualities[0];
        window.BurnCaptions = false;
        await window.SaveNamedExportCommand.ExecuteAsync(null);

        var saved = presets.Current.SavedExports.Should().ContainSingle().Subject;
        saved.Name.Should().Be("Mine");
        saved.Choices.Preset.Should().Be(ExportPresets.Vertical.Id);
        saved.Choices.Quality.Should().Be(QualityPreset.Small);
        saved.Choices.BurnCaptions.Should().BeFalse();
        window.SelectedNamedExport!.Name.Should().Be("Mine");

        var again = Window(project, presets: presets);
        again.NamedExports.Select(e => e.Name).Should().Contain("Mine");
        again.SelectedNamedExport = again.NamedExports.Single(e => e.Name == "Mine");
        again.SelectedPreset.Should().Be(ExportPresets.Vertical);
        again.Quality.Value.Should().Be(QualityPreset.Small);
        again.BurnCaptions.Should().BeFalse();

        again.DeleteNamedExportCommand.CanExecute(null).Should().BeTrue();
        await again.DeleteNamedExportCommand.ExecuteAsync(null);
        presets.Current.SavedExports.Should().BeEmpty();

        again.SelectedNamedExport = again.NamedExports[0];
        again.DeleteNamedExportCommand.CanExecute(null).Should().BeFalse("those that come with the app stay");
    }

    [Fact]
    public void An_HDR_preset_chosen_for_a_project_without_HDR_media_says_what_it_did_instead()
    {
        var window = Window(VideoProject.Empty(new ProjectCanvas(1920, 1080, 30)));

        window.Apply(new ExportChoices(ExportPresets.Mp4HevcHdr.Id));

        window.SelectedPreset.Should().Be(ExportPresets.Mp4Hevc);
        window.Error.Should().Contain("no HDR media");
    }

    [Fact]
    public async Task An_export_goes_on_to_YouTube_with_what_was_typed_and_the_chapters_times()
    {
        TestMedia.Require();
        var sender = new Sender();
        var window = Window(Project((0, "One"), (2, "Two")), sender);
        window.Quality = window.Qualities[0];
        window.SelectedPreset = ExportPresets.Mp4H264Small;
        window.Path = Path.Combine(Root, "sent.mp4");
        window.SendTargets.Select(t => t.Id).Should().Equal(null, DestinationIds.YouTube, "drive");
        window.IsYouTube.Should().BeFalse();

        window.SendTo = window.SendTargets[1];

        window.IsYouTube.Should().BeTrue();
        window.YouTubeTitle.Should().Be("My video");
        window.YouTubeDescription.Should().Be("From settings");
        window.YouTubeTags.Should().Be("one, two");
        window.Privacy.Value.Should().Be(VideoPrivacy.Unlisted);
        window.ChaptersPreview.Should().Be("0:00 One\n0:02 Two");
        window.ChaptersNote.Should().Contain("at least 3");
        await WaitForAsync(() => window.Playlists.Any(p => p.Name == "Tutorials"));
        window.Playlist = window.Playlists.Single(p => p.Name == "Tutorials");
        window.YouTubeTitle = "Typed";
        window.YouTubeTags = "a, b c,";
        window.Privacy = window.Privacies.Single(p => p.Value == VideoPrivacy.Public);
        window.PickThumbnail = () => "thumb.png";
        window.ChooseThumbnailCommand.Execute(null);
        window.ThumbnailText.Should().Be("thumb.png");

        await window.StartCommand.ExecuteAsync(null);

        window.Error.Should().BeNull();
        window.ProgressText.Should().Contain("Going to YouTube");
        var sent = sender.Sent.Single();
        sent.Destination.Should().Be(DestinationIds.YouTube);
        sent.Path.Should().Be(window.Result);
        sent.Name.Should().Be("My video");
        sent.Chapters.Select(c => c.Title).Should().Equal("One", "Two");
        sent.Details.Should().NotBeNull();
        sent.Details!.Title.Should().Be("Typed");
        sent.Details.Tags.Should().Equal("a", "b c");
        sent.Details.Privacy.Should().Be(VideoPrivacy.Public);
        sent.Details.Playlist.Should().Be("PL1");
        sent.Details.ThumbnailPath.Should().Be("thumb.png");
        State.Current.VideoEditor!.ExportSendTo.Should().Be(DestinationIds.YouTube);
    }

    [Fact]
    public async Task The_real_sender_puts_the_times_in_the_description_and_hands_the_file_to_the_queue()
    {
        var registry = new DestinationRegistry([new OneDestination()], Settings, new NoCredentials());
        using var wake = new UploadWake();
        using var queue = new UploadQueue(registry, new NoCredentials(), Paths, wake, NullLogger<UploadQueue>.Instance);
        Settings.Current = (Settings.Current with
        {
            Destinations = new DestinationSettings(Sharing: new SharingSettings(new YouTubeSettings(Description: "About it"))),
        }).Normalize();
        var sender = new ExportSender(new Lazy<DestinationRegistry>(() => registry), new Lazy<UploadQueue>(() => queue), Settings);
        var file = Path.Combine(Root, "a.mp4");
        File.WriteAllBytes(file, new byte[10]);

        sender.Send(file, "Name", DestinationIds.YouTube, [new ExportChapter(0, 5, "One"), new ExportChapter(5, 9, "Two")]);
        await queue.WhenIdleAsync(Ct);

        var sent = OneDestination.Last!;
        sent.Should().Be(file);
        queue.Jobs.Single().State.Should().Be(UploadState.Done);
        sender.Targets().Should().ContainSingle().Which.Id.Should().Be(DestinationIds.YouTube);
    }

    [Fact]
    public Task A_thumbnail_is_made_with_a_title_and_saved_or_used_for_the_upload() => StaDispatcher.RunAsync(async () =>
    {
        TestMedia.Require();
        var project = Project();
        var save = Path.Combine(Root, "chosen.png");
        Dialogs.SavePath = save;
        using var thumbnail = new ThumbnailViewModel(project, 1, new ThumbnailMaker(), BuiltInTemplates.All, null, Dialogs, "My video", Path.Combine(Root, "work"), forUpload: true);

        await thumbnail.WhenDrawnAsync();
        thumbnail.Preview.Should().NotBeNull();
        thumbnail.Titles[0].Template.Should().BeNull();
        thumbnail.Titles.Should().NotContain(t => t.Template != null && t.Template.Kind == TemplateKind.Callout);
        thumbnail.HasFields.Should().BeFalse();
        var plain = thumbnail.Preview!;

        thumbnail.SelectedTitle = thumbnail.Titles[1];
        thumbnail.HasFields.Should().BeTrue();
        thumbnail.Fields[0].Text = "عنوان";
        thumbnail.Time = 2;
        thumbnail.TimeText.Should().Contain("0:02");
        await thumbnail.WhenDrawnAsync();
        thumbnail.Preview.Should().NotBeSameAs(plain);

        thumbnail.SaveAsCommand.Execute(null);
        thumbnail.Failed.Should().BeFalse(thumbnail.Status);
        File.Exists(save).Should().BeTrue();
        thumbnail.Status.Should().Contain("chosen.png");

        var closed = false;
        thumbnail.CloseRequested += (_, _) => closed = true;
        thumbnail.UseForUploadCommand.Execute(null);

        closed.Should().BeTrue();
        File.Exists(thumbnail.Chosen).Should().BeTrue();
        Path.GetFileName(thumbnail.Chosen).Should().Be("My video.png");
    });

    [Fact]
    public async Task The_editors_export_offers_where_the_file_can_go_and_a_thumbnail()
    {
        TestMedia.Require();
        var sender = new Sender();
        using var vm = new VideoEditorViewModel(Project((0, "One")), null, Services with { Sender = sender, CanBatch = true, ShowBatch = source => Batched = source }, Dialogs);

        vm.ExportCommand.Execute(null);
        Dialogs.Export!.CanSend.Should().BeTrue();
        Dialogs.Export!.HasMarkers.Should().BeTrue();
        Dialogs.Export!.ChaptersInFile.Should().BeTrue();
        Dialogs.Export!.PickThumbnail.Should().NotBeNull();

        vm.CanBatchExport.Should().BeTrue();
        vm.BatchExportCommand.Execute(null);
        Batched!.Project.Should().BeSameAs(vm.Project);

        await Task.CompletedTask;
    }

    private BatchSource? Batched { get; set; }

    [Fact]
    public async Task The_window_lists_what_is_going_on_and_can_cancel_retry_and_copy_the_link()
    {
        var registry = new DestinationRegistry([new OneDestination()], Settings, new NoCredentials());
        using var wake = new UploadWake();
        using var uploads = new UploadQueue(registry, new NoCredentials(), Paths, wake, NullLogger<UploadQueue>.Instance);
        var exports = Queue();
        var outputs = new TextOutput();
        var workflow = new OutputWorkflow(new WorkflowRunner(registry, NullLogger<WorkflowRunner>.Instance), registry, Settings, outputs, Paths, new NoCounter(), new FakeNotifier(), Shell, NullLogger<OutputWorkflow>.Instance, uploads);
        var work = new BackgroundWork(uploads, exports, workflow);
        await work.StartAsync(Ct);
        using var window = new BackgroundWorkViewModel(work, new InlineUiDispatcher(), outputs, Shell);

        window.IsEmpty.Should().BeTrue();
        window.SummaryText.Should().Be("Nothing is going on.");
        work.Summary.IsBusy.Should().BeFalse();

        var file = Path.Combine(Root, "a.mp4");
        File.WriteAllBytes(file, new byte[10]);
        OneDestination.Gate = new TaskCompletionSource();
        uploads.Enqueue(new UploadRequest(file, false, "a", null, DateTimeOffset.UtcNow, UploadKind.Recording, [new UploadStep(DestinationIds.YouTube)]));
        window.Refresh();

        var row = window.Rows.Should().ContainSingle().Subject;
        window.IsEmpty.Should().BeFalse();
        row.Name.Should().Be("a.mp4");
        row.Where.Should().Be("One");
        row.CanCancel.Should().BeTrue();
        row.CanRemove.Should().BeFalse();
        row.AccessibleName.Should().Contain("a.mp4").And.Contain("One");
        window.SummaryText.Should().Contain("1 going on");
        work.Summary.IsBusy.Should().BeTrue();

        OneDestination.Gate.SetResult();
        await uploads.WhenIdleAsync(Ct);
        window.Refresh();

        row.CanCancel.Should().BeFalse();
        row.CanRemove.Should().BeTrue();
        row.HasLink.Should().BeTrue();
        row.Status.Should().Contain(row.Link);
        await row.CopyLinkCommand.ExecuteAsync(null);
        outputs.Texts.Should().Equal(row.Link);
        row.OpenLinkCommand.Execute(null);
        Shell.Links.Should().Contain(row.Link);

        row.RemoveCommand.Execute(null);
        window.Rows.Should().BeEmpty();
        await work.StopAsync(Ct);
    }

    [Fact]
    public async Task Settings_signs_in_and_out_and_keeps_what_was_typed()
    {
        var sign = new FakeSignIn();
        var settings = new DestinationSettings(Sharing: new SharingSettings(new YouTubeSettings(VideoPrivacy.Unlisted, "About", ["one"], "PL1", true), new CloudFolderSettings("Work", true))).Normalize();
        var destinations = new DestinationsViewModel(null, null, null) { SignIns = id => id == DestinationIds.YouTube ? sign : null };

        destinations.Load(settings);

        destinations.NoSharing.Should().BeFalse();
        var youtube = destinations.Sharing[0];
        youtube.IsAvailable.Should().BeTrue();
        destinations.Sharing[1].IsAvailable.Should().BeFalse("no registration");
        youtube.IsSignedOut.Should().BeTrue();
        youtube.AccountText.Should().Be("Not signed in");
        youtube.Privacy.Should().Be(VideoPrivacy.Unlisted);
        youtube.Tags.Should().Be("one");
        destinations.Sharing[1].Folder.Should().Be("Work");
        destinations.Sharing[1].AnyoneWithLink.Should().BeTrue();

        await youtube.SignInCommand.ExecuteAsync(null);

        youtube.IsSignedIn.Should().BeTrue();
        youtube.AccountText.Should().Be("Signed in as me@example.test");

        youtube.SignOutCommand.Execute(null);
        youtube.IsSignedIn.Should().BeFalse();
        sign.SignedOut.Should().BeTrue();

        sign.Failure = new SignInRequiredException("No way");
        await youtube.SignInCommand.ExecuteAsync(null);
        youtube.TestFailed.Should().BeTrue();
        youtube.TestStatus.Should().Be("No way");

        youtube.Tags = "x, y";
        var built = destinations.Build(settings);
        built.Sharing!.YouTube!.Tags.Should().Equal("x", "y");
        built.Sharing.YouTube.MadeForKids.Should().BeTrue();
        built.Sharing.GoogleDrive!.Folder.Should().Be("Work");
    }

    [Fact]
    public async Task Waiting_for_the_browser_can_be_cancelled()
    {
        var sign = new FakeSignIn { Hang = true };
        var destinations = new DestinationsViewModel(null, null, null) { SignIns = _ => sign };
        var row = destinations.Sharing[2];

        var signing = row.SignInCommand.ExecuteAsync(null);
        row.IsWaiting.Should().BeTrue();
        row.AccountText.Should().Contain("browser");
        row.CancelSignInCommand.Execute(null);
        await signing;

        row.IsWaiting.Should().BeFalse();
        row.TestFailed.Should().BeFalse();
    }

    [Fact]
    public void A_build_without_registrations_says_so()
    {
        var destinations = new DestinationsViewModel(null, null, null) { SignIns = _ => null };
        destinations.Load(new DestinationSettings().Normalize());

        destinations.NoSharing.Should().BeTrue();
    }

    [Fact]
    public void A_diagnostics_zip_doesnt_say_where_things_go_or_what_the_clients_are()
    {
        var json = App.Diagnostics.DiagnosticsRedaction.Settings("""
            { "destinations": { "sharing": {
                "youTube": { "privacy": "Private", "description": "secret plans", "tags": ["client x"], "playlist": "PLsecret" },
                "googleDrive": { "folder": "Clients/Acme", "anyoneWithLink": false },
                "oneDrive": { "folder": "Clients/Acme" },
                "dropbox": { "folder": "Clients/Acme" },
                "clients": { "google": "gid", "googleSecret": "gsecret", "microsoft": "mid", "dropbox": "did" }
            } } }
            """);

        json.Should().NotContain("secret").And.NotContain("Acme").And.NotContain("client x").And.NotContain("gid").And.NotContain("mid").And.NotContain("did");
        json.Should().Contain("Private", "what isn't personal stays, to help");
    }

    [Fact]
    public void The_tray_says_how_far_the_background_work_has_got() =>
        new BackgroundSummary(2, 0.5).IsBusy.Should().BeTrue();

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, Ct);
        }

        condition().Should().BeTrue();
    }

    private sealed class Pickers : IBatchPickers
    {
        public IReadOnlyList<string> Files { get; init; } = [];

        public string? Folder { get; init; }

        public IReadOnlyList<string> PickProjects() => Files;

        public string? PickFolder(string current) => Folder;
    }

    private sealed class Sender : IExportSender
    {
        public List<(string Path, string Name, string Destination, IReadOnlyList<ExportChapter> Chapters, UploadDetails? Details)> Sent { get; } = [];

        public YouTubeSettings YouTubeDefaults { get; } = new(VideoPrivacy.Unlisted, "From settings", ["one", "two"]);

        public IReadOnlyList<(string Id, string Name)> Targets() => [(DestinationIds.YouTube, "YouTube"), ("drive", "Drive")];

        public void Send(string path, string name, string destination, IReadOnlyList<ExportChapter> chapters, UploadDetails? details = null) => Sent.Add((path, name, destination, chapters, details));

        public Task<IReadOnlyList<(string Id, string Title)>> PlaylistsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<(string, string)>>([("PL1", "Tutorials")]);
    }

    private sealed class MemoryPresets : IPresetStore
    {
        public PresetLibrary Current { get; private set; } = new PresetLibrary().Normalize();

        public event EventHandler? Changed;

        public Task UpdateAsync(Func<PresetLibrary, PresetLibrary> update, CancellationToken ct = default)
        {
            Current = update(Current).Normalize();
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
    }

    private sealed class OneDestination : IDestinationSource, IOutputDestination
    {
        public static string? Last { get; private set; }

        public static TaskCompletionSource? Gate { get; set; }

        public string Id => DestinationIds.YouTube;

        public string DisplayName => "One";

        public bool SupportsVideo => true;

        public bool IsUpload => true;

        public IEnumerable<IOutputDestination> Create(AppSettings settings, ICredentialStore credentials) => [this];

        public async Task<OutputResult> SendAsync(OutputItem item, CancellationToken ct)
        {
            if (Gate is { } gate)
            {
                await gate.Task.WaitAsync(ct);
            }

            Last = await item.GetFileAsync(ct);
            return new OutputResult(true, "Sent") { Link = "https://a.test/" + Path.GetFileName(Last) };
        }

        public Task<OutputResult> TestAsync(CancellationToken ct) => Task.FromResult(OutputResult.Done("ok"));
    }

    private sealed class NoCredentials : ICredentialStore
    {
        public string? Get(string key) => null;

        public void Set(string key, string? value)
        {
        }

        public void RemoveAll(string prefix)
        {
        }
    }

    private sealed class NoCounter : CashGear.Capture.Core.Output.ICaptureCounter
    {
        public int Next() => 1;
    }

    private sealed class TextOutput : CashGear.Capture.Core.Output.IOutputService
    {
        public List<string> Texts { get; } = [];

        public Task CopyTextToClipboardAsync(string text, CancellationToken ct = default)
        {
            Texts.Add(text);
            return Task.CompletedTask;
        }

        public Task<string?> SaveAsync(CashGear.Capture.Core.Imaging.CapturedImage image, CashGear.Capture.Core.Output.SaveOptions options, CancellationToken ct = default) => throw new NotSupportedException();

        public Task SaveToFileAsync(CashGear.Capture.Core.Imaging.CapturedImage image, string path, CashGear.Capture.Core.Imaging.ImageFileFormat format, int jpegQuality, CancellationToken ct = default) => throw new NotSupportedException();

        public Task CopyToClipboardAsync(CashGear.Capture.Core.Imaging.CapturedImage image, CancellationToken ct = default) => throw new NotSupportedException();

        public Task CopyFilesToClipboardAsync(IReadOnlyList<string> paths, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeSignIn : ISignInDestination
    {
        public bool Hang { get; set; }

        public bool SignedOut { get; private set; }

        public Exception? Failure { get; set; }

        public bool IsSignedIn { get; private set; }

        public string? Account { get; private set; }

        public string Id => DestinationIds.YouTube;

        public string DisplayName => "YouTube";

        public bool SupportsVideo => true;

        public async Task SignInAsync(CancellationToken ct)
        {
            if (Hang)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            if (Failure is { } failure)
            {
                throw failure;
            }

            IsSignedIn = true;
            Account = "me@example.test";
        }

        public void SignOut()
        {
            SignedOut = true;
            IsSignedIn = false;
            Account = null;
        }

        public Task<OutputResult> SendAsync(OutputItem item, CancellationToken ct) => throw new NotSupportedException();

        public Task<OutputResult> TestAsync(CancellationToken ct) => Task.FromResult(OutputResult.Done("ok"));
    }
}
