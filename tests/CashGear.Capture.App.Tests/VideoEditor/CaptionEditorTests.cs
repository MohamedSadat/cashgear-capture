using System.IO;
using System.Net.Http;
using CashGear.Capture.App.Editor;
using CashGear.Capture.App.VideoEditor;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Transcripts;
using CashGear.Capture.Transcription;
using CashGear.Capture.VideoEditor.Export;
using CashGear.Capture.VideoEditor.Model;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.App.Tests.VideoEditor;

/// <summary>A transcriber that hears what it is told, and remembers what it was given.</summary>
internal sealed class FakeTranscriber(params TranscriptSegment[] heard) : ITranscriber
{
    public List<(int Samples, TranscriptionOptions Options)> Calls { get; } = [];

    public Task<TranscriptionResult> TranscribeAsync(float[] samples, TranscriptionOptions options, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Calls.Add((samples.Length, options));
        progress?.Report(1);
        return Task.FromResult(new TranscriptionResult(heard, options.Language ?? "en", options.UseGpu ? "Vulkan" : "Cpu"));
    }
}

public sealed class CaptionEditorTests : VideoEditorTestBase
{
    /// <summary>Services with speech recognition: the fake transcriber, and the Small model "downloaded" (a file of its size).</summary>
    private (VideoEditorServices Services, FakeTranscriber Transcriber) WithSpeech(params TranscriptSegment[] heard)
    {
        var folder = Path.Combine(Root, "models");
        Directory.CreateDirectory(folder);
        using (var model = File.Create(Path.Combine(folder, WhisperModels.Small.File)))
        {
            model.SetLength(WhisperModels.Small.Bytes);
        }

        var transcriber = new FakeTranscriber(heard);
        var store = new WhisperModelStore(Path.Combine(Root, "downloads"), new HttpClient(), NullLogger<WhisperModelStore>.Instance, alsoIn: [folder]);
        return (Services with { Transcriber = transcriber, Models = store }, transcriber);
    }

    private VideoEditorViewModel OpenWith(VideoEditorServices services) =>
        new(ProjectFactory.FromRecording(ProjectFactory.ReadAsset(Recording())), null, services, Dialogs);

    [Fact]
    public async Task Captions_from_speech_come_back_as_a_caption_track_in_one_step()
    {
        var (services, transcriber) = WithSpeech(new TranscriptSegment(0.5, 2.5, "Hello there", [new(0.5, 1.2, " Hello"), new(1.3, 2.5, " there")]));
        using var vm = OpenWith(services);
        var dialog = vm.OpenTranscribe()!;
        Dialogs.Transcribe.Should().BeSameAs(dialog);
        dialog.Sounds.Select(s => s.Name).Should().Equal("Microphone", "System audio", "All sound tracks");
        dialog.Model.Model.Should().Be(WhisperModels.Small, "the default");
        dialog.Models.Single(m => m.Model == WhisperModels.Small).Name.Should().EndWith("(downloaded)");
        dialog.Models.Single(m => m.Model == WhisperModels.Medium).Name.Should().EndWith("(539 MB to download)");
        dialog.Language = dialog.Languages.Single(l => l.Code == "ar");
        dialog.UseGpu = false;

        await dialog.StartCommand.ExecuteAsync(null);
        dialog.Error.Should().BeNull();
        transcriber.Calls.Single().Should().Match<(int Samples, TranscriptionOptions Options)>(c =>
            c.Samples == 4 * 16_000 && c.Options.Language == "ar" && !c.Options.UseGpu && c.Options.ModelPath.EndsWith(WhisperModels.Small.File, StringComparison.Ordinal));
        var track = vm.Project.Tracks[^1];
        (track.Kind, track.Name).Should().Be((TrackKind.Caption, "Captions (Arabic)"));
        track.Items.Cast<CaptionItem>().Single().Should().Match<CaptionItem>(c => c.Text == "Hello there" && c.Start == 0.5 && c.Words!.Count == 2);
        vm.UndoText.Should().Be("Undo captions from speech");
        State.Current.VideoEditor!.Should().Match<Core.Settings.VideoEditorState>(s => s.TranscriptionModel == "small" && s.TranscriptionLanguage == "ar" && !s.TranscriptionGpu);

        // Over a marked range: only its sound, and the captions land in it.
        vm.Timeline.SetRange((1, 3));
        var ranged = vm.OpenTranscribe()!;
        ranged.OnlyRange.Should().BeTrue();
        await ranged.StartCommand.ExecuteAsync(null);
        transcriber.Calls[^1].Samples.Should().Be(2 * 16_000);
        vm.Project.Tracks[^1].Items.Cast<CaptionItem>().Single().Start.Should().Be(1.5);

        // Nothing heard: said, and nothing added.
        var (silent, _) = WithSpeech();
        using var quiet = OpenWith(silent);
        var none = quiet.OpenTranscribe()!;
        await none.StartCommand.ExecuteAsync(null);
        none.Error.Should().Be("No speech was heard in that sound track.");
        quiet.Project.Tracks.Should().NotContain(t => t.Kind == TrackKind.Caption);
    }

    [Fact]
    public void A_caption_is_typed_split_merged_and_its_tracks_look_set_in_the_panel()
    {
        using var vm = Open();
        vm.Timeline.SetRange((0.5, 2.5));
        var id = vm.AddCaption();
        vm.PropertiesTitle.Should().Be("Caption");
        vm.Properties.Select(p => p.Label).Should().Contain(["Text", "Split it at the playhead", "Merge it with the next caption", "Font", "Size", "Box", "Position", "Lines up to"]);
        vm.Properties.OfType<TextParameter>().Single().Value = "aaaa bbbb cccc dddd";
        ((CaptionItem)vm.Timeline.Selected!).Text.Should().Be("aaaa bbbb cccc dddd");
        vm.UndoText.Should().Be("Undo caption text");

        vm.Properties.OfType<NumberParameter>().Single(p => p.Label == "Size").Value = 30;
        vm.Properties.OfType<ChoiceParameter>().Single(p => p.Label == "Position").Selected = vm.Properties.OfType<ChoiceParameter>().Single(p => p.Label == "Position").Choices[1];
        var style = vm.Project.Tracks.Single(t => t.Kind == TrackKind.Caption).Captions!;
        (style.FontSize, style.Position).Should().Be((30, CaptionPosition.Top));

        vm.Seek(1.5);
        vm.Properties.OfType<ActionParameter>().Single(p => p.Label == "Split it at the playhead").Command.Execute(null);
        var captions = vm.Project.Tracks.Single(t => t.Kind == TrackKind.Caption).Items.Cast<CaptionItem>().ToList();
        captions.Select(c => c.Text).Should().Equal("aaaa bbbb", "cccc dddd");
        vm.Timeline.Select(id);
        vm.Properties.OfType<ActionParameter>().Single(p => p.Label == "Merge it with the next caption").Command.Execute(null);
        vm.Project.Tracks.Single(t => t.Kind == TrackKind.Caption).Items.Cast<CaptionItem>().Single().Text.Should().Be("aaaa bbbb cccc dddd");
        vm.UndoText.Should().Be("Undo merge captions");
    }

    [Fact]
    public void Captions_are_imported_from_and_saved_to_subtitle_files()
    {
        using var vm = Open();
        var srt = Path.Combine(Root, "talk.srt");
        File.WriteAllText(srt, "1\n00:00:00,500 --> 00:00:01,500\nمرحبا\n\n2\n00:00:02,000 --> 00:00:03,000\nHello\n");
        Dialogs.Media = srt;
        vm.ImportCaptionsCommand.Execute(null);
        var track = vm.Project.Tracks[^1];
        (track.Kind, track.Name, track.Items.Count).Should().Be((TrackKind.Caption, "talk", 2));

        Dialogs.SavePath = Path.Combine(Root, "out.vtt");
        vm.SaveCaptionsCommand.Execute(null);
        File.ReadAllText(Dialogs.SavePath).Should().StartWith("WEBVTT").And.Contain("00:00:00.500 --> 00:00:01.500\nمرحبا");

        Dialogs.Media = Path.Combine(Root, "talk.txt");
        File.WriteAllText(Dialogs.Media, "not subtitles");
        vm.ImportCaptionsCommand.Execute(null);
        Dialogs.Errors.Single().Should().StartWith("talk.txt can't be read");
    }

    [Fact]
    public async Task The_export_takes_the_marked_range_and_saves_the_captions_beside_the_file()
    {
        using var vm = Open();
        vm.Timeline.SetRange((1, 3));
        vm.AddCaption();
        vm.Timeline.SetRange((1, 2));
        vm.ExportCommand.Execute(null);
        var export = Dialogs.Export!;
        (export.HasRange, export.OnlyRange, export.HasCaptions, export.BurnCaptions).Should().Be((true, true, true, true));
        export.SizeText.Should().EndWith("0:01 long");
        export.SaveSrt = true;
        export.Path = Path.Combine(Root, "range.mp4");
        await export.StartCommand.ExecuteAsync(null);
        export.Error.Should().BeNull();
        export.ProgressText.Should().Contain("with range.srt");
        SubtitleFiles.Load(Path.Combine(Root, "range.srt")).Single().Should().Be(new SubtitleCue(0, 1, "Caption"));
        State.Current.VideoEditor!.ExportSrt.Should().BeTrue();
    }
}
