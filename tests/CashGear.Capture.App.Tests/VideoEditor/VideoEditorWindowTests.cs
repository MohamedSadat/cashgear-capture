using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CashGear.Capture.App.Editor;
using CashGear.Capture.App.VideoEditor;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Engine.Tests;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.App.Tests.VideoEditor;

/// <summary>The video editor's windows on the desktop: the preview draws (on the graphics card when it can), the
/// track headers and timeline are there, playing moves on, closing asks about unsaved changes. Nothing plays sound.</summary>
[Trait("Category", InteractiveDesktop.Category)]
[Collection(DesktopCollection.Name)]
public sealed class VideoEditorWindowTests : VideoEditorTestBase
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task The_window_shows_the_preview_and_the_tracks_and_plays(bool gpu) => StaDispatcher.RunAsync(async () =>
    {
        InteractiveDesktop.Require();
        var vm = Open();
        var window = new VideoEditorWindow(vm, AppTheme.Dark, gpu, NullLogger.Instance);
        window.Show();
        try
        {
            await Task.Delay(800);
            if (!gpu)
            {
                window.PreviewUsesGpu.Should().BeFalse();
            }

            vm.Status.Should().Contain(window.PreviewUsesGpu ? "preview on the graphics card" : "preview in memory");
            window.PreviewPaints.Should().BeGreaterThan(0, "the first frame is drawn");
            Find<TextBlock>(window).Select(t => t.Text).Should().Contain(["Video", "Microphone", "System audio", "0:00.00 / 0:04.00"]);
            Find<Button>(window).Select(b => b.Content as string).Should().Contain(["Split", "Delete", "Export…"]);

            var before = window.PreviewPaints;
            vm.PlayPauseCommand.Execute(null);
            vm.IsPlaying.Should().BeTrue();
            await Task.Delay(1000);
            vm.PlayPauseCommand.Execute(null);
            vm.Position.Should().BeInRange(0.6, 1.6, "about a second played");
            (window.PreviewPaints - before).Should().BeGreaterThan(15, "frames were drawn while playing");
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"Preview asked for the graphics card: {gpu}; on it: {window.PreviewUsesGpu}; {window.PreviewPaints - before} frames drawn in a second of playing; decoding on the graphics card: {vm.Frames.IsHardware}");
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Closing_with_unsaved_changes_asks_first() => StaDispatcher.RunAsync(async () =>
    {
        InteractiveDesktop.Require();
        var vm = Open();
        var window = new VideoEditorWindow(vm, AppTheme.Light, tryGpu: false, NullLogger.Instance);
        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show();
        try
        {
            await Task.Delay(400);
            vm.Seek(1);
            vm.SplitCommand.Execute(null);
            window.Close();
            await Task.Delay(300);
            Dialogs.SaveQuestions.Should().Be(1);
            closed.Should().BeFalse("the question was cancelled");

            Dialogs.Choice = SaveChangesChoice.Discard;
            (await window.RequestCloseAsync()).Should().BeTrue();
            closed.Should().BeTrue();
        }
        finally
        {
            if (!closed)
            {
                Dialogs.Choice = SaveChangesChoice.Discard;
                window.Close();
            }
        }
    });

    [Fact]
    public Task The_export_window_shows_its_formats_and_buttons() => StaDispatcher.RunAsync(async () =>
    {
        InteractiveDesktop.Require();
        using var vm = Open();
        vm.ExportCommand.Execute(null);
        var window = new ExportWindow(Dialogs.Export!, AppTheme.Dark);
        window.Show();
        try
        {
            await Task.Delay(500);
            Find<ComboBox>(window).Where(c => c.IsVisible).Should().HaveCount(2, "the GIF choices are hidden for an MP4");
            Find<ComboBox>(window)[0].Items.Count.Should().Be(9);
            Find<Button>(window).Select(b => b.Content as string).Should().Contain(["Browse…", "Export", "Close"]);
        }
        finally
        {
            window.Close();
        }
    });
}
