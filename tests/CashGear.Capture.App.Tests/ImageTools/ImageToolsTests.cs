using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Threading;
using CashGear.Capture.App.Editor;
using CashGear.Capture.App.ImageTools;
using CashGear.Capture.App.Tests.Editor;
using CashGear.Capture.App.Platform;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Editor.Batch;
using CashGear.Capture.Editor.Composition;
using CashGear.Capture.Editor.Effects;
using CashGear.Capture.Editor.Imaging;
using CashGear.Capture.Editor.Presets;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace CashGear.Capture.App.Tests.ImageTools;

/// <summary>Pictures and folders for the image tools' tests, deleted afterwards.</summary>
public abstract class ImageToolTestBase : IDisposable
{
    protected ImageToolTestBase()
    {
        Root = Path.Combine(Path.GetTempPath(), "cashgear-tests", Guid.NewGuid().ToString("N"));
        Paths = new AppPaths(Path.Combine(Root, "settings"), Path.Combine(Root, "data", "logs"), Path.Combine(Root, "captures"));
        Presets = new JsonPresetStore(Paths, NullLogger<JsonPresetStore>.Instance);
    }

    protected string Root { get; }

    protected AppPaths Paths { get; }

    protected JsonPresetStore Presets { get; }

    private protected FakePicker Picker { get; } = new();

    private protected ShellSpy Shell { get; } = new();

    private protected MutableSettings Settings { get; } = new();

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    protected string Picture(string name, int width, int height, SKColor color)
    {
        var path = Path.Combine(Root, "in", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var image = SkiaImaging.Draw(width, height, c => c.Clear(color));
        using var stream = File.Create(path);
        ImageFiles.Write(image, stream, ImageFileFormatExtensions.FromPath(path) ?? ImageFileFormat.Png);
        return path;
    }

    internal sealed class FakePicker : IImageFilePicker
    {
        public List<string> Images { get; } = [];

        public string? Folder { get; set; }

        public string? SavePath { get; set; }

        public IReadOnlyList<string> PickImages(string title) => Images;

        public string? PickFolder(string title, string? initialFolder) => Folder;

        public string? PickSavePath(string title, string filter, string fileName, string? folder) => SavePath;
    }

    internal sealed class ShellSpy : IShell
    {
        public List<string> Shown { get; } = [];

        public List<string> Opened { get; } = [];

        public void ShowInFolder(string path) => Shown.Add(path);

        public void OpenLink(string url) => throw new NotSupportedException();

        public void OpenFile(string path) => Opened.Add(path);
    }
}

public sealed class ImageToolsTests : ImageToolTestBase
{
    [Fact]
    public void The_list_takes_pictures_and_folders_once_each()
    {
        var a = Picture("a.png", 10, 10, SKColors.Red);
        var b = Picture(@"sub\b.jpg", 10, 10, SKColors.Blue);
        File.WriteAllText(Path.Combine(Root, "in", "notes.txt"), "not a picture");
        var list = new ImageListViewModel(Picker);

        list.Add([a, a, Path.Combine(Root, "in", "notes.txt"), Path.Combine(Root, "in", "gone.png")]);
        list.Files.Select(f => f.Name).Should().Equal("a.png");
        list.Add([Path.Combine(Root, "in")]);
        list.Files.Should().HaveCount(1, "the folder alone holds only a.png");
        list.IncludeSubfolders = true;
        Picker.Folder = Path.Combine(Root, "in");
        list.AddFolderCommand.Execute(null);
        list.Files.Select(f => f.Name).Should().Equal("a.png", "b.jpg");
        list.CountText.Should().Be("2 pictures");

        list.Selected = list.Files[1];
        list.MoveUpCommand.Execute(null);
        list.Paths.Should().Equal(b, a);
        list.RemoveCommand.Execute(null);
        list.Paths.Should().Equal(a);
        list.ClearCommand.Execute(null);
        list.CountText.Should().StartWith("No pictures");
    }

    [Fact]
    public void The_batch_shows_an_example_name_and_refuses_bad_templates()
    {
        var vm = new BatchViewModel(Presets, Picker, Shell, Settings, Paths);
        vm.StartCommand.CanExecute(null).Should().BeFalse("there are no pictures yet");
        vm.List.Add([Picture("shot.png", 40, 20, SKColors.Red)]);

        vm.NameTemplate = "{counter}_{name}_{width}";
        vm.Format = vm.Formats.Single(f => Equals(f.Value, BatchFormat.Jpeg));
        vm.Resize = vm.ResizeModes.Single(r => Equals(r.Value, ResizeMode.Percent));

        vm.NameExample.Should().Be("1_shot_960.jpg", "1920 at 50%, as an example");
        vm.StartCommand.CanExecute(null).Should().BeTrue();
        vm.NameTemplate = "{nope}";
        vm.NameError.Should().Contain("{nope}");
        vm.StartCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task The_batch_uses_the_presets_and_remembers_its_choices()
    {
        await Presets.UpdateAsync(p => p.WithEffects(new EffectPreset("Grey", [new GrayscaleEffect()])), TestContext.Current.CancellationToken);
        var vm = new BatchViewModel(Presets, Picker, Shell, Settings, Paths);
        vm.List.Add([Picture("one.png", 40, 20, SKColors.Red), Picture("two.png", 40, 20, SKColors.Red)]);
        vm.EffectsPreset = "Grey";
        vm.WatermarkPreset = vm.WatermarkPresets[1];
        vm.OutputFolder = Path.Combine(Root, "out");

        await vm.StartCommand.ExecuteAsync(null);

        vm.Result!.Written.Should().HaveCount(2);
        vm.Status.Should().StartWith("Done in").And.Contain("2 saved");
        vm.Progress.Should().Be(100);
        using var result = ImageFiles.Read(vm.Result.Written[0]);
        var pixel = SkiaImaging.ReadUnpremul(result);
        (pixel[0] == pixel[1] && pixel[1] == pixel[2]).Should().BeTrue("grey");
        Presets.Current.Batch!.EffectsPreset.Should().Be("Grey");
        new BatchViewModel(Presets, Picker, Shell, Settings, Paths).EffectsPreset.Should().Be("Grey", "the choice is remembered");
        vm.OpenOutputCommand.Execute(null);
        Shell.Shown.Should().Equal(vm.Result.Written[0]);
    }

    [Fact]
    public async Task Combine_opens_the_result_in_the_editor_or_saves_it()
    {
        var editor = new EditorSpy();
        var output = new FakeOutputService();
        var vm = new CombineViewModel(Presets, Picker, editor, output, Settings, Paths) { PreviewDelay = TimeSpan.Zero };
        vm.List.Add([Picture("a.png", 100, 50, SKColors.Red), Picture("b.png", 60, 80, SKColors.Blue)]);
        vm.Spacing = 10;

        await vm.RefreshPreviewAsync();
        vm.Preview.Should().NotBeNull();
        vm.SizeText.Should().Be("170 × 80 pixels");

        await vm.OpenInEditorCommand.ExecuteAsync(null);
        editor.Sizes.Should().Equal((170, 80));

        Picker.SavePath = Path.Combine(Root, "combined.png");
        await vm.SaveAsCommand.ExecuteAsync(null);
        output.SavedFiles.Should().ContainSingle().Which.Path.Should().Be(Picker.SavePath);
        Presets.Current.Combine!.Spacing.Should().Be(10);

        vm.Layout = vm.Layouts.Single(l => Equals(l.Value, CombineLayout.Vertical));
        await vm.RefreshPreviewAsync();
        vm.SizeText.Should().Be("100 × 140 pixels");
    }

    [Fact]
    public async Task Images_to_pdf_writes_the_pages_and_can_open_them()
    {
        var vm = new PdfToolViewModel(Presets, Picker, Shell, Settings, Paths);
        vm.List.Add([Picture("a.png", 300, 100, SKColors.Red), Picture("b.png", 100, 300, SKColors.Blue), Picture("c.jpg", 50, 50, SKColors.Green)]);
        vm.Columns = 2;
        Picker.SavePath = Path.Combine(Root, "pictures.pdf");

        await vm.MakeCommand.ExecuteAsync(null);

        vm.MadeFile.Should().Be(Picker.SavePath);
        vm.Status.Should().Be("Saved pictures.pdf (3 pictures).");
        var text = Encoding.Latin1.GetString(await File.ReadAllBytesAsync(Picker.SavePath, TestContext.Current.CancellationToken));
        System.Text.RegularExpressions.Regex.Matches(text, "/MediaBox").Count.Should().Be(2, "two pictures per page");
        vm.OpenCommand.Execute(null);
        Shell.Opened.Should().Equal(Picker.SavePath);
        Presets.Current.Pdf!.Columns.Should().Be(2);
    }

    [Fact]
    public async Task Nothing_is_made_when_the_save_is_cancelled()
    {
        var vm = new PdfToolViewModel(Presets, Picker, Shell, Settings, Paths);
        vm.List.Add([Picture("a.png", 30, 10, SKColors.Red)]);
        Picker.SavePath = null;

        await vm.MakeCommand.ExecuteAsync(null);

        vm.MadeFile.Should().BeNull();
        vm.OpenCommand.CanExecute(null).Should().BeFalse();
    }

    private sealed class EditorSpy : IEditorLauncher
    {
        public List<(int Width, int Height)> Sizes { get; } = [];

        public Task OpenAsync(CapturedImage image, string? savedPath, IReadOnlyList<WorkflowStep>? pending = null, CancellationToken ct = default)
        {
            Sizes.Add((image.Width, image.Height));
            return Task.CompletedTask;
        }
    }
}

[CollectionDefinition(nameof(BatchResponsiveness), DisableParallelization = true)]
public sealed class BatchResponsiveness;

/// <summary>The acceptance criterion: a batch of 500 screenshots never holds up the window's thread.</summary>
[Collection(nameof(BatchResponsiveness))]
public sealed class BatchResponsivenessTests : ImageToolTestBase
{
    [Fact]
    public Task Five_hundred_screenshots_leave_the_window_responsive() => StaDispatcher.RunAsync(async () =>
    {
        var folder = Path.Combine(Root, "shots");
        Directory.CreateDirectory(folder);
        var files = new string[500];
        Parallel.For(0, files.Length, i =>
        {
            using var shot = SkiaImaging.Draw(800, 500, c =>
            {
                c.Clear(new SKColor(0xF3, 0xF3, 0xF3));
                using var ink = new SKPaint { Color = new SKColor(0x20, 0x20, 0x20) };
                for (var line = 0; line < 20; line++)
                {
                    c.DrawRect(40, 40 + (line * 20), 100 + (((i * 31) + (line * 17)) % 600), 8, ink);
                }
            });
            files[i] = Path.Combine(folder, $"shot{i:000}.png");
            using var stream = File.Create(files[i]);
            ImageFiles.Write(shot, stream, ImageFileFormat.Png);
        });
        var vm = new BatchViewModel(Presets, Picker, Shell, Settings, Paths);
        vm.List.Add([folder]);
        vm.Resize = vm.ResizeModes.Single(r => Equals(r.Value, ResizeMode.Percent));
        vm.Percent = 60;
        vm.EffectsPreset = "Torn bottom with a shadow";
        vm.Format = vm.Formats.Single(f => Equals(f.Value, BatchFormat.Jpeg));
        vm.OutputFolder = Path.Combine(Root, "out");

        var gaps = new List<double>();
        var last = Stopwatch.GetTimestamp();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Normal, (_, _) =>
        {
            var now = Stopwatch.GetTimestamp();
            gaps.Add(Stopwatch.GetElapsedTime(last, now).TotalMilliseconds);
            last = now;
        }, Dispatcher.CurrentDispatcher);
        timer.Start();
        var clock = Stopwatch.StartNew();
        await vm.StartCommand.ExecuteAsync(null);
        clock.Stop();
        timer.Stop();

        vm.Result!.Written.Should().HaveCount(500, string.Join("; ", vm.Failures));
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"500 screenshots in {clock.Elapsed.TotalSeconds:0.0} s; the UI thread ran {gaps.Count} times, the longest wait {gaps.Max():0} ms, 99% within {gaps.Order().ElementAt((int)(gaps.Count * 0.99)):0} ms.");
        gaps.Count.Should().BeGreaterThan(50);
        gaps.Max().Should().BeLessThan(250, "the window's thread is never held up");
    });
}
