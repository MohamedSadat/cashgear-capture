using System.IO;
using CashGear.Capture.App.Editor;
using CashGear.Capture.App.Output;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Editor.Document;
using CashGear.Capture.Editor.Imaging;
using CashGear.Capture.Editor.Interaction;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace CashGear.Capture.App.Tests.Editor;

/// <summary>The editor's Send to menu and its Continue button (the workflow's steps after the editor).</summary>
public sealed class EditorSendTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cashgear-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeOutputs _outputs = new();
    private readonly List<EditorViewModel> _created = [];

    public void Dispose()
    {
        _created.ForEach(vm => vm.Dispose());
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private EditorViewModel Create(IReadOnlyList<WorkflowStep>? pending = null, string? filePath = null, bool withOutputs = true)
    {
        var paths = new AppPaths(Path.Combine(_root, "settings"), Path.Combine(_root, "data", "logs"), Path.Combine(_root, "captures"));
        var metadata = new CaptureMetadata { Kind = CaptureKind.Region, SourceBounds = new PixelRect(0, 0, 60, 40) };
        var vm = new EditorViewModel(
            new EditorDocument(SkiaImaging.Draw(60, 40, c => c.Clear(SKColors.White))),
            metadata,
            filePath,
            new ToolStyleMemory(),
            new FakeOutputService(),
            new MutableSettings(),
            paths,
            new FakeCounter(),
            new FakeEditorDialogs(),
            NullLogger<EditorViewModel>.Instance,
            withOutputs ? _outputs : null,
            pending);
        _created.Add(vm);
        return vm;
    }

    private static void Draw(EditorViewModel vm)
    {
        vm.Tool = EditorTool.Rectangle;
        vm.Session.PointerDown(new SKPoint(5, 5), PointerModifiers.None, 1, 4);
        vm.Session.PointerUp(new SKPoint(30, 20), PointerModifiers.None);
    }

    [Fact]
    public async Task Continue_runs_the_waiting_steps_once_on_the_edited_image()
    {
        var vm = Create(pending: [new WorkflowStep("upload:imgs"), new WorkflowStep("copy-link")]);
        Draw(vm);

        vm.HasPending.Should().BeTrue();
        vm.ContinueText.Should().Be("Continue: upload:imgs → copy-link");
        await vm.ContinueCommand.ExecuteAsync(null);

        var (steps, savedPath, width, edited) = _outputs.Sent.Single();
        steps.Should().Equal(new WorkflowStep("upload:imgs"), new WorkflowStep("copy-link"));
        savedPath.Should().BeNull("the image has unsaved changes, so it is sent as it is now");
        width.Should().Be(60);
        edited.Should().BeTrue("the annotations are in the image sent");
        vm.HasPending.Should().BeFalse();
        vm.ContinueCommand.CanExecute(null).Should().BeFalse();
        vm.Message.Should().Be("Sent");
    }

    [Fact]
    public async Task A_file_step_makes_its_file_the_editors_file()
    {
        _outputs.SaveTo = Path.Combine(_root, "Saved.png");
        var vm = Create(pending: [new WorkflowStep("file")]);
        Draw(vm);

        await vm.ContinueCommand.ExecuteAsync(null);

        vm.FilePath.Should().Be(_outputs.SaveTo);
        vm.IsDirty.Should().BeFalse();
    }

    [Fact]
    public async Task Send_to_lists_the_destinations_and_sends_the_saved_file_when_nothing_changed()
    {
        var vm = Create(filePath: @"C:\shots\shot.png");

        vm.CanSend.Should().BeTrue();
        vm.SendTargets.Should().BeEmpty("listed when the menu opens");
        vm.RefreshSendTargets();
        vm.SendTargets.Select(t => (t.Id, t.Name)).Should().Equal(("file", "Save to file"), ("printer", "Print"));
        await vm.SendTargets[1].Send.ExecuteAsync(null);

        var (steps, savedPath, _, _) = _outputs.Sent.Single();
        steps.Should().Equal(new WorkflowStep("printer"));
        savedPath.Should().Be(@"C:\shots\shot.png");
    }

    [Fact]
    public void Without_destinations_there_is_nothing_to_send_or_continue()
    {
        var vm = Create(pending: [new WorkflowStep("file")], withOutputs: false);

        vm.CanSend.Should().BeFalse();
        vm.HasPending.Should().BeFalse();
        vm.ContinueText.Should().BeEmpty();
    }

    private sealed class FakeOutputs : IEditorOutputs
    {
        public List<(IReadOnlyList<WorkflowStep> Steps, string? SavedPath, int Width, bool Edited)> Sent { get; } = [];

        public string? SaveTo { get; set; }

        public IReadOnlyList<SendTarget> Targets() => [new("file", "Save to file"), new("printer", "Print")];

        public string Describe(IReadOnlyList<WorkflowStep> steps) => string.Join(" → ", steps.Select(s => s.Destination));

        public Task<EditorSendResult> SendAsync(CapturedImage image, IReadOnlyList<WorkflowStep> steps, string? savedPath, CancellationToken ct = default)
        {
            // Anything but white means the rectangle was burned in.
            var edited = false;
            var pixels = image.Pixels;
            for (var i = 0; i < pixels.Length && !edited; i += 4)
            {
                edited = pixels[i] != 0xFF || pixels[i + 1] != 0xFF || pixels[i + 2] != 0xFF;
            }

            Sent.Add((steps, savedPath, image.Width, edited));
            var saved = steps.Any(s => s.Destination == "file") ? SaveTo : null;
            return Task.FromResult(new EditorSendResult(saved, "Sent", true));
        }
    }
}
