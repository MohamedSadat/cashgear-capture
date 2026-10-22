using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using CashGear.Capture.App.Editor;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Resources;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Editor.Document;
using CashGear.Capture.Editor.Effects;
using CashGear.Capture.Editor.Imaging;
using CashGear.Capture.Editor.Interaction;
using CashGear.Capture.Editor.Model;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace CashGear.Capture.App.Tests.Editor;

/// <summary>The editor for screen readers, the keyboard and high contrast, without a desktop: names, the canvas's peer, Enter and the arrows, colours.</summary>
public sealed class EditorAccessibilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cashgear-tests", Guid.NewGuid().ToString("N"));
    private readonly List<EditorViewModel> _created = [];

    public void Dispose()
    {
        _created.ForEach(vm => vm.Dispose());
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <param name="disposeLater">False for a view model a window binds to: the test disposes it on the window's thread (<see cref="DisposeOnThisThreadAsync"/>).</param>
    internal EditorViewModel Create(int width = 120, int height = 80, bool disposeLater = true)
    {
        var paths = new AppPaths(Path.Combine(_root, "settings"), Path.Combine(_root, "data", "logs"), Path.Combine(_root, "captures"));
        var metadata = new CaptureMetadata { Kind = CaptureKind.Region, SourceBounds = new PixelRect(0, 0, width, height), CapturedAt = DateTimeOffset.Now };
        var vm = new EditorViewModel(
            new EditorDocument(SkiaImaging.Draw(width, height, c => c.Clear(SKColors.White))), metadata, null, new ToolStyleMemory(),
            new FakeOutputService(), new MutableSettings(), paths, new FakeCounter(), new FakeEditorDialogs(), NullLogger<EditorViewModel>.Instance);
        if (disposeLater)
        {
            _created.Add(vm);
        }

        return vm;
    }

    /// <summary>Unbinds <paramref name="window"/> and lets queued work finish (bindings read the document), then frees the view model.</summary>
    internal static async Task DisposeOnThisThreadAsync(EditorViewModel vm, FrameworkElement? window = null)
    {
        if (window is not null)
        {
            window.DataContext = null;
        }

        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        vm.Dispose();
    }

    private static ISelectionItemProvider Item(AutomationPeer peer) => (ISelectionItemProvider)peer.GetPattern(PatternInterface.SelectionItem)!;

    private static List<T> Logical<T>(DependencyObject root)
        where T : DependencyObject
    {
        var found = new List<T>();
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match)
            {
                found.Add(match);
            }

            found.AddRange(Logical<T>(child));
        }

        return found;
    }

    [Fact]
    public Task The_canvas_peer_lists_the_annotations_by_name_and_selecting_one_selects_it() => StaDispatcher.RunAsync(async () =>
    {
        var vm = Create(disposeLater: false);
        var canvas = new EditorCanvas();
        canvas.Attach(vm);
        vm.Tool = EditorTool.Arrow;
        vm.Enter();
        vm.Tool = EditorTool.StepNumber;
        vm.Enter();
        var text = new TextAnnotation(vm.Document.NewId(), new AnnotationStyle(), new SKPoint(5, 5), "Hello");
        vm.Document.Execute(new AddAnnotationCommand(text));
        vm.Session.Select(null);

        var peer = UIElementAutomationPeer.CreatePeerForElement(canvas);
        peer.GetName().Should().Be(EditorText.Picture);
        peer.GetAutomationControlType().Should().Be(AutomationControlType.Custom);
        peer.GetLocalizedControlType().Should().Be(EditorText.CanvasControlType);
        var selection = (ISelectionProvider)peer.GetPattern(PatternInterface.Selection)!;
        selection.CanSelectMultiple.Should().BeFalse();

        var children = peer.GetChildren();
        children.Select(c => c.GetName()).Should().Equal("Arrow", "Step 1", "Text: Hello");
        children.Should().AllSatisfy(c => c.GetAutomationControlType().Should().Be(AutomationControlType.ListItem));

        Item(children[2]).IsSelected.Should().BeFalse();
        Item(children[2]).Select();
        vm.Session.SelectedId.Should().Be(text.Id);
        Item(children[2]).IsSelected.Should().BeTrue();
        Item(children[0]).AddToSelection();
        vm.Session.SelectedId.Should().Be(vm.Document.Annotations[0].Id, "one at a time");
        Item(children[0]).RemoveFromSelection();
        vm.Session.Selected.Should().BeNull();

        // A deleted annotation leaves; the others keep their peers, so a screen reader knows them again.
        vm.Session.Select(vm.Document.Annotations[1].Id);
        vm.Session.DeleteSelection();
        peer.ResetChildrenCache();
        var after = peer.GetChildren();
        after.Should().HaveCount(2);
        after[0].Should().BeSameAs(children[0]);
        after[1].Should().BeSameAs(children[2]);
        await DisposeOnThisThreadAsync(vm);
    });

    [Fact]
    public void Enter_adds_with_the_tool_then_with_crop_makes_a_frame_and_crops()
    {
        var vm = Create();

        vm.Tool = EditorTool.Rectangle;
        vm.Enter().Should().BeTrue();
        vm.Document.Annotations.Should().ContainSingle().Which.Should().BeOfType<RectangleAnnotation>();
        vm.HasSelection.Should().BeTrue("the arrows and Delete act on it next");

        vm.Tool = EditorTool.Crop;
        vm.Enter().Should().BeTrue();
        vm.IsCropping.Should().BeTrue();
        vm.Enter().Should().BeTrue();
        vm.Tool.Should().Be(EditorTool.Select);
        vm.Document.Width.Should().BeLessThan(120);

        vm.Enter().Should().BeFalse("the Select tool with nothing selected");
    }

    [Fact]
    public void Arrows_with_nothing_selected_scroll_the_view()
    {
        var vm = Create(400, 300);
        vm.Viewport.SetViewSize(new SKSize(100, 100));
        vm.Viewport.ActualSize();
        var before = vm.Viewport.Offset;

        vm.Nudge(1, 0);
        vm.Viewport.Offset.X.Should().Be(before.X - 20);
        vm.Nudge(0, 10);
        vm.Viewport.Offset.Y.Should().BeLessThan(before.Y);
        vm.Document.History.CanUndo.Should().BeFalse();
    }

    [Fact]
    public void Swatches_settings_and_effects_are_read_by_their_names()
    {
        new ColorSwatch(EditorColors.Red).Label.Should().Be(EditorText.ColourRed);
        new ColorSwatch(EditorColors.Red).ToString().Should().Be(EditorText.ColourRed);
        new ColorSwatch(SKColors.White).Label.Should().Be(EditorText.ColourWhite);
        new ColorSwatch(new SKColor(0x12, 0x34, 0x56)).Label.Should().Be("#123456");
        new ColorSwatch(SKColors.Transparent).Label.Should().Be(EditorText.NoneTransparent);
        EditorColors.Palette.Select(c => new ColorSwatch(c).Label).Should().NotContain(label => label.StartsWith('#'), "every palette colour has a name");

        new NumberParameter(EditorText.Width, 1, 10, 5).ToString().Should().Be(EditorText.Width);
        new AnchorParameter(EditorText.Position, Anchor.Center).Cells.Single(c => c.Anchor == Anchor.Center).ToString().Should().Be(EditorText.Centre);
        EffectItemViewModel.Kinds[0].ToString().Should().Be(EditorText.DropShadow);
        var context = new EffectEditorContext([], () => null, (_, s) => s, null);
        EffectItemViewModel.From(new ShadowEffect(), context).ToString().Should().Be(EditorText.DropShadow);
    }

    [Fact]
    public Task Under_high_contrast_the_canvas_surround_and_grips_take_the_system_colours() => StaDispatcher.RunAsync(async () =>
    {
        var vm = Create(disposeLater: false);
        EditorWindow? window = null;
        try
        {
            HighContrast.Override = false;
            CanvasColors.For(dark: true).Background.Should().Be(new SKColor(0x1E, 0x1E, 0x1E));
            CanvasColors.For(dark: false).HandleFill.Should().Be(SKColors.White);

            HighContrast.Override = true;
            var colors = CanvasColors.For(dark: true);
            colors.Background.Should().Be(HighContrast.ToSkia(SystemColors.WindowColor));
            colors.HandleFill.Should().Be(HighContrast.ToSkia(SystemColors.HighlightColor));
            colors.HandleOutline.Should().Be(HighContrast.ToSkia(SystemColors.HighlightTextColor));
            colors.LabelText.Should().Be(HighContrast.ToSkia(SystemColors.WindowTextColor));

            window = new EditorWindow(vm, AppTheme.Dark);
            window.Surface.Colors.Background.Should().Be(HighContrast.ToSkia(SystemColors.WindowColor));

            HighContrast.Override = false;
            window.ApplyTheme(AppTheme.Dark); // what ThemeService does when high contrast is turned off
            window.Surface.Colors.Background.Should().Be(new SKColor(0x1E, 0x1E, 0x1E));
        }
        finally
        {
            HighContrast.Override = null;
            await DisposeOnThisThreadAsync(vm, window);
        }
    });

    [Fact]
    public Task The_toolbar_buttons_are_tab_stops_with_names() => StaDispatcher.RunAsync(async () =>
    {
        var vm = Create(disposeLater: false);
        var window = new EditorWindow(vm, AppTheme.Light);
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle); // the bindings

        var tools = Logical<ToolButton>(window);
        tools.Should().HaveCount(15);
        tools.Should().AllSatisfy(b => b.Focusable.Should().BeTrue());
        tools.Select(b => UIElementAutomationPeer.CreatePeerForElement(b).GetName()).Should().Contain([EditorText.ToolSelect, EditorText.ToolArrow, EditorText.ToolMagnify])
            .And.OnlyHaveUniqueItems();

        var buttons = Logical<Button>(window).Where(b => b.Visibility == Visibility.Visible).ToList();
        buttons.Should().AllSatisfy(b => b.Focusable.Should().BeTrue());
        Name(buttons.Single(b => b.Command == vm.ZoomOutCommand)).Should().Be(EditorText.ZoomOut);
        Name(buttons.Single(b => b.Command == vm.ZoomInCommand)).Should().Be(EditorText.ZoomIn);
        buttons.Select(Name).Should().NotContain(string.Empty, "every button has a name");

        // The current colour's swatch is read out too.
        vm.ColorName.Should().Be(EditorText.CurrentColourIs(EditorText.ColourRed));
        Logical<Label>(window).Select(l => UIElementAutomationPeer.CreatePeerForElement(l).GetName()).Should().Contain(vm.ColorName);
        await DisposeOnThisThreadAsync(vm, window);

        static string Name(Button b) => UIElementAutomationPeer.CreatePeerForElement(b).GetName();
    });
}
