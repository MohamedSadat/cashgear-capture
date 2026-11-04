using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.VideoEditor;
using CashGear.Capture.Core.Transcripts;
using CashGear.Capture.Licensing;
using CashGear.Capture.Media.Tests;
using CashGear.Capture.Transcription;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Playback;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.App.Tests.VideoEditor;

/// <summary>The transcript panel: its words through the timeline's edits, its keys, what it says to screen readers.</summary>
public sealed class TranscriptEditorTests : VideoEditorTestBase
{
    private sealed class NothingFree : IFeatureGate
    {
        public bool IsEnabled(ProFeature feature) => feature is not (ProFeature.TranscriptEditing or ProFeature.Translation);
    }

    /// <summary>A made-up recording (its file isn't there), a word a second, heard from the microphone.</summary>
    private VideoEditorViewModel Spoken(double seconds = 20, string language = "en", VideoEditorServices? services = null)
    {
        TestMedia.Require();
        var asset = new MediaAsset("rec", Path.Combine(Root, "Rec.mp4"), seconds,
        [
            new(0, StreamKind.Video, null, 0, seconds, 1280, 720, 30),
            new(1, StreamKind.Audio, "Microphone", 0, seconds),
        ]);
        var words = Enumerable.Range(0, (int)seconds).Select(i => new SpokenWord(i, i + 0.5, language == "ar" ? $"كلمة{i}" : $"w{i}")).ToList();
        var project = ProjectFactory.AddRecording(VideoProject.Empty(new ProjectCanvas(1280, 720, 30)), asset with { Transcript = new MediaTranscript(1, language, words) }, 0);
        return new VideoEditorViewModel(project, null, services ?? Services, Dialogs);
    }

    private static int At(TranscriptPanelViewModel panel, string word) => panel.View.Words.Select((w, i) => (w, i)).First(p => p.w.Word.Text == word).i;

    [Fact]
    public void A_cut_on_the_timeline_shows_in_the_transcript_at_once_and_the_reverse_and_undo_restores_both()
    {
        using var vm = Spoken();
        var panel = vm.Transcript;
        panel.IsShown.Should().BeTrue("a project with a transcript opens with the panel");

        // On the timeline: the words go.
        vm.Timeline.SetRange((2.75, 4.75));
        vm.DeleteCommand.Execute(null);
        panel.View.Words.Where(w => w.IsCut).Select(w => w.Word.Text).Should().Equal("w3", "w4");
        vm.UndoCommand.Execute(null);
        panel.View.Words.Should().OnlyContain(w => !w.IsCut);

        // In the transcript: the clips are cut, and the marked time closes up.
        panel.Select(At(panel, "w7"));
        panel.Select(At(panel, "w8"), extend: true);
        panel.CanCut.Should().BeTrue();
        panel.CutSelected();
        vm.UndoText.Should().Be("Undo cut words");
        vm.Project.Duration.Should().BeApproximately(18, 1e-3);
        vm.Project.Tracks.Single(t => t.HasPictures).Clips.Should().HaveCount(2, "on the timeline too");
        panel.View.Words.Where(w => w.IsCut).Select(w => w.Word.Text).Should().Equal("w7", "w8");
        panel.View.Words[panel.Caret].Word.Text.Should().Be("w9", "the next word");
        vm.Position.Should().BeApproximately(6.75, 1e-3, "the playhead is at the cut");
        vm.UndoCommand.Execute(null);
        vm.Project.Duration.Should().Be(20);
        panel.View.Words.Should().OnlyContain(w => !w.IsCut);
        vm.RedoCommand.Execute(null);
        vm.Project.Duration.Should().BeApproximately(18, 1e-3);
    }

    [Fact]
    public void Cut_words_can_be_hidden_and_restored()
    {
        using var vm = Spoken();
        var panel = vm.Transcript;
        panel.Select(At(panel, "w3"));
        panel.CutSelected();
        panel.Shows(At(panel, "w3")).Should().BeTrue("shown struck through by default");
        panel.ShowCut = false;
        panel.Shows(At(panel, "w3")).Should().BeFalse();
        panel.Select(At(panel, "w2"));
        panel.View.Words[panel.Step(1)].Word.Text.Should().Be("w4", "the arrows skip a hidden word");
        panel.ShowCut = true;
        panel.Select(At(panel, "w3"));
        panel.CanCut.Should().BeFalse();
        panel.CanRestore.Should().BeTrue();
        panel.RestoreSelected();
        vm.Project.Duration.Should().BeApproximately(20, 1e-3);
        vm.UndoText.Should().Be("Undo restore words");
        panel.View.Words.Should().OnlyContain(w => !w.IsCut);
    }

    [Fact]
    public void Typing_corrects_the_text_and_captions_and_leaves_the_media()
    {
        using var vm = Spoken();
        var (withCaption, _) = CaptionEdits.AddTrack(vm.Project, [new CaptionItem { Id = "c", Start = 2.9, Length = 1, Text = "w3", Words = [new(0.1, 0.6, " w3")] }]);
        vm.History.Apply("captions", withCaption);
        var pictures = vm.Project.Tracks.Single(t => t.HasPictures);
        var panel = vm.Transcript;
        panel.Correct(At(panel, "w3"), "three");
        vm.UndoText.Should().Be("Undo correct a word");
        panel.View.Words[3].Word.Should().Match<SpokenWord>(w => w.Text == "three" && w.Heard == "w3");
        panel.NameOf(3).Should().Contain("three").And.Contain("w3");
        ((CaptionItem)vm.Project.Find("c")!.Value.Item).Text.Should().Be("three");
        vm.Project.Tracks.Single(t => t.HasPictures).Should().BeSameAs(pictures, "the media isn't touched");
        vm.UndoCommand.Execute(null);
        panel.View.Words[3].Word.Text.Should().Be("w3");
        ((CaptionItem)vm.Project.Find("c")!.Value.Item).Text.Should().Be("w3");
    }

    [Fact]
    public void The_word_being_said_follows_the_playhead_and_a_word_seeks()
    {
        using var vm = Spoken();
        var panel = vm.Transcript;
        vm.Seek(5.2);
        panel.Current.Should().Be(At(panel, "w5"));
        panel.SeekTo(At(panel, "w9"));
        vm.Position.Should().BeApproximately(9, 1e-6);
        panel.Current.Should().Be(At(panel, "w9"));
    }

    [Fact]
    public void Reading_and_seeking_are_free_and_cutting_is_part_of_pro()
    {
        using var vm = Spoken(services: Services with { Features = new NothingFree() });
        var panel = vm.Transcript;
        panel.SeekTo(3);
        vm.Position.Should().BeApproximately(3, 1e-6);
        panel.Select(3);
        panel.CutSelected();
        Dialogs.Errors.Should().ContainSingle();
        vm.Project.Duration.Should().Be(20);
        panel.Correct(3, "x");
        vm.Project.Asset("rec")!.Transcript!.Words[3].Text.Should().Be("w3");
        panel.ShowSilencesCommand.Execute(null);
        panel.Review.Should().Be(TranscriptReview.None);
    }

    [Fact]
    public async Task Silences_are_reviewed_kept_and_shortened_in_one_step()
    {
        var levels = new SoundLevelStore(Path.Combine(Root, "levels"), NullLogger<SoundLevelStore>.Instance);
        using var vm = Spoken(services: Services with { Levels = levels });
        var panel = vm.Transcript;

        // Without its file there are no levels: the gaps between words are all there is to go by (0.5 s here, under the limit).
        panel.SilenceLonger = 0.45;
        await panel.ShowSilencesAsync();
        panel.Silences.Should().HaveCount(20, "nineteen gaps and the end");
        panel.Silences[0].Text.Should().Contain("0.5 s");
        panel.HasNoSilences.Should().BeFalse();
        panel.SilenceShortenTo = 0.3;
        var duration = vm.Project.Duration;

        panel.Silences[0].KeepCommand.Execute(null);
        panel.Silences.Should().HaveCount(19, "kept: it leaves the list");

        panel.Silences[0].ActCommand.Execute(null);
        vm.Project.Duration.Should().BeApproximately(duration - 0.2, 1e-3);
        vm.UndoCommand.Execute(null);

        panel.ShortenAllCommand.Execute(null);
        vm.UndoText.Should().Be("Undo shorten silences");
        vm.Project.Duration.Should().BeApproximately(duration - (19 * 0.2), 1e-3);
        panel.Silences.Should().OnlyContain(r => r.Silence.Length <= 0.31 || r.Silence.Start < 1, "what is left of them isn't over the limit");
        vm.UndoCommand.Execute(null);
        vm.Project.Duration.Should().Be(duration);
        panel.CloseReviewCommand.Execute(null);
        panel.Review.Should().Be(TranscriptReview.None);
    }

    [Fact]
    public async Task Filler_words_are_reviewed_and_removed()
    {
        using var vm = Spoken();
        var transcript = vm.Project.Asset("rec")!.Transcript!;
        var list = transcript.Words.ToList();
        list[2] = list[2] with { Text = "um," };
        list[5] = list[5] with { Text = "يعني" };
        vm.History.Apply("words", TranscriptEdits.WithTranscript(vm.Project, "rec", transcript with { Words = list }));
        var panel = vm.Transcript;
        await panel.ShowFillersAsync();
        panel.Fillers.Should().ContainSingle().Which.Text.Should().StartWith("um,");
        panel.CountYani = true;
        panel.Fillers.Should().HaveCount(2, "only when asked");
        panel.Fillers[1].KeepCommand.Execute(null);
        panel.Fillers.Should().ContainSingle();
        panel.RemoveAllFillersCommand.Execute(null);
        vm.UndoText.Should().Be("Undo remove filler words");
        vm.Project.Duration.Should().BeApproximately(19, 1e-3);
        panel.View.Words.Single(w => w.IsCut).Word.Text.Should().Be("um,");
        panel.Fillers.Should().BeEmpty();
        panel.HasNoFillers.Should().BeTrue();
    }

    [Fact]
    public void Captions_can_be_made_from_the_transcript_of_this_cut()
    {
        using var vm = Spoken();
        var panel = vm.Transcript;
        panel.Select(At(panel, "w3"));
        panel.CutSelected();
        panel.MakeCaptionsCommand.Execute(null);
        vm.UndoText.Should().Be("Undo captions from the transcript");
        var track = vm.Project.Tracks[^1];
        track.Name.Should().Be("Captions (English)");
        string.Join(' ', track.Items.Cast<CaptionItem>().Select(c => c.Text)).Should().Contain("w2 w4");
    }

    [Fact]
    public async Task A_projects_caption_words_are_offered_as_the_transcript()
    {
        TestMedia.Require();
        var asset = new MediaAsset("rec", Path.Combine(Root, "Rec.mp4"), 10, [new(1, StreamKind.Audio, "Microphone", 0, 10)]);
        var project = ProjectFactory.AddRecording(VideoProject.Empty(new ProjectCanvas(1280, 720, 30)), asset, 0);
        var (withCaptions, id) = CaptionEdits.AddTrack(project, [new CaptionItem { Id = "c", Start = 1, Length = 2, Text = "Hello there", Words = [new(0, 0.5, " Hello"), new(0.6, 1.5, " there")] }], "Captions (English)");
        using var vm = new VideoEditorViewModel(withCaptions, null, Services, Dialogs);
        var panel = vm.Transcript;
        panel.IsShown.Should().BeTrue("there is something to offer");
        panel.HasTranscript.Should().BeFalse();
        panel.ShowsEmpty.Should().BeTrue();
        var offer = panel.Offers.Should().ContainSingle().Which;
        offer.Text.Should().Contain("Captions (English)");
        await panel.UseCaptionWordsAsync(offer);
        vm.UndoText.Should().Be("Undo captions as the transcript");
        panel.View.Words.Select(w => w.Word.Text).Should().Equal("Hello", "there");
        vm.Project.Asset("rec")!.Transcript!.Language.Should().Be("en");
        panel.Offers.Should().BeEmpty();
        vm.UndoCommand.Execute(null);
        panel.Offers.Should().ContainSingle("it is offered again");
    }

    [Fact]
    public void A_sentence_note_shows_when_a_transcript_is_cut_by_the_sentence()
    {
        using var vm = Spoken();
        vm.Transcript.ShowsSentenceNote.Should().BeFalse();
        var transcript = vm.Project.Asset("rec")!.Transcript!;
        vm.History.Apply("words", TranscriptEdits.WithTranscript(vm.Project, "rec", transcript with { Words = [new SpokenWord(0, 3, "جملة كاملة", IsSentence: true), new SpokenWord(4, 6, "جملة أخرى", IsSentence: true)] }));
        vm.Transcript.ShowsSentenceNote.Should().BeTrue();
        vm.Transcript.Select(0);
        vm.Transcript.CutSelected();
        vm.Transcript.View.Words[0].IsCut.Should().BeTrue();
        vm.Project.Duration.Should().BeLessThan(20);
    }

    [Fact]
    public async Task A_transcript_is_made_from_the_media_on_its_own_clock_in_one_step()
    {
        TestMedia.Require();
        var folder = Path.Combine(Root, "models");
        Directory.CreateDirectory(folder);
        using (var model = File.Create(Path.Combine(folder, WhisperModels.Small.File)))
        {
            model.SetLength(WhisperModels.Small.Bytes);
        }

        var transcriber = new FakeTranscriber(new TranscriptSegment(0.5, 2, "Hello there", [new(0.5, 1, " Hello"), new(1.1, 2, " there")]));
        var store = new WhisperModelStore(Path.Combine(Root, "downloads"), new HttpClient(), NullLogger<WhisperModelStore>.Instance, alsoIn: [folder]);
        var levels = new SoundLevelStore(Path.Combine(Root, "levels"), NullLogger<SoundLevelStore>.Instance);
        using var vm = new VideoEditorViewModel(ProjectFactory.FromRecording(ProjectFactory.ReadAsset(Recording())), null, Services with { Transcriber = transcriber, Models = store, Levels = levels }, Dialogs);

        // A cut first: the transcript hears all of the file all the same.
        vm.History.Apply("cut", TimelineEdits.DeleteRange(vm.Project, 1, 2));
        var dialog = vm.OpenTranscript()!;
        dialog.MakesTranscript.Should().BeTrue();
        dialog.TitleText.Should().Be("Transcript from Speech");
        dialog.Sounds.Select(s => s.Name).Should().Equal("Microphone", "System audio");
        dialog.KeepFillers.Should().BeTrue();
        await dialog.StartCommand.ExecuteAsync(null);
        dialog.Error.Should().BeNull();
        transcriber.Calls.Single().Should().Match<(int Samples, TranscriptionOptions Options)>(c => c.Samples == 4 * 16_000 && c.Options.KeepFillers);
        vm.UndoText.Should().Be("Undo transcript");
        var transcript = vm.Project.Assets.Single().Transcript!;
        transcript.Stream.Should().Be(vm.Project.Tracks.First(t => t.Name == "Microphone").Clips.First().Stream);
        transcript.Words.Select(w => w.Text).Should().Equal("Hello", "there");
        levels.Known(vm.Project.Assets.Single(), transcript.Stream).Should().NotBeNull("what was measured is kept");
        vm.Transcript.IsShown.Should().BeTrue();
    }

    private static TranscriptControl Attached(TranscriptPanelViewModel panel, double width = 330, double height = 300)
    {
        var words = new TranscriptControl();
        words.Attach(panel, new ScrollBar());
        words.Measure(new Size(width, height));
        words.Arrange(new Rect(0, 0, width, height));
        words.UpdateLayout();
        return words;
    }

    private static void Press(UIElement element, Key key)
    {
        var source = new HwndlessSource();
        element.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = UIElement.KeyDownEvent });
    }

    /// <summary>A presentation source for a key that isn't really pressed.</summary>
    private sealed class HwndlessSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = new DrawingVisual();

        public override bool IsDisposed => false;

        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }

    [Fact]
    public Task The_keys_move_select_cut_and_restore() => StaDispatcher.RunAsync(() =>
    {
        using var vm = Spoken();
        var panel = vm.Transcript;
        var words = Attached(panel);
        panel.Select(0);
        Press(words, Key.Right);
        panel.Caret.Should().Be(1);
        Press(words, Key.Right);
        panel.Caret.Should().Be(2);
        panel.Selected.Should().Equal(2);
        Press(words, Key.Left);
        panel.Caret.Should().Be(1);
        Press(words, Key.Down);
        panel.Caret.Should().BeGreaterThan(1, "the line below");
        Press(words, Key.Up);
        panel.Caret.Should().Be(1);
        Press(words, Key.Enter);
        vm.Position.Should().BeApproximately(1, 1e-6);
        Press(words, Key.Delete);
        panel.View.Words[1].IsCut.Should().BeTrue();
        panel.Caret.Should().Be(2);
        Press(words, Key.Escape);
        panel.Caret.Should().Be(-1);
        return Task.CompletedTask;
    });

    [Fact]
    public Task In_a_right_to_left_transcript_left_is_the_next_word_and_words_start_at_the_right() => StaDispatcher.RunAsync(() =>
    {
        using var vm = Spoken(language: "ar");
        var panel = vm.Transcript;
        var words = Attached(panel);
        panel.Select(0);
        Press(words, Key.Left);
        panel.Caret.Should().Be(1);
        Press(words, Key.Right);
        panel.Caret.Should().Be(0);
        words.BoxOf(0).Left.Should().BeGreaterThan(words.BoxOf(1).Left, "the first word is to the right of the second");
        words.BoxOf(0).Right.Should().BeApproximately(320, 1, "from the right edge, less the inset");
        return Task.CompletedTask;
    });

    [Fact]
    public Task Screen_readers_see_paragraphs_of_words_they_can_select_and_invoke() => StaDispatcher.RunAsync(() =>
    {
        using var vm = Spoken();
        var panel = vm.Transcript;
        var words = Attached(panel);
        var peer = (TranscriptAutomationPeer)UIElementAutomationPeer.CreatePeerForElement(words);
        peer.GetAutomationControlType().Should().Be(AutomationControlType.List);
        peer.GetName().Should().Be("The transcript's words");
        peer.GetHelpText().Should().Contain("Delete cuts");
        var paragraphs = peer.GetChildren();
        paragraphs.Should().NotBeEmpty();
        paragraphs[0].GetName().Should().Contain("w0 w1 w2");
        var word = (TranscriptWordPeer)paragraphs[0].GetChildren()[3];
        word.GetName().Should().StartWith("w3");
        word.GetAutomationControlType().Should().Be(AutomationControlType.ListItem);
        ((ISelectionItemProvider)word.GetPattern(PatternInterface.SelectionItem)!).Select();
        panel.Selected.Should().Equal(3);
        ((ISelectionItemProvider)word).IsSelected.Should().BeTrue();
        ((IInvokeProvider)word.GetPattern(PatternInterface.Invoke)!).Invoke();
        vm.Position.Should().BeApproximately(3, 1e-6);

        // A cut shows in the names at once.
        panel.CutSelected();
        peer.GetChildren()[0].GetChildren()[3].GetName().Should().Be("w3, cut");
        return Task.CompletedTask;
    });

    [Fact]
    public Task The_panel_opens_in_every_state_with_named_controls_and_a_correction_says_so() => StaDispatcher.RunAsync(() =>
    {
        using var vm = Spoken();
        var view = new TranscriptPanel();
        view.Attach(vm.Transcript);
        view.Measure(new Size(330, 500));
        view.Arrange(new Rect(0, 0, 330, 500));
        view.UpdateLayout();
        vm.Transcript.Select(3);

        // Correcting: the box is on the word, and a note says the video stays.
        view.EditNoteLine.Visibility.Should().Be(Visibility.Collapsed);
        view.StartEditing(3, "t");
        view.EditNoteLine.Visibility.Should().Be(Visibility.Visible);
        view.Editor.Text.Should().Be("t");
        view.Editor.Text = "three";
        view.FinishEditing(keep: false);
        vm.Project.Asset("rec")!.Transcript!.Words[3].Text.Should().Be("w3", "Esc changes nothing");
        view.StartEditing(3, "three");
        view.FinishEditing(keep: true);
        vm.Project.Asset("rec")!.Transcript!.Words[3].Text.Should().Be("three");
        return Task.CompletedTask;
    });

    [Fact]
    public Task The_window_leaves_typed_keys_to_the_words() => StaDispatcher.RunAsync(() =>
    {
        VideoEditorWindow.IsTextInput(new TranscriptControl()).Should().BeTrue();
        VideoEditorWindow.IsTextInput(new Button()).Should().BeFalse();
        return Task.CompletedTask;
    });

    /// <summary>The words drawn to a picture.</summary>
    private static BitmapSource Picture(TranscriptControl words, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var back = new DrawingVisual();
        using (var context = back.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
        }

        bitmap.Render(back);
        bitmap.Render(words);
        bitmap.Freeze();
        return bitmap;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public Task Words_are_drawn_with_cut_current_and_selected_ones_marked_also_in_high_contrast(bool arabic, bool highContrast) => StaDispatcher.RunAsync(() =>
    {
        HighContrast.Override = highContrast;
        try
        {
            using var vm = Spoken(language: arabic ? "ar" : "en");
            var panel = vm.Transcript;
            var words = Attached(panel, 330, 200);
            System.Windows.Documents.TextElement.SetForeground(words, Brushes.Black);
            panel.Select(1);
            panel.CutSelected();
            vm.Seek(5.1);
            panel.Select(7);
            panel.Select(8, extend: true);
            words.InvalidateVisual();
            words.UpdateLayout();
            var picture = Picture(words, 330, 200);
            if (Environment.GetEnvironmentVariable("CASHGEAR_KEEP_TEST_FILES") is { Length: > 0 } folder && Directory.Exists(folder))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(picture));
                using var file = File.Create(Path.Combine(folder, $"transcript-{(arabic ? "ar" : "en")}{(highContrast ? "-hc" : "")}.png"));
                encoder.Save(file);
            }

            // Something is drawn at the selected word's place, in the selection's colour.
            var box = words.BoxOf(7);
            var pixels = new byte[4];
            picture.CopyPixels(new Int32Rect((int)box.X + 1, (int)box.Y + 2, 1, 1), pixels, 4, 0);
            var selection = highContrast ? SystemColors.HighlightColor : Color.FromRgb(0x00, 0x78, 0xD4);
            (pixels[2], pixels[1], pixels[0]).Should().Be((selection.R, selection.G, selection.B));
        }
        finally
        {
            HighContrast.Override = null;
        }

        return Task.CompletedTask;
    });
}
