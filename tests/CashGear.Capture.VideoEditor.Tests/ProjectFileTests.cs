using System.IO;
using System.IO.Compression;
using System.Text;
using CashGear.Capture.Core.Overlays;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Media.Tests;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Model;

namespace CashGear.Capture.VideoEditor.Tests;

public sealed class ProjectFileTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cashgear-cgcap-").FullName;

    public void Dispose()
    {
        if (Environment.GetEnvironmentVariable("CASHGEAR_KEEP_TEST_FILES") is null)
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Cache => Path.Combine(_root, "cache");

    /// <summary>A project file with a made-up recording file next to it.</summary>
    private (VideoProject Project, string Media) WithMedia(string folder)
    {
        Directory.CreateDirectory(folder);
        var media = Path.Combine(folder, "Rec.mp4");
        File.WriteAllBytes(media, Encoding.ASCII.GetBytes("not really a video"));
        var metadata = Path.Combine(folder, "Rec.cgmeta.json");
        File.WriteAllText(metadata, "{}");
        var asset = TestProjects.Recording(60, webcam: true, path: media) with { MetadataPath = metadata };
        var project = ProjectFactory.AddRecording(VideoProject.Empty(new ProjectCanvas(1280, 720, 30, "#102030")), asset, 0);
        project = TimelineEdits.DeleteRange(TimelineEdits.Split(project, 12.5), 3, 4.25);
        var clip = project.Tracks[2].Clips.Last() with { Speed = 1.5, EndSpeed = 0.5, FadeIn = 0.4, Volume = 1.7 };
        project = project.WithItem(clip);
        project = project.WithTrack(project.Tracks[0] with { Muted = true, Volume = 0.3, Locked = true });
        return (project, media);
    }

    [Fact]
    public void A_saved_project_reads_back_exactly()
    {
        var (project, _) = WithMedia(Path.Combine(_root, "work"));
        var file = Path.Combine(_root, "work", "Edit.cgcap");
        var saved = ProjectFile.Save(project, file);
        saved.Assets.Single().RelativePath.Should().Be("Rec.mp4");
        saved.Assets.Single().MetadataRelativePath.Should().Be("Rec.cgmeta.json");

        var opened = ProjectFile.Open(file, Cache);
        opened.Missing.Should().BeEmpty();
        Encoding.UTF8.GetString(ProjectJson.Serialize(opened.Project)).Should().Be(Encoding.UTF8.GetString(ProjectJson.Serialize(saved)));
        opened.Project.Tracks[2].Clips.Last().Should().Be(project.Tracks[2].Clips.Last());
        File.Exists(file + ".partial").Should().BeFalse();

        using var zip = ZipFile.OpenRead(file);
        zip.Entries.Select(e => e.FullName).Should().Equal(ProjectFile.ProjectEntry);
        var json = new StreamReader(zip.GetEntry(ProjectFile.ProjectEntry)!.Open()).ReadToEnd();
        json.Should().Contain("\"type\": \"clip\"").And.Contain("\"kind\": \"Webcam\"").And.Contain("\"background\": \"#102030\"");
    }

    [Fact]
    public void A_project_moved_with_its_media_finds_it_by_the_relative_path()
    {
        var (project, _) = WithMedia(Path.Combine(_root, "a"));
        ProjectFile.Save(project, Path.Combine(_root, "a", "Edit.cgcap"));
        Directory.Move(Path.Combine(_root, "a"), Path.Combine(_root, "b"));

        var opened = ProjectFile.Open(Path.Combine(_root, "b", "Edit.cgcap"), Cache);
        opened.Missing.Should().BeEmpty();
        opened.Project.Assets.Single().Path.Should().Be(Path.Combine(_root, "b", "Rec.mp4"));
        opened.Project.Assets.Single().MetadataPath.Should().Be(Path.Combine(_root, "b", "Rec.cgmeta.json"));
    }

    [Fact]
    public void Missing_media_is_reported_and_can_be_located()
    {
        var (project, media) = WithMedia(Path.Combine(_root, "c"));
        var file = Path.Combine(_root, "Edit.cgcap");
        ProjectFile.Save(project, file);
        var elsewhere = Path.Combine(_root, "elsewhere", "Renamed.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(elsewhere)!);
        File.Move(media, elsewhere);

        var opened = ProjectFile.Open(file, Cache);
        opened.Missing.Select(a => a.Id).Should().Equal("rec");
        var located = ProjectFile.Relocate(opened.Project, "rec", elsewhere);
        located.Asset("rec")!.Path.Should().Be(elsewhere);
        located.Asset("rec")!.MetadataPath.Should().EndWith("Rec.cgmeta.json", "the metadata wasn't moved, and is still found");
    }

    [Fact]
    public void Embedded_media_travels_inside_the_file_and_stays_inside()
    {
        var (project, media) = WithMedia(Path.Combine(_root, "d"));
        var file = Path.Combine(_root, "d", "Edit.cgcap");
        ProjectFile.Save(project, file, embedMedia: true);
        var copy = Path.Combine(_root, "other", "Copy.cgcap");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.Copy(file, copy);
        Directory.Delete(Path.Combine(_root, "d"), recursive: true);

        var opened = ProjectFile.Open(copy, Cache);
        opened.Missing.Should().BeEmpty();
        var asset = opened.Project.Assets.Single();
        asset.Path.Should().StartWith(Cache);
        File.ReadAllText(asset.Path).Should().Be("not really a video");
        File.ReadAllText(asset.MetadataPath!).Should().Be("{}");

        // Saved again without asking: still inside.
        var again = ProjectFile.Save(opened.Project, copy);
        again.Assets.Single().EmbeddedName.Should().Be("rec.mp4");
        using var zip = ZipFile.OpenRead(copy);
        zip.GetEntry("media/rec.mp4")!.CompressedLength.Should().Be(zip.GetEntry("media/rec.mp4")!.Length, "media is stored, not compressed");
    }

    [Fact]
    public void Files_that_are_not_projects_are_refused()
    {
        var text = Path.Combine(_root, "text.cgcap");
        File.WriteAllText(text, "hello");
        FluentActions.Invoking(() => ProjectFile.Open(text, Cache)).Should().Throw<InvalidDataException>();

        var newer = Path.Combine(_root, "newer.cgcap");
        using (var zip = ZipFile.Open(newer, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry(ProjectFile.ProjectEntry).Open());
            writer.Write("""{ "version": 99, "canvas": { "width": 2, "height": 2, "fps": 30 }, "assets": [], "tracks": [], }""");
        }

        FluentActions.Invoking(() => ProjectFile.Open(newer, Cache)).Should().Throw<InvalidDataException>().WithMessage("*newer version*");
    }

    [Fact]
    public void A_recording_with_metadata_and_a_webcam_opens_as_it_was_recorded()
    {
        TestMedia.Require();
        var video = Path.Combine(_root, "rec", "Rec.mp4");
        new StampedRecording
        {
            Path = video,
            Seconds = 2,
            Format = new(320, 180, 30),
            Webcam = new(160, 120, 15),
            Tracks = ["Mixed", "Microphone", "System audio"],
        }.Write();
        var metadata = new RecordingMetadata(
            RecordingMetadata.FormatName, 1, "Rec.mp4", [new MetadataPart("Rec.mp4", 0)], 30, new MetadataArea(0, 0, 320, 180), 1.5,
            CursorInVideo: true, OverlaysInVideo: true, KeystrokeMode.Off, [], [], [], [], [], [],
            new MetadataWebcam("Webcam", WebcamShape.RoundedRectangle, 30, 80, 70, "#FF0000", 4, true, 160, 120));
        metadata.Save(RecordingMetadata.PathFor(video));

        var asset = ProjectFactory.ReadAsset(video);
        asset.MetadataPath.Should().Be(RecordingMetadata.PathFor(video));
        asset.Duration.Should().BeApproximately(2, 0.05);
        var project = ProjectFactory.FromRecording(asset);
        project.Canvas.Should().Be(new ProjectCanvas(320, 180, 30));
        project.Tracks.Select(t => (t.Kind, t.Name, t.Hidden)).Should().Equal(
            (TrackKind.Audio, "Microphone", false), (TrackKind.Audio, "System audio", false), (TrackKind.Video, "Screen", false),
            (TrackKind.Webcam, "Webcam", true));
        project.Tracks[3].Clips.Single().Placement.Should().Be(new Placement(0.8, 0.7, 0.3, PlacementShape.Rounded, "#FF0000", 6, true));
        project.Tracks[3].Clips.Single().Stream.Should().Be(4);
    }
}
