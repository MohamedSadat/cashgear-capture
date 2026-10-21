using System.IO;
using System.Net.Http;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.VideoEditor;
using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Licensing;
using CashGear.Capture.Media.Tests;
using CashGear.Capture.Outputs;
using CashGear.Capture.VideoEditor.Export;
using CashGear.Capture.VideoEditor.Model;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.App.Tests.VideoEditor;

/// <summary>
/// The Pro features ask the licence service before they run. In this version every one is free; these tests close
/// the gate to check that each entry point asks, and that the free edition says why rather than failing.
/// </summary>
public sealed class ProFeatureTests : VideoEditorTestBase
{
    [Fact]
    public void Every_Pro_entry_point_of_the_video_editor_asks_first()
    {
        var closed = Services with { Features = FeatureGate.Closed };
        var vm = new VideoEditorViewModel(ProjectFactory.FromRecording(ProjectFactory.ReadAsset(Recording())), null, closed, Dialogs);
        var before = vm.Project;

        vm.InsertTextCommand.Execute(null);
        vm.InsertBlurCommand.Execute(null);
        vm.InsertZoomCommand.Execute(null);
        vm.MakeAutoZoomsCommand.Execute(null);
        vm.InsertClicksCommand.Execute(null);
        vm.InsertCaptionCommand.Execute(null);
        vm.TranscribeCommand.Execute(null);
        vm.ImportCaptionsCommand.Execute(null);
        vm.FreezeFrameCommand.Execute(null);
        vm.CrossfadeFrom(vm.Project.Tracks.SelectMany(t => t.Items).First().Id);

        vm.Project.Should().Be(before, "nothing was added or changed");
        Dialogs.Errors.Should().HaveCount(10).And.OnlyContain(e => e == "This is part of CashGear Capture Pro.");
    }

    [Fact]
    public void With_every_feature_free_the_same_commands_work()
    {
        var vm = Open();

        vm.InsertTextCommand.Execute(null);
        vm.FreezeFrameCommand.Execute(null);

        Dialogs.Errors.Should().BeEmpty();
        vm.Project.Tracks.SelectMany(t => t.Items).OfType<AnnotationItem>().Should().ContainSingle();
    }

    [Fact]
    public void GIFs_are_offered_only_where_they_may_be_made()
    {
        var project = ProjectFactory.FromRecording(ProjectFactory.ReadAsset(Recording()));
        ExportViewModel Export(IFeatureGate? gate) =>
            new(project, "clip", Root, Services.Exporter, Settings, State, Shell, NullLogger.Instance, features: gate);

        Export(null).Presets.Should().Contain(p => p.IsGif);
        Export(FeatureGate.Closed).Presets.Should().NotContain(p => p.IsGif).And.Contain(ExportPresets.Mp4H264Hd);
    }

    [Fact]
    public async Task The_recording_workflows_GIF_step_is_skipped_without_the_feature()
    {
        var gif = new GifDestination(Services.Exporter, TestMedia.Require(), State, FeatureGate.Closed);
        using var item = OutputItem.ForVideo(Recording(), DateTimeOffset.Now);

        var result = await gif.SendAsync(item, TestContext.Current.CancellationToken);

        result.Should().Be(OutputResult.Skip("This is part of CashGear Capture Pro."));
        File.Exists(Path.ChangeExtension(Recording(), ".gif")).Should().BeFalse();
    }

    [Fact]
    public void The_licence_service_lets_everything_through_in_this_version()
    {
        var licenses = new LicenseService(Paths, TimeProvider.System, NullLogger<LicenseService>.Instance);

        licenses.Policy.IsAllFree.Should().BeTrue();
        Enum.GetValues<ProFeature>().Should().OnlyContain(f => licenses.IsEnabled(f));
        licenses.Status.Edition.Should().Be(Edition.Free, "no licence, and none is needed");
        licenses.Summary.Should().Be("Every feature is included in this version.");
    }

    [Fact]
    public async Task Once_sold_a_licence_activated_here_unlocks_Pro()
    {
        var (privateKey, publicKey) = Signing.NewKeyPair();
        var license = new LicenseInfo("lic-9", LicenseInfo.ProductName, "Ada", Edition.Pro, null, 1, DateTimeOffset.UtcNow.AddDays(-1), null, ActivationUrl: null);
        var file = Signing.Sign(license.ToBytes(), privateKey).ToText();
        var licenses = new LicenseService(Paths, TimeProvider.System, NullLogger<LicenseService>.Instance, publicKey, FeaturePolicy.ProSplit, "pc", () => new HttpClient());

        licenses.IsEnabled(ProFeature.Captions).Should().BeFalse();
        licenses.Summary.Should().StartWith("The free edition");

        var activate = () => licenses.ActivateAsync("not a licence", TestContext.Current.CancellationToken);
        await activate.Should().ThrowAsync<InvalidDataException>();

        var status = await licenses.ActivateAsync(file, TestContext.Current.CancellationToken);
        status.State.Should().Be(LicenseState.NotActivated, "a licence without an endpoint can't be activated");
        File.Exists(licenses.File).Should().BeTrue();
    }
}
