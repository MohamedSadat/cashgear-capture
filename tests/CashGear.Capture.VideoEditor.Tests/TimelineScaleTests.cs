using System.Diagnostics;
using System.IO;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Media;
using CashGear.Capture.Media.Tests;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Export;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Playback;
using CashGear.Capture.VideoEditor.Rendering;
using CashGear.Capture.VideoEditor.Timeline;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace CashGear.Capture.VideoEditor.Tests;

/// <summary>Phase 22 M5: groups, markers, copy and paste, track reordering, the ripple mode, proxies.</summary>
public sealed class TimelineScaleTests : IDisposable
{
    private readonly TestMediaFiles _media = new("cashgear-scale-");

    public void Dispose() => _media.Dispose();

    /// <summary>Three annotations on two tracks and a colour.</summary>
    private static (VideoProject Project, string A, string B, string C) Items()
    {
        var (project, a) = AnnotationEdits.Add(VideoProject.Empty(new ProjectCanvas(640, 360, 30)), AnnotationShape.Rectangle, 1, 2);
        (project, var b) = AnnotationEdits.Add(project, AnnotationShape.Ellipse, 1.5, 2);
        (project, var c) = MediaEdits.AddSolid(project, "#102030", 0, 10);
        return (project, a, b, c);
    }

    [Fact]
    public void A_group_moves_and_is_deleted_as_one()
    {
        var (project, a, b, c) = Items();
        var (grouped, group) = GroupEdits.Group(project, [a, b]);
        group.Should().NotBeNull();
        GroupEdits.Group(project, [a]).Group.Should().BeNull("one item is no group");
        GroupEdits.Members(grouped, a).Should().BeEquivalentTo([a, b]);

        var moved = TimelineEdits.Move(grouped, a, 3);
        moved.Find(a)!.Value.Item.Start.Should().Be(3);
        moved.Find(b)!.Value.Item.Start.Should().Be(3.5, "it moves with it");
        moved.Find(c)!.Value.Item.Start.Should().Be(0, "what isn't in the group stays");

        var gone = TimelineEdits.Delete(grouped, [a]);
        gone.Find(b).Should().BeNull();
        gone.Find(c).Should().NotBeNull();

        var apart = GroupEdits.Ungroup(grouped, [a]);
        apart.Find(b)!.Value.Item.Group.Should().BeNull();
        TimelineEdits.Move(apart, a, 3).Find(b)!.Value.Item.Start.Should().Be(1.5);
    }

    [Fact]
    public void A_cut_through_a_group_leaves_two_groups()
    {
        var (project, a, b, _) = Items();
        var (grouped, group) = GroupEdits.Group(project, [a, b]);
        var split = TimelineEdits.Split(grouped, 2);
        var parts = split.Tracks.SelectMany(t => t.Items.OfType<AnnotationItem>()).ToList();
        parts.Should().HaveCount(4);
        parts.Where(i => i.Start < 2).Should().OnlyContain(i => i.Group == group);
        var after = parts.Where(i => i.Start >= 2).Select(i => i.Group).Distinct().ToList();
        after.Should().ContainSingle().Which.Should().NotBe(group);
    }

    [Fact]
    public void A_groups_clips_bring_what_is_linked_to_them()
    {
        var project = TestProjects.Project(10);
        (project, var text) = AnnotationEdits.Add(project, AnnotationShape.Text, 0, 2);
        var screen = project.Tracks.Single(t => t.Kind == TrackKind.Video).Clips.Single();
        var (grouped, _) = GroupEdits.Group(project, [text, screen.Id]);
        TimelineEdits.Linked(grouped, text).Should().HaveCount(4, "the text, the screen and its two sound clips");
    }

    [Fact]
    public void The_selection_has_the_group_and_what_ctrl_click_adds()
    {
        var (project, a, b, c) = Items();
        var (grouped, _) = GroupEdits.Group(project, [a, b]);
        var session = new TimelineSession(new ProjectHistory(grouped), new TimelineView());
        session.Select(a);
        session.SelectedIds.Should().BeEquivalentTo([a, b]);
        session.ToggleSelected(c);
        session.SelectedIds.Should().BeEquivalentTo([a, b, c]);
        session.ToggleSelected(c);
        session.SelectedIds.Should().BeEquivalentTo([a, b]);
        session.Select(null);
        session.SelectedIds.Should().BeEmpty();
    }

    [Fact]
    public void Markers_are_kept_in_order_and_move_with_the_time()
    {
        var project = TestProjects.Project(30);
        (project, var first) = MarkerEdits.Add(project, 20, "Late");
        (project, _) = MarkerEdits.Add(project, 5, "Early");
        (project, var again) = MarkerEdits.Add(project, 20.001, "Later");
        project.Markers!.Select(m => m.Name).Should().Equal("Early", "Later");
        again.Should().Be(first, "within half a frame it is the same marker");
        MarkerEdits.Next(project, 6, backward: false).Should().Be(20);
        MarkerEdits.Next(project, 6, backward: true).Should().Be(5);
        MarkerEdits.Next(project, 25, backward: false).Should().BeNull();

        // A cut before it moves it back; one over it takes it away.
        var cut = TimelineEdits.DeleteRange(project, 1, 3);
        cut.Markers!.Select(m => m.Time).Should().Equal(3, 18);
        var over = TimelineEdits.DeleteRange(project, 4, 6);
        over.Markers!.Select(m => m.Name).Should().Equal("Later");

        // Speed and freezes carry it along; a range keeps only what is in it, on its own clock.
        var screen = project.Tracks.Single(t => t.Kind == TrackKind.Video).Clips.Single();
        var slower = TimelineEdits.Split(project, 10);
        var first10 = slower.Tracks.Single(t => t.Kind == TrackKind.Video).Clips.First();
        TransitionEdits.SetSpeed(slower, first10.Id, 2).Markers!.Single(m => m.Name == "Later").Time.Should().Be(15);
        TransitionEdits.Freeze(project, 10, 2).Markers!.Single(m => m.Name == "Later").Time.Should().Be(22);
        ProjectRange.Extract(project, new TimeRange(4, 10)).Markers!.Single().Time.Should().Be(1);
        screen.Should().NotBeNull();

        var renamed = MarkerEdits.Rename(project, first, "Last");
        renamed.Markers!.Should().Contain(m => m.Name == "Last");
        MarkerEdits.MoveTo(renamed, first, 1).Markers![0].Name.Should().Be("Last");
        MarkerEdits.Remove(MarkerEdits.Remove(renamed, first), renamed.Markers![0].Id).Markers.Should().BeNull();
        ProjectJson.Serialize(ProjectJson.Deserialize(ProjectJson.Serialize(project))).Should().Equal(ProjectJson.Serialize(project));
    }

    [Fact]
    public void Paste_makes_new_items_where_there_is_room_and_keeps_them_together()
    {
        var project = TestProjects.Project(10);
        var screen = project.Tracks.Single(t => t.Kind == TrackKind.Video).Clips.Single();
        var copied = ClipboardEdits.Copy(project, TimelineEdits.Linked(project, screen.Id).ToList());
        var (pasted, ids) = ClipboardEdits.Paste(project, copied, 10);
        ids.Should().HaveCount(3);
        project.Tracks.SelectMany(t => t.Items).Select(i => i.Id).Should().NotIntersectWith(ids);
        pasted.Tracks.Should().HaveCount(project.Tracks.Count, "its own tracks have room at the end");
        var again = ids.Select(id => (Clip)pasted.Find(id)!.Value.Item).ToList();
        again.Should().OnlyContain(c => c.Start == 10);
        again.Select(c => c.Link).Distinct().Should().ContainSingle().Which.Should().NotBe(screen.Link);
        pasted.Assets.Should().ContainSingle("the same media");

        // Where there isn't room: a new track of its kind.
        var (over, overIds) = ClipboardEdits.Paste(project, copied, 2);
        over.Tracks.Count.Should().Be(project.Tracks.Count + 3);
        over.Find(overIds[0])!.Value.Item.Start.Should().Be(2);

        // What came from another project brings its media; anything else isn't pasted.
        var empty = VideoProject.Empty(new ProjectCanvas(1280, 720, 30));
        var (into, _) = ClipboardEdits.Paste(empty, copied, 0);
        into.Assets.Should().ContainSingle(a => a.Id == "rec");
        ClipboardEdits.Paste(project, "not json"u8, 0).Ids.Should().BeEmpty();
    }

    [Fact]
    public void Pasted_annotations_keep_their_place_on_another_canvas_and_their_group()
    {
        var (project, a, b, _) = Items();
        var (grouped, _) = GroupEdits.Group(project, [a, b]);
        var copied = ClipboardEdits.Copy(grouped, [a, b]);
        var wider = VideoProject.Empty(new ProjectCanvas(1280, 720, 30));
        var (into, ids) = ClipboardEdits.Paste(wider, copied, 5);
        var items = ids.Select(id => (AnnotationItem)into.Find(id)!.Value.Item).OrderBy(i => i.Start).ToList();
        items[0].Start.Should().Be(5);
        items[1].Start.Should().Be(5.5, "they keep their times among them");
        items[0].Keyframes[0].X.Should().BeApproximately(((AnnotationItem)grouped.Find(a)!.Value.Item).Keyframes[0].X * 2, 1e-6);
        items.Select(i => i.Group).Distinct().Should().ContainSingle().Which.Should().NotBeNull();
    }

    [Fact]
    public void Tracks_are_reordered_among_their_own_sort()
    {
        var project = TestProjects.Project(10, webcam: true);
        (project, _) = MediaEdits.AddSolid(project, "#FF0000", 0, 10);
        var rows = TimelineView.Rows(project).Select(r => r.Track.Id).ToList();
        var top = rows[0];
        TrackEdits.Reorder(project, top, -1).Should().BeSameAs(project, "nothing above the top");
        var down = TrackEdits.Reorder(project, top, 1);
        TimelineView.Rows(down).Select(r => r.Track.Id).Take(2).Should().Equal(rows[1], rows[0]);
        var firstSound = project.Tracks.First(t => t.HasSound).Id;
        var lastPicture = TimelineView.Rows(project).Last(r => !r.Track.HasSound).Track.Id;
        TrackEdits.Reorder(project, lastPicture, 1).Should().BeSameAs(project, "the sound stays below the pictures");
        TrackEdits.MoveTo(project, firstSound, top).Should().BeSameAs(project);
        var moved = TrackEdits.MoveTo(project, lastPicture, top);
        TimelineView.Rows(moved).First().Track.Id.Should().Be(lastPicture);
    }

    [Fact]
    public void Reordering_changes_what_is_on_top()
    {
        var (a, first) = MediaEdits.AddSolid(VideoProject.Empty(new ProjectCanvas(64, 36, 30)), "#FF0000", 0, 5);
        var project = a with { Tracks = [.. a.Tracks, new Track("over", TrackKind.Media, "Over", [new Clip { Id = "green", AssetId = "", Solid = "#00FF00", FreezeLength = 5 }])] };
        using var before = new SKBitmap(64, 36);
        using (var canvas = new SKCanvas(before))
        {
            new FrameCompositor(new PatternFrames()).Render(canvas, project, 1);
        }

        before.GetPixel(10, 10).Should().Be(SKColors.Lime);
        var swapped = TrackEdits.Reorder(project, "over", 1);
        using var after = new SKBitmap(64, 36);
        using (var canvas = new SKCanvas(after))
        {
            new FrameCompositor(new PatternFrames()).Render(canvas, swapped, 1);
        }

        after.GetPixel(10, 10).Should().Be(SKColors.Red);
        first.Should().NotBeNull();
    }

    /// <summary>A recording cut in three, on all its tracks.</summary>
    private static VideoProject InThree()
    {
        var project = TestProjects.Project(30);
        return TimelineEdits.Split(TimelineEdits.Split(project, 10), 20);
    }

    [Fact]
    public void The_ripple_mode_closes_the_gap_when_an_edge_comes_in_and_opens_one_when_it_goes_out()
    {
        var project = InThree();
        var screen = project.Tracks.Single(t => t.Kind == TrackKind.Video).Clips.ToList();
        var shorter = MagneticEdits.Trim(project, screen[0].Id, ItemEdge.End, 8);
        var after = shorter.Tracks.Single(t => t.Kind == TrackKind.Video).Clips.ToList();
        after[0].End.Should().Be(8);
        after[1].Start.Should().Be(8, "what follows closes up");
        shorter.Duration.Should().Be(28);
        shorter.Tracks.Where(t => t.HasSound).Should().OnlyContain(t => t.End == 28, "the sound stays with it");

        var fromStart = MagneticEdits.Trim(project, screen[1].Id, ItemEdge.Start, 12);
        var cutIn = fromStart.Tracks.Single(t => t.Kind == TrackKind.Video).Clips.ToList();
        cutIn[1].Start.Should().Be(10, "it stays where it starts");
        cutIn[1].SourceIn.Should().Be(12);
        cutIn[2].Start.Should().Be(18);

        // Longer: what follows makes room, and the media it hides shows.
        var longer = MagneticEdits.Trim(shorter, after[0].Id, ItemEdge.End, 9);
        var opened = longer.Tracks.Single(t => t.Kind == TrackKind.Video).Clips.ToList();
        opened[0].End.Should().Be(9);
        opened[1].Start.Should().Be(9);
        opened[0].SourceOut.Should().Be(9);
        longer.Duration.Should().Be(29);
    }

    [Fact]
    public void The_ripple_mode_moves_a_clip_between_others_with_its_linked_clips()
    {
        var project = InThree();
        var screen = project.Tracks.Single(t => t.Kind == TrackKind.Video).Clips.ToList();
        var moved = MagneticEdits.Move(project, screen[2].Id, 1);
        var clips = moved.Tracks.Single(t => t.Kind == TrackKind.Video).Clips.OrderBy(c => c.Start).ToList();
        clips[0].Id.Should().Be(screen[2].Id, "it goes in at the nearest edge");
        clips[0].Start.Should().Be(0);
        clips[1].Id.Should().Be(screen[0].Id);
        clips[1].Start.Should().Be(10);
        clips[2].Start.Should().Be(20);
        moved.Duration.Should().Be(30);
        var mic = moved.Tracks.First(t => t.HasSound).Clips.OrderBy(c => c.Start).ToList();
        mic[0].SourceIn.Should().Be(screen[2].SourceIn, "its sound goes with it");
    }

    [Fact]
    public void The_ripple_mode_leaves_a_cuts_join_alone()
    {
        var project = TestProjects.Project(30);
        var cut = TranscriptEdits.Cut(project, [new CutRange(10, 11)]);
        var mic = cut.Tracks.First(t => t.HasSound).Clips.ToList();
        var overlap = mic[0].End - mic[1].Start;
        overlap.Should().BeApproximately(TranscriptEdits.JoinLength, 1e-6);
        var screen = cut.Tracks.Single(t => t.Kind == TrackKind.Video).Clips.ToList();
        var last = screen[^1];

        // Taking time out of the second part, and adding it to the first: the first cut's sound stays joined.
        var after = MagneticEdits.Trim(cut, last.Id, ItemEdge.End, last.End - 2);
        var micAfter = after.Tracks.First(t => t.HasSound).Clips.ToList();
        (micAfter[0].End - micAfter[1].Start).Should().BeApproximately(TranscriptEdits.JoinLength, 1e-6);

        var longer = MagneticEdits.Trim(cut, screen[0].Id, ItemEdge.End, screen[0].End + 0.5);
        var micLonger = longer.Tracks.First(t => t.HasSound).Clips.ToList();
        var pictureLonger = longer.Tracks.Single(t => t.Kind == TrackKind.Video).Clips.ToList();
        pictureLonger[1].Start.Should().BeApproximately(screen[1].Start + 0.5, 1e-6);
        micLonger[1].Start.Should().BeApproximately(mic[1].Start + 0.5, 1e-6, "the join moved along with the picture");
        (micLonger[0].End - micLonger[1].Start).Should().BeApproximately(TranscriptEdits.JoinLength, 1e-6, "and is still a join");
    }

    [Fact]
    public void Proxies_are_only_wanted_for_taller_media_and_only_read_when_on()
    {
        var folder = Path.Combine(_media.Root, "proxies");
        var store = new ProxyStore(folder, new SoftwareEncoders(), NullLogger<ProxyStore>.Instance);
        ProxyStore.Wants(new AssetStream(0, StreamKind.Video, null, 0, 1, 3840, 2160), ProxyStore.Height1080).Should().BeTrue();
        ProxyStore.Wants(new AssetStream(0, StreamKind.Video, null, 0, 1, 1920, 1080), ProxyStore.Height1080).Should().BeFalse();
        ProxyStore.Wants(new AssetStream(0, StreamKind.Video, null, 0, 1, 3840, 2160), ProxyStore.Off).Should().BeFalse();
        var asset = new MediaAsset("a", @"C:\nowhere\Big.mp4", 1, [new AssetStream(0, StreamKind.Video, null, 0, 1, 3840, 2160)]);
        store.Height = ProxyStore.Height720;
        store.PathFor(asset, 0).Should().BeNull("there is no such copy");
    }

    [Fact]
    public async Task A_proxy_has_every_picture_at_its_time_and_the_preview_reads_it()
    {
        TestMedia.Require();
        var file = _media.Stamped("big.mp4", 3, new VideoFormat(1280, 720, 30));
        var asset = ProjectFactory.ReadAsset(file);
        var store = new ProxyStore(Path.Combine(_media.Root, "proxies"), new SoftwareEncoders(), NullLogger<ProxyStore>.Instance) { Height = ProxyStore.Height720 };
        var main = ProjectFactory.MainVideo(asset)!;

        // Anything lower than the stream is wanted; 360 stands in for 1080 under 4K, for time's sake.
        var made = await store.EnsureAsync(asset, main.Index, 360, ct: TestContext.Current.CancellationToken);
        File.Exists(made).Should().BeTrue();
        store.IsReady(asset, main.Index, 360).Should().BeTrue();
        (await store.EnsureAsync(asset, main.Index, 360, ct: TestContext.Current.CancellationToken)).Should().Be(made, "once");

        using var reader = new VideoStreamReader(made, decoding: VideoDecoding.Software);
        (reader.Width, reader.Height).Should().Be((640, 360));
        var pixels = new byte[640 * 360 * 4];
        var stamps = new List<int>();
        while (reader.Read(pixels, 640, 360) is not null)
        {
            stamps.Add(StampedRecording.ReadStamp(pixels, 640, 0.5));
        }

        stamps.Should().HaveCount(90);
        stamps.Should().BeInAscendingOrder();
        stamps.Distinct().Count().Should().Be(90, "no picture is lost or repeated");
    }

    [Fact]
    public void A_thirty_minute_project_of_large_clips_is_laid_out_from_a_stream_at_a_time()
    {
        var asset = new MediaAsset("rec", @"C:\nowhere\Big.mp4", 60, [new AssetStream(0, StreamKind.Video, null, 0, 60, 3840, 2160, 30)]);
        var (project, _) = MediaEdits.Add(VideoProject.Empty(new ProjectCanvas(3840, 2160, 30)), asset, 0);
        for (var i = 1; i < 30; i++)
        {
            (project, _) = MediaEdits.Add(project, asset, i * 60.0);
        }

        project.Duration.Should().Be(1800);
        project.Tracks.Should().ContainSingle();
    }
}

/// <summary>Phase 22 M5's acceptance: run on their own, as the other timed ones are.</summary>
[Collection(nameof(PreviewAcceptance))]
public sealed class ProxyAcceptanceTests : IDisposable
{
    private readonly TestMediaFiles _media = new("cashgear-proxy-");

    public void Dispose() => _media.Dispose();

    [Fact]
    public Task Acceptance_a_thirty_minute_4K_project_previews_at_full_rate_with_proxies_and_exports_from_the_original() =>
        CashGear.Capture.Tests.MachineTurn.RunAsync(FourKAcceptance);

    private async Task<bool> FourKAcceptance()
    {
        TestMedia.Require();
        Assert.SkipWhen(VideoStreamReader.HardwareProblem is not null, $"No hardware video decoding here: {VideoStreamReader.HardwareProblem}");
        var output = TestContext.Current.TestOutputHelper;
        var made = Stopwatch.StartNew();

        // A minute of 4K laid out thirty times is thirty minutes of 4K on the timeline: what the preview reads is the same.
        var file = _media.Stamped("4k.mp4", 60, new VideoFormat(3840, 2160, 30));
        output?.WriteLine($"Made a minute of 4K in {made.Elapsed.TotalSeconds:0} s");
        var asset = ProjectFactory.ReadAsset(file);
        var main = ProjectFactory.MainVideo(asset)!;
        var project = VideoProject.Empty(new ProjectCanvas(3840, 2160, 30)) with { Assets = [asset] };
        var clips = Enumerable.Range(0, 30).Select(i => (TimelineItem)new Clip { Id = $"c{i}", AssetId = asset.Id, Stream = main.Index, Start = i * 60.0, SourceOut = asset.Duration }).ToList();
        project = project with { Tracks = [new Track("screen", TrackKind.Video, "Screen", clips)] };
        project.Duration.Should().BeApproximately(1800, 1);

        var store = new ProxyStore(Path.Combine(_media.Root, "proxies"), new SoftwareEncoders(), NullLogger<ProxyStore>.Instance) { Height = ProxyStore.Height720 };
        var proxying = Stopwatch.StartNew();
        await store.EnsureAsync(asset, main.Index, ProxyStore.Height720, ct: TestContext.Current.CancellationToken);
        output?.WriteLine($"The 720p proxy of a minute of 4K: {proxying.Elapsed.TotalSeconds:0} s, {new FileInfo(store.PathFor(asset, main.Index)!).Length / 1_000_000} MB");

        // Ten seconds played from each of three places, drawn into a preview of 1280 × 720 on the graphics card's decoder.
        var (proxied, waited, drew) = Played(project, store);
        output?.WriteLine($"With proxies: {proxied} of 300 pictures, {waited} draws waited, {drew:0.0} ms a draw");
        proxied.Should().BeGreaterThanOrEqualTo(297, "at least 99% of the pictures are shown at 30 fps");

        var (direct, directWaited, directDrew) = Played(project, null);
        output?.WriteLine($"Without proxies, for comparison: {direct} of 300 pictures, {directWaited} draws waited, {directDrew:0.0} ms a draw");

        // The export reads the original, at the project's size: compared with a direct decode of it, frame by frame.
        var path = Path.Combine(_media.Root, "part.mp4");
        var range = new TimeRange(600, 603);
        var result = await new ProjectExporter(new SoftwareEncoders(), NullLogger<ProjectExporter>.Instance).ExportAsync(new ExportRequest(project, ExportPresets.Mp4H264Project, QualityPreset.High, path, Range: range), ct: TestContext.Current.CancellationToken);
        result.Frames.Should().Be(90);
        using var exported = new VideoStreamReader(path, decoding: VideoDecoding.Software);
        (exported.Width, exported.Height).Should().Be((3840, 2160), "the export is 4K");
        using var original = new VideoStreamReader(file, main.Index, VideoDecoding.Software);
        original.Seek(0);
        var a = new byte[3840 * 2160 * 4];
        var b = new byte[3840 * 2160 * 4];
        var compared = 0;
        var worst = 0.0;
        for (var n = 0; n < 90 && exported.Read(a, 3840, 2160) is not null && original.Read(b, 3840, 2160) is not null; n++)
        {
            StampedRecording.ReadStamp(a, 3840).Should().Be(StampedRecording.ReadStamp(b, 3840), "frame {0} is the original's", n);
            if (n % 30 == 0)
            {
                long sum = 0;
                for (var i = 0; i < a.Length; i += 4 * 97)
                {
                    sum += Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);
                }

                worst = Math.Max(worst, sum / (a.Length / (4.0 * 97) * 3));
            }

            compared++;
        }

        compared.Should().Be(90);
        output?.WriteLine($"The export's 90 frames against a direct decode: the worst differs by {worst:0.00} levels on average");
        worst.Should().BeLessThan(3);
        return true;
    }

    /// <summary>
    /// The plan's budget for this phase: a 1080p30 project using its features previews at full rate on the reference PC. Real
    /// decoding, on the graphics card through the hidden OpenGL context: a look, a colour effect on the screen, a transition
    /// every two seconds, a keyframed picture over it with a key, a moving title and animated text.
    /// </summary>
    [Fact]
    public void Acceptance_a_1080p_project_with_this_phases_features_previews_at_full_rate_on_the_graphics_card()
    {
        TestMedia.Require();
        Assert.SkipWhen(VideoStreamReader.HardwareProblem is not null, $"No hardware video decoding here: {VideoStreamReader.HardwareProblem}");
        using var turn = new CashGear.Capture.Tests.MachineTurn();
        using var gl = OffscreenGl.TryCreate();
        Assert.SkipWhen(gl is null, "No OpenGL here.");
        var file = _media.Stamped("hd-motion.mp4", 12, new VideoFormat(1920, 1080, 30));
        var project = Looks.Apply(ProjectFactory.FromRecording(ProjectFactory.ReadAsset(file)), Looks.Find(Looks.SoftId)!);
        foreach (var at in new[] { 8.0, 6.0, 4.0, 2.0 })
        {
            project = TimelineEdits.Split(project, at);
        }

        TransitionKind[] kinds = [TransitionKind.Dissolve, TransitionKind.Wipe, TransitionKind.Blur, TransitionKind.Push];
        for (var i = 0; i < 4; i++)
        {
            var clip = project.Tracks.Single(t => t.Kind == TrackKind.Video).Clips.ElementAt(i + 1);
            project = TransitionEdits.SetTransition(project, clip.Id, ItemEdge.Start, new ClipTransition(kinds[i], 0.5));
        }

        project = project with
        {
            Tracks = [.. project.Tracks.Select(t => t.Kind == TrackKind.Video
                ? t with { Items = [.. t.Clips.Select(c => (TimelineItem)(c with { Effects = new ClipEffects(Brightness: 0.05, Contrast: 0.1, Saturation: 0.2, Temperature: 0.1) }))] }
                : t)],
        };
        (project, var solid) = MediaEdits.AddSolid(project, "#20C040", 0, project.Duration);
        project = project.WithItem((Clip)project.Find(solid)!.Value.Item with
        {
            Placement = new Placement(0.8, 0.25, 0.3),
            Effects = new ClipEffects(Key: new ChromaKey("#0000FF")),
            Motion = new ItemMotion { Rotation = [new(0, 0, MotionEasing.EaseInOut), new(10, 360)], Scale = [new(0, 1, MotionEasing.Spring), new(5, 1.4)], Opacity = [new(0, 0.5), new(5, 1)] },
        });
        (project, _) = TemplateEdits.Insert(project, BuiltInTemplates.All.Single(t => t.Id == "third-bar"), 1);
        (project, var text) = AnnotationEdits.Add(project, AnnotationShape.Text, 0, project.Duration);
        project = project.WithItem((AnnotationItem)project.Find(text)!.Value.Item with { TextIn = new TextAnimation(TextAnimationKind.Typewriter, 2), TextDuring = new TextAnimation(TextAnimationKind.Pop, 1) });

        using var frames = new PreviewFrames(NullLoggerFactory.Instance, VideoDecoding.Hardware);
        var stamps = new StampingFrames(frames);
        var compositor = new FrameCompositor(stamps);
        using var surface = gl!.Surface(1280, 720);
        var duration = project.Duration;
        void Draw(double time, ref int missing)
        {
            var canvas = surface.Canvas;
            canvas.Save();
            canvas.Scale(1280f / 1920, 720f / 1080);
            missing += compositor.Render(canvas, project, time).Missing;
            canvas.Restore();
            surface.Flush();
            gl.Context.Submit(synchronous: true);
        }

        var ignored = 0;
        frames.Update(project, 0, playing: false);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        do
        {
            ignored = 0;
            Draw(0, ref ignored);
            Thread.Sleep(5);
        }
        while (ignored > 0 && DateTime.UtcNow < deadline);

        stamps.Shown.Clear();
        var expected = new HashSet<int>();
        var screen = project.Tracks.Single(t => t.Kind == TrackKind.Video);
        for (var n = 0; n < duration * 30; n++)
        {
            foreach (var clip in screen.Clips.Where(c => c.Covers((double)n / 30)))
            {
                expected.Add((int)Math.Floor((clip.SourceAt((double)n / 30) * 30) + 1e-6));
            }
        }

        var draws = new List<double>();
        var waited = 0;
        var tick = Stopwatch.StartNew();
        var next = TimeSpan.Zero;
        while (tick.Elapsed.TotalSeconds < duration)
        {
            var time = tick.Elapsed.TotalSeconds;
            frames.Update(project, time, playing: true);
            var started = tick.Elapsed;
            Draw(time, ref waited);
            draws.Add((tick.Elapsed - started).TotalMilliseconds);
            next += TimeSpan.FromSeconds(1 / 60.0);
            var wait = next - tick.Elapsed;
            if (wait > TimeSpan.Zero)
            {
                Thread.Sleep(wait);
            }
        }

        var shown = stamps.Shown.Distinct().Count(expected.Contains);
        var sorted = draws.Order().ToList();
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"1080p with this phase's features on the graphics card: {shown} of {expected.Count} pictures shown, {waited} draws waited for a picture, {sorted.Average():0.0} ms a draw on average, {sorted[(int)(sorted.Count * 0.95)]:0.0} ms at the 95th percentile, {sorted[^1]:0.0} ms at most");
        shown.Should().BeGreaterThanOrEqualTo((int)(expected.Count * 0.97), "the pictures are shown at 30 fps");
    }

    /// <summary>Plays ten seconds in all from three places of the timeline; returns the pictures shown, the draws that waited and a draw's time.</summary>
    private static (int Shown, int Waited, double Draw) Played(VideoProject project, IProxyResolver? proxies)
    {
        var shown = 0;
        var waited = 0;
        var draws = new List<double>();
        foreach (var from in new[] { 10.0, 900.0, 1500.0 })
        {
            using var frames = new PreviewFrames(NullLoggerFactory.Instance, VideoDecoding.Hardware) { Proxies = proxies };
            var stamps = new StampingFrames(frames, proxies is null ? 1 : 1 / 3.0);
            var compositor = new FrameCompositor(stamps);
            using var surface = SKSurface.Create(new SKImageInfo(1280, 720, SKColorType.Bgra8888, SKAlphaType.Premul));
            frames.Update(project, from, playing: false);
            var deadline = DateTime.UtcNow.AddSeconds(20);
            void Draw(double time)
            {
                var canvas = surface.Canvas;
                canvas.Save();
                canvas.Scale(1280f / 3840, 720f / 2160);
                waited += compositor.Render(canvas, project, time).Missing;
                canvas.Restore();
                surface.Flush();
            }

            var earlier = waited;
            while (DateTime.UtcNow < deadline)
            {
                var before = waited;
                Draw(from);
                if (waited == before)
                {
                    break;
                }

                Thread.Sleep(5);
            }

            waited = earlier; // what waiting for the first picture cost doesn't count
            stamps.Shown.Clear();
            var tick = Stopwatch.StartNew();
            var next = TimeSpan.Zero;
            const double seconds = 10.0 / 3;
            while (tick.Elapsed.TotalSeconds < seconds)
            {
                var time = from + tick.Elapsed.TotalSeconds;
                frames.Update(project, time, playing: true);
                var started = tick.Elapsed;
                Draw(time);
                draws.Add((tick.Elapsed - started).TotalMilliseconds);
                next += TimeSpan.FromSeconds(1 / 60.0);
                var wait = next - tick.Elapsed;
                if (wait > TimeSpan.Zero)
                {
                    Thread.Sleep(wait);
                }
            }

            shown += stamps.Shown.Distinct().Count();
        }

        return (shown, waited, draws.Average());
    }
}
