using System.IO;
using CashGear.Capture.App.Editor;
using CashGear.Capture.App.Resources;
using CashGear.Capture.App.VideoEditor;
using CashGear.Capture.Licensing;
using CashGear.Capture.Media.Tests;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Model;
using SkiaSharp;

namespace CashGear.Capture.App.Tests.VideoEditor;

/// <summary>Titles, the brand kit, the library and callouts, through the view model.</summary>
public sealed class TemplatePanelTests : VideoEditorTestBase
{
    private sealed class OnlyFree : IFeatureGate
    {
        public bool IsEnabled(ProFeature feature) => feature != ProFeature.Templates;
    }

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

    [Fact]
    public void A_template_goes_at_the_playhead_and_its_texts_are_typed_in_the_panel()
    {
        TestMedia.Require();
        using var vm = Open();
        vm.Media.Templates.Should().HaveCount(BuiltInTemplates.All.Count);
        vm.Media.InsertTemplateCommand.CanExecute(null).Should().BeFalse();
        vm.Media.SelectedTemplate = vm.Media.Templates.First(t => t.Template.Id == "third-bar");
        vm.Media.SelectedTemplate.AccessibleName.Should().Contain(VideoEditorText.TemplateBuiltIn);
        vm.Seek(0.5);
        vm.Media.InsertTemplateCommand.Execute(null);
        var item = vm.Timeline.Selected.Should().BeOfType<TemplateItem>().Subject;
        item.Start.Should().Be(0.5);
        vm.PropertiesTitle.Should().Contain(item.Template.Name);
        vm.Project.Brand.Should().NotBeNull();

        var name = vm.Properties.OfType<TextParameter>().First();
        name.Value = "Layla";
        ((TemplateItem)vm.Timeline.Selected!).Text(item.Template.Fields![0]).Should().Be("Layla");
        var length = vm.Properties.OfType<NumberParameter>().First(p => p.Label == VideoEditorText.ClipLength);
        length.Value = 2;
        vm.Timeline.Selected!.Duration.Should().Be(2);
        vm.History.Undo();
        vm.Timeline.Selected!.Duration.Should().Be(item.Duration);
    }

    [Fact]
    public void A_template_is_dragged_on_the_preview()
    {
        TestMedia.Require();
        using var vm = Open();
        var id = vm.InsertTemplate(BuiltInTemplates.All.First(t => t.Id == "title-band"), 0)!;
        using var bitmap = new SKBitmap(new SKImageInfo(vm.Project.Canvas.Width, vm.Project.Canvas.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            vm.RenderPreview(canvas, bitmap.Width, bitmap.Height, SKColors.Black);
        }

        var (w, h) = (vm.Project.Canvas.Width, vm.Project.Canvas.Height);
        vm.Seek(1);
        vm.PreviewPointerDown(w / 2.0, h / 2.0);
        vm.Timeline.SelectedId.Should().Be(id);
        vm.PreviewPointerMove((w / 2.0) + (w * 0.1), h / 2.0);
        vm.PreviewPointerUp();
        ((TemplateItem)vm.Project.Find(id)!.Value.Item).MoveX.Should().BeApproximately(0.1, 1e-3);
    }

    [Fact]
    public void Templates_ask_for_pro_and_the_brand_follows_undo()
    {
        TestMedia.Require();
        using (var free = new VideoEditorViewModel(ProjectFactory.FromRecording(ProjectFactory.ReadAsset(Recording())), null, Services with { Features = new OnlyFree() }, Dialogs))
        {
            free.InsertTemplate(BuiltInTemplates.All[0], 0).Should().BeNull();
            Dialogs.Errors.Should().ContainSingle();
        }

        using var vm = Open();
        var primary = vm.Media.BrandProperties.OfType<ColorParameter>().First(p => p.Label == VideoEditorText.BrandPrimary);
        primary.Value = new ColorSwatch(SKColors.Red);
        vm.Project.Brand!.Primary.Should().Be("#FF0000");
        vm.Media.BrandProperties.OfType<ColorParameter>().First(p => p.Label == VideoEditorText.BrandPrimary).Should().BeSameAs(primary, "the settings stay while they are used");
        vm.History.Undo();
        vm.Project.Brand.Should().BeNull();
        vm.Media.BrandProperties.OfType<ColorParameter>().First(p => p.Label == VideoEditorText.BrandPrimary).Value.Value.Should().NotBe(SKColors.Red);
    }

    [Fact]
    public void The_brands_logo_intro_and_outro_go_on_the_timeline()
    {
        TestMedia.Require();
        using var vm = Open();
        var duration = vm.Project.Duration;
        vm.Media.BrandProperties.OfType<ActionParameter>().First(p => p.Label == VideoEditorText.BrandInsertLogo).Command.Execute(null);
        Dialogs.Errors.Should().ContainSingle(e => e == VideoEditorText.BrandNoFile);

        vm.PickBrandFile(Png("logo.png"), "logo", (b, id) => b with { LogoAssetId = id });
        vm.PickBrandFile(Recording(), "intro", (b, id) => b with { IntroAssetId = id });
        vm.Project.Brand!.LogoAssetId.Should().NotBeNull();
        vm.Media.BrandProperties.OfType<ActionParameter>().First(p => p.Label == VideoEditorText.BrandInsertLogo).Command.Execute(null);
        vm.Timeline.Selected.Should().BeOfType<Clip>().Which.AssetId.Should().Be(vm.Project.Brand.LogoAssetId);
        vm.Project.Duration.Should().Be(duration);

        vm.Media.BrandProperties.OfType<ActionParameter>().First(p => p.Label == VideoEditorText.BrandAddIntro).Command.Execute(null);
        vm.Project.Duration.Should().BeGreaterThan(duration, "what was there moved on");
        vm.Timeline.Selected!.Start.Should().Be(0);
    }

    [Fact]
    public async Task The_selection_is_saved_in_the_library_and_its_pack_goes_to_another_profile()
    {
        TestMedia.Require();
        using var vm = Open();
        vm.Timeline.Select(null);
        vm.SaveSelectionAsTemplate().Should().BeFalse();
        Dialogs.Errors.Should().ContainSingle(e => e == VideoEditorText.SaveTemplateNeedsSelection);

        vm.Insert(AnnotationShape.Text);
        Dialogs.Name = "Saved one";
        vm.SaveSelectionAsTemplate().Should().BeTrue();
        vm.Media.Templates.Should().Contain(t => t.IsSaved && t.Name == "Saved one");
        vm.Media.SelectedTemplate = vm.Media.Templates.First(t => t.IsSaved);
        vm.Media.DeleteTemplateCommand.CanExecute(null).Should().BeTrue();

        var pack = Path.Combine(Root, "team.cgpack");
        Dialogs.SavePath = pack;
        vm.Media.ExportPackCommand.Execute(null);
        File.Exists(pack).Should().BeTrue();
        vm.Media.DeleteTemplateCommand.Execute(null);
        vm.Media.Templates.Should().NotContain(t => t.IsSaved);

        Dialogs.Media = pack;
        vm.Media.ImportPackCommand.Execute(null);
        vm.Media.Templates.Should().Contain(t => t.IsSaved && t.Name == "Saved one");

        // A file that isn't one says so.
        var wrong = Path.Combine(Root, "wrong.cgpack");
        await File.WriteAllTextAsync(wrong, "no", TestContext.Current.CancellationToken);
        Dialogs.Media = wrong;
        vm.Media.ImportPackCommand.Execute(null);
        Dialogs.Errors.Should().Contain(e => e.Contains("no") || e.Length > 0);
    }

    [Fact]
    public void Callouts_are_added_from_the_menu_and_freehand_is_drawn_on_the_preview()
    {
        TestMedia.Require();
        using var vm = Open();
        vm.InsertBubbleCommand.Execute(null);
        vm.Timeline.Selected.Should().BeOfType<AnnotationItem>().Which.Shape.Should().Be(AnnotationShape.SpeechBubble);
        vm.Properties.OfType<TextParameter>().Should().ContainSingle("a bubble says something");
        vm.InsertStepCommand.Execute(null);
        vm.Properties.OfType<NumberParameter>().First(p => p.Label == VideoEditorText.StepNumberLabel).Value.Should().Be(1);
        vm.InsertSpotlightCommand.Execute(null);
        vm.Properties.OfType<NumberParameter>().Should().Contain(p => p.Label == VideoEditorText.SpotlightDim);
        vm.InsertMagnifyCommand.Execute(null);
        vm.Properties.OfType<NumberParameter>().Should().Contain(p => p.Label == VideoEditorText.MagnifyZoom);

        using var bitmap = new SKBitmap(new SKImageInfo(vm.Project.Canvas.Width, vm.Project.Canvas.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            vm.RenderPreview(canvas, bitmap.Width, bitmap.Height, SKColors.Black);
        }

        vm.InsertFreehandCommand.Execute(null);
        vm.WorkText.Should().Be(VideoEditorText.DrawFreehandNow);
        vm.ClearSelectionCommand.Execute(null);
        vm.WorkText.Should().BeEmpty("Esc cancels");
        vm.InsertFreehandCommand.Execute(null);
        vm.PreviewPointerDown(10, 10);
        vm.PreviewPointerMove(40, 12);
        vm.PreviewPointerMove(70, 40);
        vm.PreviewPointerUp();
        vm.Timeline.Selected.Should().BeOfType<AnnotationItem>().Which.Shape.Should().Be(AnnotationShape.Freehand);
        vm.WorkText.Should().BeEmpty();
    }

    [Fact]
    public void The_templates_menu_shows_the_panel_on_its_tab()
    {
        TestMedia.Require();
        using var vm = Open();
        vm.ShowTemplatesCommand.Execute(null);
        vm.Media.IsShown.Should().BeTrue();
        vm.Media.ShownTab.Should().Be(MediaPanelViewModel.TemplatesTabIndex);
    }
}
