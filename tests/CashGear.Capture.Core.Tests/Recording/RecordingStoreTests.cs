using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.Core.Tests.Recording;

public sealed class RecordingStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cashgear-store-").FullName;
    private readonly RecordingStore _store;

    public RecordingStoreTests()
    {
        _store = new RecordingStore(new AppPaths(Path.Combine(_root, "settings"), Path.Combine(_root, "logs"), Path.Combine(_root, "pictures"), Path.Combine(_root, "data")));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static RecordingManifest Manifest(string id = "a1b2c3d4") => new(
        id, new DateTimeOffset(2026, 10, 17, 14, 30, 5, TimeSpan.Zero), @"C:\Pictures\Recording_001.mp4", 1920, 1080, 30,
        ["segment-001.mp4"], ["Mixed", "Microphone"]);

    [Fact]
    public void Recordings_go_to_their_own_folder_under_LocalAppData()
    {
        using var folder = _store.Create(Manifest());

        _store.Root.Should().Be(Path.Combine(_root, "data", "Recordings", "Temp"));
        Path.GetDirectoryName(folder.Directory).Should().Be(_store.Root);
        Path.GetFileName(folder.Directory).Should().EndWith("-a1b2c3d4");
        folder.SegmentPath(2).Should().Be(Path.Combine(folder.Directory, "segment-002.mp4"));
        RecordingStore.ReadManifest(folder.Directory).Should().BeEquivalentTo(Manifest());
    }

    [Fact]
    public void A_recording_being_written_is_not_an_orphan()
    {
        using var folder = _store.Create(Manifest());

        _store.FindOrphans().Should().BeEmpty();
    }

    [Fact]
    public void A_recording_whose_writer_went_away_is_an_orphan_with_its_manifest()
    {
        string directory;
        using (var folder = _store.Create(Manifest()))
        {
            folder.Save(Manifest() with { Segments = ["segment-001.mp4", "segment-002.mp4"] });
            directory = folder.Directory;
        }

        var orphans = _store.FindOrphans();

        orphans.Should().ContainSingle().Which.Directory.Should().Be(directory);
        orphans[0].Manifest.Segments.Should().Equal("segment-001.mp4", "segment-002.mp4");
    }

    [Fact]
    public void Folders_without_a_readable_manifest_are_skipped()
    {
        Directory.CreateDirectory(Path.Combine(_store.Root, "junk"));
        var broken = Path.Combine(_store.Root, "broken");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, RecordingStore.ManifestFile), "{");

        _store.FindOrphans().Should().BeEmpty();
    }

    [Fact]
    public void Deleting_removes_the_folder()
    {
        string directory;
        using (var folder = _store.Create(Manifest()))
        {
            directory = folder.Directory;
        }

        RecordingStore.Delete(directory).Should().BeTrue();
        Directory.Exists(directory).Should().BeFalse();
        _store.FindOrphans().Should().BeEmpty();
    }

    [Fact]
    public void Later_parts_are_named_after_the_first()
    {
        PartNames.Part(@"C:\Videos\Demo.mp4", 1).Should().Be(@"C:\Videos\Demo.mp4");
        PartNames.Part(@"C:\Videos\Demo.mp4", 2).Should().Be(@"C:\Videos\Demo (part 2).mp4");
    }

    [Fact]
    public void A_taken_name_gets_a_number()
    {
        var path = Path.Combine(_root, "Demo.mp4");
        PartNames.Unique(path).Should().Be(path);

        File.WriteAllText(path, "");
        File.WriteAllText(Path.Combine(_root, "Demo (2).mp4"), "");

        PartNames.Unique(path).Should().Be(Path.Combine(_root, "Demo (3).mp4"));
    }
}
