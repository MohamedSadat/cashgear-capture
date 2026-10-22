using System.Windows.Controls;
using System.Windows.Input;
using CashGear.Capture.App.ImageTools;
using CashGear.Capture.App.Tests.Editor;
using CashGear.Capture.App.Tests.Platform;
using CashGear.Capture.Core.Settings;
using SkiaSharp;

namespace CashGear.Capture.App.Tests.ImageTools;

/// <summary>The image tools' windows for a screen reader and from the keyboard (laid out, not shown).</summary>
public sealed class ImageToolAccessibilityTests : ImageToolTestBase
{
    [Fact]
    public Task The_batch_window_names_its_pictures_puts_its_buttons_last_and_closes_with_Esc() => StaDispatcher.RunAsync(() =>
    {
        var vm = new BatchViewModel(Presets, Picker, Shell, Settings, Paths);
        vm.List.Add([Picture("first.png", 10, 10, SKColors.Red), Picture("second.jpg", 10, 10, SKColors.Blue)]);
        var window = new BatchWindow(vm, AppTheme.Light);
        var closed = false;
        window.Closed += (_, _) => closed = true;
        var content = TestLayout.Layout(window);

        var list = TestLayout.FindAll<ListBox>(content).Single();
        TestLayout.ItemNames(list).Should().Equal(["first.png", "second.jpg"], "a screen reader hears the file names");

        var controls = TestLayout.FindAll<Control>(content).Where(c => c.Focusable && c is not ListBoxItem).ToList();
        var start = controls.OfType<Button>().Single(b => b.IsDefault);
        controls.IndexOf(start).Should().Be(controls.Count - 1, "Start, the default button, comes last in Tab order");
        controls.IndexOf(list).Should().BeLessThan(controls.IndexOf(controls.OfType<Button>().First(b => Equals(b.Content, "Add files…"))),
            "the list comes before its buttons");

        TestKeys.Press(list, Key.Escape).Should().BeTrue();
        closed.Should().BeTrue("Esc closes the window");
        return Task.CompletedTask;
    });

    [Fact]
    public Task The_combine_and_pdf_windows_close_with_Esc() => StaDispatcher.RunAsync(() =>
    {
        var combine = new CombineWindow(new CombineViewModel(Presets, Picker, new NoEditor(), new FakeOutputService(), Settings, Paths), AppTheme.Dark);
        var pdf = new PdfWindow(new PdfToolViewModel(Presets, Picker, Shell, Settings, Paths), AppTheme.Light);
        var closed = 0;
        combine.Closed += (_, _) => closed++;
        pdf.Closed += (_, _) => closed++;

        TestKeys.Press(TestLayout.FindAll<ComboBox>(TestLayout.Layout(pdf)).First(), Key.Escape).Should().BeTrue();
        TestKeys.Press(TestLayout.Layout(combine), Key.Escape).Should().BeTrue();
        closed.Should().Be(2);
        return Task.CompletedTask;
    });

    private sealed class NoEditor : CashGear.Capture.App.Editor.IEditorLauncher
    {
        public Task OpenAsync(Core.Imaging.CapturedImage image, string? savedPath, IReadOnlyList<WorkflowStep>? pending = null, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
