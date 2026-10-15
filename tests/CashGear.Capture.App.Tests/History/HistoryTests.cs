using System.IO;
using CashGear.Capture.App.Editor;
using CashGear.Capture.App.History;
using CashGear.Capture.App.Output;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Tests.Editor;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.History;
using CashGear.Capture.Core.Imaging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.App.Tests.History;

public sealed class HistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cashgear-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeHistory _history;
    private readonly FakeEditor _editor = new();
    private readonly FakeOutputService _output = new();
    private readonly FakeShell _shell = new();
    private readonly FakeNotifier _notifier = new();

    public HistoryTests()
    {
        Directory.CreateDirectory(_root);
        _history = new FakeHistory(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private HistoryActions Actions() =>
        new(_history, _editor, _output, _shell, _notifier, NullLogger<HistoryActions>.Instance);

    private HistoryViewModel ViewModel() => new(_history, Actions(), new InlineUiDispatcher(), TimeProvider.System);

    [Theory]
    [InlineData("2026-10-15T10:42:00+03:00", "Today ")]
    [InlineData("2026-10-14T23:59:00+03:00", "Yesterday ")]
    [InlineData("2026-10-13T09:30:00+03:00", "13 Oct ")]
    public void When_a_capture_was_taken_reads_naturally(string at, string start)
    {
        var now = DateTimeOffset.Parse("2026-10-15T12:00:00+03:00", System.Globalization.CultureInfo.InvariantCulture);
        using var english = new CultureScope("en-GB");
        HistoryItem.FormatWhen(DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture), now).Should().StartWith(start);
    }

    [Fact]
    public async Task A_stored_png_reads_back_pixel_for_pixel()
    {
        using var image = Checkerboard(37, 21);
        var path = Path.Combine(_root, "image.png");
        await using (var file = File.Create(path))
        {
            WpfImaging.Encode(image.ToBitmapSource(), ImageFileFormat.Png, 100, keepAlpha: true, file);
        }

        using var loaded = WpfImaging.Load(path, image.Metadata);

        (loaded.Width, loaded.Height).Should().Be((37, 21));
        loaded.Pixels.ToArray().Should().Equal(image.Pixels.ToArray());
        loaded.Metadata.Kind.Should().Be(CaptureKind.Region);
    }

    [Fact]
    public async Task Opening_from_history_uses_the_saved_file_only_while_it_exists()
    {
        var saved = Path.Combine(_root, "Capture_saved.png");
        var withFile = await _history.AddPngAsync(Checkerboard(8, 8), saved);
        var withoutFile = await _history.AddPngAsync(Checkerboard(9, 9), Path.Combine(_root, "deleted.png"));
        File.Copy(_history.ImagePath(withFile), saved);
        var actions = Actions();

        await actions.OpenAsync(withFile);
        await actions.OpenAsync(withoutFile);

        _editor.Opened.Should().Equal((8, saved), (9, null));
        actions.ShowInFolder(withoutFile);
        _shell.Shown.Should().Equal(_history.ImagePath(withoutFile));
    }

    [Fact]
    public async Task Save_as_copies_a_png_and_converts_other_formats()
    {
        var entry = await _history.AddPngAsync(Checkerboard(8, 8), null);
        var actions = Actions();

        var png = Path.Combine(_root, "copy.png");
        await actions.SaveAsAsync(entry, png);
        File.ReadAllBytes(png).Should().Equal(File.ReadAllBytes(_history.ImagePath(entry)));

        await actions.SaveAsAsync(entry, Path.Combine(_root, "copy.jpg"));
        _output.SavedFiles.Should().ContainSingle().Which.Format.Should().Be(ImageFileFormat.Jpeg);

        _notifier.ClickActions.Should().HaveCount(2).And.OnlyContain(a => a != null);
        _notifier.ClickActions[0]!();
        _shell.Shown.Should().Equal(png);
    }

    [Fact]
    public async Task A_missing_image_is_reported_instead_of_thrown()
    {
        var entry = await _history.AddPngAsync(Checkerboard(8, 8), null);
        File.Delete(_history.ImagePath(entry));

        await Actions().CopyAsync(entry);

        _notifier.Messages.Should().ContainSingle().Which.Text.Should().StartWith("Couldn't copy the capture");
    }

    [Fact]
    public async Task The_window_keeps_its_selection_and_moves_to_a_neighbour_after_a_removal()
    {
        var first = await _history.AddPngAsync(Checkerboard(8, 8), null);
        var second = await _history.AddPngAsync(Checkerboard(9, 9), null);
        var third = await _history.AddPngAsync(Checkerboard(10, 10), null);
        using var vm = ViewModel();

        vm.Items.Select(i => i.Entry).Should().Equal(third, second, first);
        vm.Summary.Should().StartWith("3 captures");
        vm.OpenCommand.CanExecute(null).Should().BeFalse("nothing selected yet");

        vm.Selected = vm.Items[1];
        await _history.AddPngAsync(Checkerboard(11, 11), null);
        vm.Selected!.Entry.Should().Be(second, "a new capture doesn't move the selection");

        await vm.RemoveCommand.ExecuteAsync(null);
        vm.Items.Select(i => i.Entry).Should().NotContain(second);
        vm.Selected!.Entry.Should().Be(first, "the next one down is selected");

        vm.ConfirmClear = _ => false;
        await vm.ClearCommand.ExecuteAsync(null);
        vm.Items.Should().HaveCount(3);

        vm.ConfirmClear = count => count == 3;
        await vm.ClearCommand.ExecuteAsync(null);
        vm.IsEmpty.Should().BeTrue();
        vm.Selected.Should().BeNull();
    }

    private static CapturedImage Checkerboard(int width, int height)
    {
        var image = CapturedImage.Allocate(width, height, new CaptureMetadata { Kind = CaptureKind.Region, SourceBounds = new PixelRect(0, 0, width, height) });
        var pixels = image.Pixels;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 4;
                var on = (x + y) % 2 == 0;
                pixels[i] = (byte)(on ? 255 : x * 5);
                pixels[i + 1] = (byte)(y * 9);
                pixels[i + 2] = (byte)(on ? 0 : 200);
                pixels[i + 3] = (byte)(x == 0 ? 128 : 255);
            }
        }

        return image;
    }

    /// <summary>A history in a temp folder with real PNGs (written by WPF), and no limit.</summary>
    private sealed class FakeHistory(string folder) : ICaptureHistory
    {
        private readonly List<CaptureHistoryEntry> _entries = [];
        private int _next;

        public event EventHandler? Changed;

        public IReadOnlyList<CaptureHistoryEntry> Entries => [.. _entries];

        public string ImagePath(CaptureHistoryEntry entry) => Path.Combine(folder, entry.Id + ".png");

        public string ThumbnailPath(CaptureHistoryEntry entry) => Path.Combine(folder, entry.Id + ".thumb.png");

        public async Task<CaptureHistoryEntry> AddPngAsync(CapturedImage image, string? savedPath)
        {
            using (image)
            {
                var entry = new CaptureHistoryEntry($"e{++_next}", DateTimeOffset.Now.AddMinutes(_next), CaptureKind.Region, image.Width, image.Height, SavedPath: savedPath);
                await using (var file = File.Create(ImagePath(entry)))
                {
                    WpfImaging.Encode(image.ToBitmapSource(), ImageFileFormat.Png, 100, keepAlpha: true, file);
                }

                _entries.Insert(0, entry);
                Changed?.Invoke(this, EventArgs.Empty);
                return entry;
            }
        }

        public Task<CaptureHistoryEntry?> AddAsync(CapturedImage image, string? savedPath, string? savedPngPath, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task RemoveAsync(string id)
        {
            _entries.RemoveAll(e => e.Id == id);
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task ClearAsync()
        {
            _entries.Clear();
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeEditor : IEditorLauncher
    {
        public List<(int Width, string? SavedPath)> Opened { get; } = [];

        public Task OpenAsync(CapturedImage image, string? savedPath, CancellationToken ct = default)
        {
            Opened.Add((image.Width, savedPath));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeShell : IShell
    {
        public List<string> Shown { get; } = [];

        public void ShowInFolder(string path) => Shown.Add(path);
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly System.Globalization.CultureInfo _previous = System.Globalization.CultureInfo.CurrentCulture;

        public CultureScope(string name) => System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(name);

        public void Dispose() => System.Globalization.CultureInfo.CurrentCulture = _previous;
    }
}
