using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Resources;
using CashGear.Capture.App.VideoEditor;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Engine.Tests;
using CashGear.Capture.Overlays.Tests;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Model;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace CashGear.Capture.App.Tests.VideoEditor;

/// <summary>The media panel in the real window, with real keys. Nothing plays sound.</summary>
[Trait("Category", InteractiveDesktop.Category)]
[Collection(DesktopCollection.Name)]
public sealed class MediaPanelWindowTests : VideoEditorTestBase
{
    private string Png(string name)
    {
        using var surface = SKSurface.Create(new SKImageInfo(40, 20, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Orange);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var path = Path.Combine(Root, name);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 60 && !done(); i++)
        {
            await Task.Delay(50);
        }
    }

    [Fact]
    public Task The_panel_opens_beside_the_preview_and_takes_the_keys() => StaDispatcher.RunAsync(async () =>
    {
        InteractiveDesktop.Require();
        using var input = TestInput.Exclusive();
        var vm = Open();
        await vm.ImportFilesAsync([Png("logo.png")], null, null);
        var window = new VideoEditorWindow(vm, AppTheme.Dark, tryGpu: false, NullLogger.Instance);
        window.Show();
        try
        {
            await Task.Delay(500);
            window.Activate();
            window.MediaArea2.IsVisible.Should().BeFalse("it opens when asked for");
            await VideoEditorKeyboardTests.Press(Key.M, Key.LeftCtrl);
            await Until(() => vm.Media.IsShown);
            vm.Media.IsShown.Should().BeTrue();
            await Task.Delay(300);
            window.MediaArea2.IsVisible.Should().BeTrue();
            window.MediaArea2.ActualWidth.Should().BeGreaterThan(150);

            // The list has names for a screen reader; Enter puts the chosen file at the playhead.
            var rows = window.MediaArea2.Rows;
            AutomationProperties.GetName(rows).Should().Be(VideoEditorText.MediaListName);
            rows.Focus();
            vm.Media.Selected = vm.Media.Items.Single(i => i.Name == "logo.png");
            await Task.Delay(100);
            (rows.ItemContainerGenerator.ContainerFromItem(vm.Media.Selected) as ListBoxItem)?.Focus();
            var peer = UIElementAutomationPeer.CreatePeerForElement(rows);
            peer.GetChildren().Should().Contain(p => p.GetName().Contains("logo.png"));
            await VideoEditorKeyboardTests.Press(Key.Enter);
            await Until(() => vm.Timeline.Selected is Clip);
            vm.Timeline.Selected.Should().BeOfType<Clip>().Which.AssetId.Should().Be(vm.Media.Selected.Asset.Id);

            // Delete on its row says it is still in use; undo makes it free again, and it goes.
            await VideoEditorKeyboardTests.Press(Key.Delete);
            Dialogs.Errors.Should().ContainSingle(e => e.Contains("logo.png"));
            await VideoEditorKeyboardTests.Press(Key.Z, Key.LeftCtrl);
            await Until(() => vm.Project.Tracks.All(t => t.Kind != TrackKind.Media));
            await VideoEditorKeyboardTests.Press(Key.Delete);
            await Until(() => vm.Media.Items.Count == 1);
            vm.Media.Items.Should().ContainSingle();

            // Off the lists, M adds a marker, and Ctrl+C and Ctrl+V copy what is selected on the timeline.
            window.TimelineSurface.Focus();
            await VideoEditorKeyboardTests.Press(Key.M);
            await Until(() => vm.Project.Markers is { Count: 1 });
            vm.Project.Markers.Should().ContainSingle();

            vm.Insert(AnnotationShape.Text);
            await VideoEditorKeyboardTests.Press(Key.C, Key.LeftCtrl);
            var before = vm.Project.Tracks.SelectMany(t => t.Items).Count();
            await VideoEditorKeyboardTests.Press(Key.V, Key.LeftCtrl);
            await Until(() => vm.Project.Tracks.SelectMany(t => t.Items).Count() > before);
            vm.Project.Tracks.SelectMany(t => t.Items).Count().Should().Be(before + 1);

            // Each tab shows its list.
            foreach (var tab in new[] { 1, 2, 3, 4 })
            {
                vm.Media.ShownTab = tab;
                await Task.Delay(100);
            }

            vm.Media.ShownTab = MediaPanelViewModel.TemplatesTabIndex;
            await Task.Delay(100);
            vm.Media.SelectedTemplate = vm.Media.Templates[0];
            vm.Media.InsertTemplateCommand.Execute(null);
            vm.Timeline.Selected.Should().BeOfType<TemplateItem>();
            await Task.Delay(200);
            window.PreviewPaints.Should().BeGreaterThan(0);
        }
        finally
        {
            vm.History.MarkSaved(null);
            window.Close();
        }
    });
}
