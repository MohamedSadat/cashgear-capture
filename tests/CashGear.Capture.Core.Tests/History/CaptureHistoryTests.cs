using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.History;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.Core.Tests.History;

public sealed class CaptureHistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cashgear-tests", Guid.NewGuid().ToString("N"));
    private readonly TestSettings _settings = new();
    private readonly RecordingOutput _output = new();
    private readonly AppPaths _paths;

    public CaptureHistoryTests()
    {
        _paths = new AppPaths(Path.Combine(_root, "settings"), Path.Combine(_root, "logs"), Path.Combine(_root, "pictures"), Path.Combine(_root, "data"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private FileCaptureHistory NewHistory() => new(_paths, _settings, _output, TimeProvider.System, NullLogger<FileCaptureHistory>.Instance);

    private static CapturedImage Image(int width, int height, CaptureKind kind = CaptureKind.Region, string? title = null, int minute = 0)
    {
        var metadata = new CaptureMetadata
        {
            Kind = kind,
            SourceBounds = new PixelRect(0, 0, width, height),
            CapturedAt = new DateTimeOffset(2026, 10, 15, 10, minute, 0, TimeSpan.FromHours(3)),
            WindowTitle = title,
        };
        var image = CapturedImage.Allocate(width, height, metadata);
        image.Pixels.Fill(255);
        return image;
    }

    [Fact]
    public async Task Captures_are_kept_newest_first_with_an_image_and_a_thumbnail()
    {
        using var history = NewHistory();
        var changes = 0;
        history.Changed += (_, _) => changes++;

        using (var first = Image(800, 600, CaptureKind.Window, "Notepad", minute: 1))
        {
            await history.AddAsync(first, savedPath: null, savedPngPath: null, TestContext.Current.CancellationToken);
        }

        using (var second = Image(3200, 1082, CaptureKind.AllMonitors, minute: 2))
        {
            await history.AddAsync(second, savedPath: @"C:\Pictures\Capture_all.png", savedPngPath: null, TestContext.Current.CancellationToken);
        }

        history.Entries.Should().HaveCount(2);
        history.Entries[0].Kind.Should().Be(CaptureKind.AllMonitors);
        history.Entries[0].SavedPath.Should().Be(@"C:\Pictures\Capture_all.png");
        history.Entries[1].Description.Should().Be("Window · Notepad · 800 × 600");
        changes.Should().Be(2);

        File.Exists(history.ImagePath(history.Entries[0])).Should().BeTrue();
        File.Exists(history.ThumbnailPath(history.Entries[0])).Should().BeTrue();
        _output.Sizes[history.ThumbnailPath(history.Entries[0])].Should().Be((240, 81));
        _output.Sizes[history.ThumbnailPath(history.Entries[1])].Should().Be((213, 160));
    }

    [Fact]
    public async Task A_capture_saved_as_png_is_copied_rather_than_encoded_again()
    {
        var saved = Path.Combine(_root, "saved.png");
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(saved, "the saved file", TestContext.Current.CancellationToken);
        using var history = NewHistory();

        using (var image = Image(100, 50))
        {
            await history.AddAsync(image, saved, saved, TestContext.Current.CancellationToken);
        }

        var entry = history.Entries.Single();
        (await File.ReadAllTextAsync(history.ImagePath(entry), TestContext.Current.CancellationToken)).Should().Be("the saved file");
        _output.Sizes.Should().ContainSingle("only the thumbnail was encoded");
    }

    [Fact]
    public async Task The_oldest_captures_go_when_the_limit_is_reached_or_lowered()
    {
        _settings.Current = new AppSettings { History = new HistorySettings(MaxItems: 3) };
        using var history = NewHistory();
        for (var i = 0; i < 5; i++)
        {
            using var image = Image(10 + i, 10, minute: i);
            await history.AddAsync(image, null, null, TestContext.Current.CancellationToken);
        }

        history.Entries.Select(e => e.Width).Should().Equal(14, 13, 12);
        Directory.GetFiles(_paths.HistoryDirectory, "*.png").Should().HaveCount(6, "an image and a thumbnail for each kept capture");

        _settings.Change(new AppSettings { History = new HistorySettings(MaxItems: 1) });
        await WaitUntil(() => history.Entries.Count == 1);
        history.Entries.Single().Width.Should().Be(14);
        Directory.GetFiles(_paths.HistoryDirectory, "*.png").Should().HaveCount(2);
    }

    [Fact]
    public async Task History_off_keeps_nothing()
    {
        _settings.Current = new AppSettings { History = new HistorySettings(MaxItems: 0) };
        using var history = NewHistory();
        using var image = Image(10, 10);

        (await history.AddAsync(image, null, null, TestContext.Current.CancellationToken)).Should().BeNull();
        history.Entries.Should().BeEmpty();
        Directory.Exists(_paths.HistoryDirectory).Should().BeFalse();
    }

    [Fact]
    public async Task Remove_and_clear_delete_the_files()
    {
        using var history = NewHistory();
        for (var i = 0; i < 3; i++)
        {
            using var image = Image(10 + i, 10, minute: i);
            await history.AddAsync(image, null, null, TestContext.Current.CancellationToken);
        }

        var middle = history.Entries[1];
        await history.RemoveAsync(middle.Id);
        history.Entries.Should().NotContain(middle);
        File.Exists(history.ImagePath(middle)).Should().BeFalse();
        await history.RemoveAsync("no-such-id");

        await history.ClearAsync();
        history.Entries.Should().BeEmpty();
        Directory.GetFiles(_paths.HistoryDirectory, "*.png").Should().BeEmpty();
    }

    [Fact]
    public async Task The_list_survives_a_restart_and_stray_or_missing_files_are_cleaned_up()
    {
        using (var history = NewHistory())
        {
            for (var i = 0; i < 2; i++)
            {
                using var image = Image(10 + i, 10, minute: i);
                await history.AddAsync(image, null, null, TestContext.Current.CancellationToken);
            }

            File.Delete(history.ImagePath(history.Entries[1])); // e.g. removed by hand
        }

        var stray = Path.Combine(_paths.HistoryDirectory, "left-over.png");
        await File.WriteAllTextAsync(stray, "x", TestContext.Current.CancellationToken);

        using var reopened = NewHistory();
        reopened.Entries.Should().ContainSingle().Which.Width.Should().Be(11);
        File.Exists(stray).Should().BeFalse();
    }

    [Fact]
    public void A_broken_or_tampered_list_is_ignored()
    {
        Directory.CreateDirectory(_paths.HistoryDirectory);
        File.WriteAllText(Path.Combine(_paths.HistoryDirectory, "history.json"), "{ not json");
        using (var history = NewHistory())
        {
            history.Entries.Should().BeEmpty();
        }

        File.WriteAllText(
            Path.Combine(_paths.HistoryDirectory, "history.json"),
            """[{ "id": "..\\..\\settings\\settings", "capturedAt": "2026-10-15T10:00:00+03:00", "kind": "Region", "width": 1, "height": 1 }]""");
        using var tampered = NewHistory();
        tampered.Entries.Should().BeEmpty("an id that is not a plain name is never used as a path");
    }

    [Theory]
    [InlineData(3200, 1082, 240, 81, 1082)]
    [InlineData(800, 600, 213, 160, 600)]
    [InlineData(100, 50, 100, 50, 50)]
    [InlineData(1000, 20000, 107, 160, 1500)] // a scrolling capture: its top part
    public void Thumbnails_fit_the_box(int width, int height, int thumbWidth, int thumbHeight, int shownHeight)
    {
        var (source, w, h) = HistoryThumbnail.Fit(width, height);
        (w, h).Should().Be((thumbWidth, thumbHeight));
        source.Should().Be(new PixelRect(0, 0, width, shownHeight));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    private sealed class TestSettings : ISettingsService
    {
        public event EventHandler? Changed;

        public AppSettings Current { get; set; } = new();

        public void Change(AppSettings settings)
        {
            Current = settings;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task UpdateAsync(Func<AppSettings, AppSettings> update, CancellationToken ct = default)
        {
            Change(update(Current));
            return Task.CompletedTask;
        }
    }

    /// <summary>Writes a placeholder file per encode and remembers each file's image size.</summary>
    private sealed class RecordingOutput : IOutputService
    {
        public Dictionary<string, (int Width, int Height)> Sizes { get; } = [];

        public Task SaveToFileAsync(CapturedImage image, string path, ImageFileFormat format, int jpegQuality, CancellationToken ct = default)
        {
            File.WriteAllText(path, $"{image.Width}x{image.Height}");
            Sizes[path] = (image.Width, image.Height);
            return Task.CompletedTask;
        }

        public Task<string?> SaveAsync(CapturedImage image, SaveOptions options, CancellationToken ct = default) => throw new NotSupportedException();

        public Task CopyToClipboardAsync(CapturedImage image, CancellationToken ct = default) => throw new NotSupportedException();

        public Task CopyTextToClipboardAsync(string text, CancellationToken ct = default) => throw new NotSupportedException();
    }
}

public sealed class PixelShrinkTests
{
    [Fact]
    public void Each_result_pixel_is_the_average_of_its_block()
    {
        // 4x2: left 2x2 block black/white checker (average 127.5 grey), right 2x2 block solid red.
        byte[] source =
        [
            0, 0, 0, 255, 255, 255, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255,
            255, 255, 255, 255, 0, 0, 0, 255, 0, 0, 255, 255, 0, 0, 255, 255,
        ];

        var result = PixelScaler.Shrink(source, 4, 2, 16, 2, 1);

        result[..4].Should().Equal(128, 128, 128, 255);
        result[4..].Should().Equal(0, 0, 255, 255);
    }

    [Fact]
    public void Transparent_pixels_do_not_darken_their_neighbours()
    {
        // One white pixel and one fully transparent (black) pixel: the colour stays white, half covered.
        byte[] source = [255, 255, 255, 255, 0, 0, 0, 0];

        PixelScaler.Shrink(source, 2, 1, 8, 1, 1).Should().Equal(255, 255, 255, 128);
    }

    [Fact]
    public void Rows_may_be_wider_than_the_area_read()
    {
        // Stride 12 (3 pixels), only the left 2 columns are read.
        byte[] source = [10, 10, 10, 255, 30, 30, 30, 255, 99, 99, 99, 255];

        PixelScaler.Shrink(source, 2, 1, 12, 1, 1).Should().Equal(20, 20, 20, 255);
    }
}
