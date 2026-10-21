using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CashGear.Capture.App.ImageTools;
using CashGear.Capture.App.Tests.Editor;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Engine.Tests;
using SkiaSharp;

namespace CashGear.Capture.App.Tests.ImageTools;

/// <summary>The tool windows' XAML on the desktop: the picture list, the settings and the buttons are there.</summary>
[Trait("Category", InteractiveDesktop.Category)]
[Collection(DesktopCollection.Name)]
public sealed class ImageToolWindowsTests : ImageToolTestBase
{
    private static List<T> Find<T>(DependencyObject root)
        where T : DependencyObject
    {
        var found = new List<T>();
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                found.Add(match);
            }

            found.AddRange(Find<T>(child));
        }

        return found;
    }

    private static async Task<(int Pictures, IReadOnlyList<string> Buttons)> ShowAsync(Window window)
    {
        window.Show();
        try
        {
            await Task.Delay(700);
            var list = Find<ListBox>(window).Single(l => System.Windows.Automation.AutomationProperties.GetName(l) == "Pictures");
            return (list.Items.Count, [.. Find<Button>(window).Select(b => b.Content as string ?? string.Empty)]);
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public Task The_batch_window_shows_its_list_and_steps() => StaDispatcher.RunAsync(async () =>
    {
        InteractiveDesktop.Require();
        var vm = new BatchViewModel(Presets, Picker, Shell, Settings, Paths);
        vm.List.Add([Picture("a.png", 20, 20, SKColors.Red)]);

        var (pictures, buttons) = await ShowAsync(new BatchWindow(vm, AppTheme.Light));

        pictures.Should().Be(1);
        buttons.Should().Contain(["Add files…", "Add a folder…", "Browse…", "Start", "Cancel"]);
    });

    [Fact]
    public Task The_combine_window_shows_its_list_settings_and_preview() => StaDispatcher.RunAsync(async () =>
    {
        InteractiveDesktop.Require();
        var vm = new CombineViewModel(Presets, Picker, new NoEditor(), new FakeOutputService(), Settings, Paths) { PreviewDelay = TimeSpan.Zero };
        vm.List.Add([Picture("a.png", 20, 20, SKColors.Red), Picture("b.png", 20, 20, SKColors.Blue)]);
        await vm.RefreshPreviewAsync();

        var (pictures, buttons) = await ShowAsync(new CombineWindow(vm, AppTheme.Dark));

        pictures.Should().Be(2);
        buttons.Should().Contain(["Open in the editor", "Save as…", "None (transparent)"]);
        vm.Preview.Should().NotBeNull();
    });

    [Fact]
    public Task The_pdf_window_shows_its_list_and_page_settings() => StaDispatcher.RunAsync(async () =>
    {
        InteractiveDesktop.Require();
        var vm = new PdfToolViewModel(Presets, Picker, Shell, Settings, Paths);

        var (pictures, buttons) = await ShowAsync(new PdfWindow(vm, AppTheme.Light));

        pictures.Should().Be(0);
        buttons.Should().Contain(["Make the PDF…", "Open the PDF", "Show in folder"]);
    });

    private sealed class NoEditor : CashGear.Capture.App.Editor.IEditorLauncher
    {
        public Task OpenAsync(Core.Imaging.CapturedImage image, string? savedPath, IReadOnlyList<WorkflowStep>? pending = null, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
