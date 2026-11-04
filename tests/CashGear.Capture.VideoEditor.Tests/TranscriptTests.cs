using System.IO;
using CashGear.Capture.Core.Transcripts;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Playback;

namespace CashGear.Capture.VideoEditor.Tests;

/// <summary>The transcript belongs to the media, and is shown through the clips. No media read here.</summary>
public sealed class TranscriptTests
{
    /// <summary>A word a second, each half a second long and named by its number, heard from the microphone (stream 1).</summary>
    private static VideoProject Spoken(double seconds = 60)
    {
        var project = TestProjects.Project(seconds);
        var words = Enumerable.Range(0, (int)seconds).Select(i => new SpokenWord(i, i + 0.5, $"w{i}")).ToList();
        return TranscriptEdits.WithTranscript(project, "rec", new MediaTranscript(1, "en", words));
    }

    private static Track Mic(VideoProject project) => project.Tracks.First(t => t.Name == "Microphone");

    private static Track Screen(VideoProject project) => project.Tracks.First(t => t.Kind == TrackKind.Video);

    private static int PositionOf(TranscriptView view, string text) => view.Words.Select((w, i) => (w, i)).First(p => p.w.Word.Text == text).i;

    [Fact]
    public void Words_are_shown_at_their_clips_times_through_cuts_speed_and_freezes()
    {
        var project = Spoken(20);
        var view = TranscriptView.Build(project);
        view.Words.Should().HaveCount(20).And.OnlyContain(w => !w.IsCut);
        view.Words[3].Start.Should().Be(3);

        // A cut: what is after it moves back, what was in it is cut.
        project = TimelineEdits.DeleteRange(project, 2.75, 5.75);
        view = TranscriptView.Build(project);
        view.Words.Single(w => w.Word.Text == "w6").Start.Should().BeApproximately(3, 1e-6);
        view.Words.Where(w => w.IsCut).Select(w => w.Word.Text).Should().Equal("w3", "w4", "w5");
        view.Words.First(w => w.IsCut).Start.Should().BeApproximately(2.75, 1e-6, "where they were");
        view.Words.Select(w => w.Word.Text).Take(8).Should().Equal("w0", "w1", "w2", "w3", "w4", "w5", "w6", "w7");

        // A speed change: twice as fast from that clip on.
        var second = Mic(project).Clips.Last();
        project = TransitionEdits.SetSpeed(project, second.Id, 2);
        view = TranscriptView.Build(project);
        var w8 = view.Words.Single(w => w.Word.Text == "w8");
        w8.Start.Should().BeApproximately(2.75 + ((8 - 5.75) / 2), 1e-5);
        (w8.End - w8.Start).Should().BeApproximately(0.25, 1e-5, "said in half the time");

        // A freeze: the sound pauses for it.
        var before = TranscriptView.Build(project).Words.Single(w => w.Word.Text == "w12").Start;
        project = TransitionEdits.Freeze(project, 3.5);
        TranscriptView.Build(project).Words.Single(w => w.Word.Text == "w12").Start.Should().BeApproximately(before + TransitionEdits.DefaultFreeze, 1e-5);
    }

    [Fact]
    public void The_current_word_is_found_by_the_time()
    {
        var view = TranscriptView.Build(Spoken(10));
        view.Words[view.IndexAt(3.2)].Word.Text.Should().Be("w3");
        view.Words[view.IndexAt(3.6)].Word.Text.Should().Be("w3", "just after it");
        view.IndexAt(3.9).Should().Be(-1, "well after it, before the next");
        view.IndexAt(-1).Should().Be(-1);
    }

    [Fact]
    public void Paragraphs_split_at_pauses_and_stay_put_when_a_pause_is_cut()
    {
        var words = new List<SpokenWord> { new(0, 0.3, "a"), new(0.4, 0.7, "b"), new(2, 2.3, "c"), new(2.4, 2.7, "d") };
        var project = TranscriptEdits.WithTranscript(TestProjects.Project(5), "rec", new MediaTranscript(1, "en", words));
        TranscriptView.Build(project).Words.Select(w => w.Paragraph).Should().Equal(0, 0, 1, 1);
        project = TranscriptEdits.Cut(project, [new CutRange(0.9, 1.8)]);
        TranscriptView.Build(project).Words.Select(w => w.Paragraph).Should().Equal(0, 0, 1, 1);
    }

    [Fact]
    public void Deleting_words_cuts_in_the_middle_of_the_silence_on_every_unlocked_track()
    {
        var project = Spoken(10);
        var view = TranscriptView.Build(project);
        var ranges = TranscriptEdits.RangesOf(project, view, [PositionOf(view, "w3")]);
        ranges.Should().Equal(new CutRange(2.75, 3.75));

        var cut = TranscriptEdits.CutWords(project, [PositionOf(view, "w3")]);
        cut.Duration.Should().BeApproximately(9, 1e-6);
        Screen(cut).Spans().Should().Equal((0, 2.75, 0, 2.75), (2.75, 9, 3.75, 10));

        // On the sound the two parts overlap by the join, centred on the cut, reading on into what was cut.
        var half = TranscriptEdits.JoinLength / 2;
        var mic = Mic(cut).Spans();
        mic.Should().HaveCount(2);
        mic[0].End.Should().BeApproximately(2.75 + half, 1e-6);
        mic[0].Out.Should().BeApproximately(2.75 + half, 1e-6);
        mic[1].Start.Should().BeApproximately(2.75 - half, 1e-6);
        mic[1].In.Should().BeApproximately(3.75 - half, 1e-6);
        mic[1].End.Should().BeApproximately(9, 1e-6);
        TransitionEdits.CrossfadeLength(cut, Mic(cut).Clips.First().Id).Should().Be(0, "a join isn't a crossfade to take away");

        var after = TranscriptView.Build(cut);
        after.Words.Single(w => w.Word.Text == "w3").IsCut.Should().BeTrue();
        after.Words.Single(w => w.Word.Text == "w4").Start.Should().BeApproximately(3, 1e-5);
    }

    [Fact]
    public void A_locked_track_keeps_its_clips_and_several_words_are_one_cut()
    {
        var project = Spoken(10);
        project = project.WithTrack(project.Tracks.First(t => t.Name == "System audio") with { Locked = true });
        var view = TranscriptView.Build(project);
        var cut = TranscriptEdits.CutWords(project, [PositionOf(view, "w3"), PositionOf(view, "w4"), PositionOf(view, "w7")]);
        cut.Tracks.First(t => t.Name == "System audio").Clips.Should().ContainSingle();
        Screen(cut).Spans().Should().Equal((0, 2.75, 0, 2.75), (2.75, 4.75, 4.75, 6.75), (4.75, 7, 7.75, 10));
        var words = TranscriptView.Build(cut).Words;
        words.Where(w => w.IsCut).Select(w => w.Word.Text).Should().Equal("w3", "w4", "w7");
    }

    [Fact]
    public void Cutting_next_to_an_earlier_cut_goes_back_to_it()
    {
        var project = Spoken(10);
        var view = TranscriptView.Build(project);
        project = TranscriptEdits.CutWords(project, [PositionOf(view, "w3")]);
        view = TranscriptView.Build(project);
        project = TranscriptEdits.CutWords(project, [PositionOf(view, "w4")]);
        Screen(project).Spans().Should().Equal((0, 2.75, 0, 2.75), (2.75, 8, 4.75, 10)); // no crumb is left between the cuts
        project.Duration.Should().BeApproximately(8, 1e-6);
        Mic(project).Clips.Should().HaveCount(2, "nor a sliver of sound from the first join");
        Mic(project).Spans()[1].In.Should().BeApproximately(4.75 - (TranscriptEdits.JoinLength / 2), 1e-6);

        // And the same on the other side of a cut.
        var other = TranscriptEdits.CutWords(Spoken(10), [PositionOf(TranscriptView.Build(Spoken(10)), "w4")]);
        other = TranscriptEdits.CutWords(other, [PositionOf(TranscriptView.Build(other), "w3")]);
        Mic(other).Clips.Should().HaveCount(2);
        other.Duration.Should().BeApproximately(8, 1e-6);
    }

    [Fact]
    public void Restoring_a_cut_word_brings_the_timeline_back()
    {
        var original = Spoken(10);
        var view = TranscriptView.Build(original);
        var cut = TranscriptEdits.CutWords(original, [PositionOf(view, "w3")]);
        view = TranscriptView.Build(cut);
        var back = TranscriptEdits.Restore(cut, [PositionOf(view, "w3")]);
        back.Duration.Should().BeApproximately(10, 1e-6);
        foreach (var track in original.Tracks)
        {
            back.Track(track.Id)!.Spans().Should().Equal(track.Spans(), "the clips are one again on {0}", track.Name);
        }

        TranscriptView.Build(back).Words.Should().OnlyContain(w => !w.IsCut);
        TranscriptView.Build(back).Words.Single(w => w.Word.Text == "w5").Start.Should().BeApproximately(5, 1e-5);
    }

    [Fact]
    public void Restoring_one_of_several_cut_words_puts_a_part_between_the_clips()
    {
        var project = Spoken(10);
        var view = TranscriptView.Build(project);
        project = TranscriptEdits.CutWords(project, [PositionOf(view, "w3"), PositionOf(view, "w4"), PositionOf(view, "w5")]);
        view = TranscriptView.Build(project);
        var back = TranscriptEdits.Restore(project, [PositionOf(view, "w4")]);
        var after = TranscriptView.Build(back);
        after.Words.Where(w => w.IsCut).Select(w => w.Word.Text).Should().Equal("w3", "w5");
        after.Words.Select(w => w.Word.Text).Take(7).Should().Equal("w0", "w1", "w2", "w3", "w4", "w5", "w6");
        var w4 = after.Words.Single(w => w.Word.Text == "w4");
        var w6 = after.Words.Single(w => w.Word.Text == "w6");
        w4.Start.Should().BeApproximately(3, 1e-3, "right after what was before it (2.75 + 0.25 of silence)");
        w6.Start.Should().BeGreaterThan(w4.End);
        Screen(back).Clips.Should().HaveCount(3);
        back.Duration.Should().BeApproximately(8, 1e-3);

        // The part is linked with its recording, and undo isn't needed to cut it again.
        var again = TranscriptEdits.CutWords(back, [after.Words.Select((w, i) => (w, i)).First(p => p.w.Word.Text == "w4").i]);
        TranscriptView.Build(again).Words.Where(w => w.IsCut).Select(w => w.Word.Text).Should().Equal("w3", "w4", "w5");
    }

    [Fact]
    public void Undo_covers_cuts_corrections_and_restoring()
    {
        var history = new ProjectHistory(Spoken(10));
        var start = history.Project;
        history.Apply("cut", TranscriptEdits.CutWords(history.Project, [3]));
        history.Apply("correct", TranscriptEdits.Correct(history.Project, "rec", 5, "five"));
        history.Undo();
        history.Project.Asset("rec")!.Transcript!.Words[5].Text.Should().Be("w5");
        history.Undo();
        history.Project.Should().BeSameAs(start);
        TranscriptView.Build(history.Project).Words.Should().OnlyContain(w => !w.IsCut);
        history.Redo();
        TranscriptView.Build(history.Project).Words.Single(w => w.IsCut).Word.Text.Should().Be("w3", "a cut on the timeline shows, and undoing shows it back");
    }

    [Fact]
    public void Correcting_a_word_changes_the_transcript_and_captions_and_never_the_media()
    {
        var project = Spoken(10);
        var (withCaptions, id) = CaptionEdits.AddTrack(project, [
            new CaptionItem { Id = "words", Start = 2.5, Length = 3, Text = "w3 w4", Words = [new(0.5, 1, " w3"), new(1.5, 2, " w4")] },
            new CaptionItem { Id = "plain", Start = 6, Length = 1, Text = "and w6 here" },
        ]);
        var corrected = TranscriptEdits.Correct(withCaptions, "rec", 3, "three");
        var word = corrected.Asset("rec")!.Transcript!.Words[3];
        word.Text.Should().Be("three");
        word.Heard.Should().Be("w3", "kept, to show it was corrected");
        ((CaptionItem)corrected.Find("words")!.Value.Item).Text.Should().Be("three w4");
        ((CaptionItem)corrected.Find("words")!.Value.Item).Words![0].Should().Be(new CaptionWord(0.5, 1, " three"));

        // A caption without words: the word in its text, when it is there once.
        corrected = TranscriptEdits.Correct(corrected, "rec", 6, "six");
        ((CaptionItem)corrected.Find("plain")!.Value.Item).Text.Should().Be("and six here");

        foreach (var track in withCaptions.Tracks.Where(t => t.Kind != TrackKind.Caption))
        {
            corrected.Track(track.Id).Should().Be(track, "the media doesn't change");
        }

        // Back to what was heard, it no longer counts as corrected; nothing typed changes nothing.
        var again = TranscriptEdits.Correct(corrected, "rec", 3, "w3");
        again.Asset("rec")!.Transcript!.Words[3].Heard.Should().BeNull();
        TranscriptEdits.Correct(corrected, "rec", 3, "  ").Should().BeSameAs(corrected);
    }

    [Fact]
    public void Captions_can_be_made_from_the_transcript_for_any_cut()
    {
        var project = Spoken(30);
        var styled = new Track("old", TrackKind.Caption, "Old", [], Captions: new CaptionStyle(Color: "#FF0000", Box: null));
        project = project with { Tracks = [.. project.Tracks, styled] };
        var view = TranscriptView.Build(project);
        project = TranscriptEdits.CutWords(project, [.. Enumerable.Range(5, 10).Select(i => PositionOf(view, $"w{i}"))]);
        var (made, id) = TranscriptEdits.CaptionsFromTranscript(project);
        var track = made.Track(id)!;
        track.Captions!.Color.Should().Be("#FF0000", "the look of the captions there are");
        var captions = track.Items.OfType<CaptionItem>().ToList();
        string.Join(' ', captions.Select(c => c.Text)).Should().Contain("w4 w15").And.NotContain("w5");
        captions.SelectMany(c => c.Words!.Select(w => (c.Start + w.Start, w.Text))).First(w => w.Text.Trim() == "w15").Item1.Should().BeApproximately(5, 0.3, "at the word's time after the cut");
        captions.Zip(captions.Skip(1)).Should().OnlyContain(p => p.First.End <= p.Second.Start + 1e-6);
    }

    [Fact]
    public void A_caption_tracks_words_become_the_transcript_on_the_streams_clock()
    {
        var project = TestProjects.Project(30);
        var (withCaptions, id) = CaptionEdits.AddTrack(project, [
            new CaptionItem { Id = "a", Start = 1, Length = 2, Text = "Hello world.", Words = [new(0, 0.5, " Hel"), new(0.5, 1, "lo"), new(1.1, 1.8, " world"), new(1.8, 1.8, ".")] },
            new CaptionItem { Id = "b", Start = 10, Length = 2, Text = "مرحبا بكم" },
        ]);

        // The timeline was cut and sped up after the captions were made; a word's time goes through the clip it is under.
        withCaptions = TimelineEdits.DeleteRange(withCaptions, 5, 7);
        var made = TranscriptBuilder.FromCaptions(withCaptions, id, "en");
        var transcript = made.Asset("rec")!.Transcript!;
        transcript.Stream.Should().Be(1, "the microphone's");
        transcript.Words.Select(w => w.Text).Should().Equal("Hello", "world.", "مرحبا بكم");
        transcript.Words[0].Should().Be(new SpokenWord(1, 2, "Hello"));
        transcript.Words[1].Should().Be(new SpokenWord(2.1, 2.8, "world."));
        transcript.Words[2].IsSentence.Should().BeTrue("a caption without words is cut as a sentence");
        (transcript.Words[2].Start, transcript.Words[2].End).Should().Be((10.0, 12.0), "the cut moved the caption with the sound");
        transcript.HasSentences.Should().BeTrue();
        TranscriptBuilder.CaptionTracksWithWords(made).Should().ContainSingle().Which.Id.Should().Be(id);
        TranscriptBuilder.CaptionTracksWithWords(TestProjects.Project()).Should().BeEmpty();
    }

    [Fact]
    public void Pieces_of_words_join_and_punctuation_takes_no_time()
    {
        var words = TranscriptBuilder.Merge([new TranscriptWord(0, 0.2, " Hel"), new TranscriptWord(0.2, 0.4, "lo"), new TranscriptWord(0.4, 0.9, ","), new TranscriptWord(0.9, 1.2, " world")]);
        words.Should().Equal(new SpokenWord(0, 0.4, "Hello,"), new SpokenWord(0.9, 1.2, "world"));
    }

    [Fact]
    public void A_segment_without_words_is_a_sentence()
    {
        var transcript = TranscriptBuilder.FromSegments(1, "ar", [
            new TranscriptSegment(2, 4, "مرحبا بكم"),
            new TranscriptSegment(0, 1, "Hi there", [new TranscriptWord(0, 0.4, " Hi"), new TranscriptWord(0.5, 1, " there")]),
        ]);
        transcript.Words.Select(w => (w.Text, w.IsSentence)).Should().Equal(("Hi", false), ("there", false), ("مرحبا بكم", true));
        transcript.IsRightToLeft.Should().BeTrue();
    }

    /// <summary>Levels from runs of (seconds, decibels), voiced where asked.</summary>
    internal static SoundLevels Levels(params (double Seconds, float Level, bool Voiced)[] runs)
    {
        var levels = new List<float>();
        var voiced = new List<bool>();
        foreach (var (seconds, level, pitched) in runs)
        {
            for (var i = 0; i < seconds * SoundLevels.PerSecond; i++)
            {
                levels.Add(level);
                voiced.Add(pitched);
            }
        }

        return new SoundLevels([.. levels], [.. voiced]);
    }

    [Fact]
    public void Word_times_move_to_the_silence_near_them()
    {
        // Sound from 0.5 to 1 s, a gap to 1.4 s, sound to 2 s. The recognizer ends the first word early, and starts the
        // second just where it ended.
        var levels = Levels((0.5, -80, false), (0.5, -20, true), (0.4, -80, false), (0.6, -20, true), (1, -80, false));
        var refined = TranscriptBuilder.Refine([new SpokenWord(0.2, 0.95, "one"), new SpokenWord(0.95, 1.98, "two")], levels);
        refined[0].End.Should().BeApproximately(1.0, 0.02);
        refined[1].Start.Should().BeApproximately(1.4, 0.02);
        refined[0].Start.Should().BeApproximately(0.5, 0.02, "out to the silence before the first word");
        refined[1].End.Should().BeApproximately(2.0, 0.02);
    }

    [Fact]
    public void Between_words_with_no_silence_the_quietest_moment_is_the_boundary()
    {
        var levels = Levels((0.5, -20, true), (0.03, -30, true), (0.5, -20, true));
        var refined = TranscriptBuilder.Refine([new SpokenWord(0, 0.47, "one"), new SpokenWord(0.47, 1.03, "two")], levels);
        refined[0].End.Should().BeApproximately(0.51, 0.02);
        refined[1].Start.Should().Be(refined[0].End);
    }

    [Fact]
    public void The_middle_of_the_silence_is_of_its_longest_quiet_stretch()
    {
        var levels = Levels((1, -20, true), (0.1, -80, false), (0.2, -40, false), (0.5, -80, false), (1, -20, true));
        levels.IsQuiet(levels.FrameAt(1.05)).Should().BeTrue();
        TranscriptEdits.Middle(levels, 1, 1.8).Should().BeApproximately(1.55, 0.02, "the breath between isn't the silence's middle");
        TranscriptEdits.Middle(null, 1, 1.8).Should().BeApproximately(1.4, 1e-6);
    }

    [Fact]
    public void Pauses_are_quiet_and_have_no_word_in_them()
    {
        var project = TranscriptEdits.WithTranscript(TestProjects.Project(5), "rec", new MediaTranscript(1, "en", [new SpokenWord(2, 2.5, "soft")]));
        var levels = Levels((1, -20, true), (3, -80, false), (1, -20, true));
        var found = SilenceFinder.Find(project, Mic(project), (_, _) => levels);
        found.Select(s => (Math.Round(s.Start, 2), Math.Round(s.End, 2))).Should().Equal((1, 2), (2.5, 4));
        SilenceFinder.Find(project, Mic(project), (_, _) => levels, longer: 1.2).Should().ContainSingle();

        // Without the sound's levels, the gaps between words.
        SilenceFinder.Find(project, Mic(project), null).Select(s => (Math.Round(s.Start, 2), Math.Round(s.End, 2))).Should().Equal((0, 2), (2.5, 5));
    }

    [Fact]
    public void Shortening_a_pause_leaves_some_of_it_each_side_and_a_pause_across_a_cut_counts_whole()
    {
        var project = TestProjects.Project(5);
        var levels = Levels((1, -20, true), (3, -80, false), (1, -20, true));
        var silence = SilenceFinder.Find(project, SilenceFinder.VoiceTrack(project)!, (_, _) => levels).Single();
        silence.CutFor(0.3).Should().Be(new CutRange(1.15, 3.85));
        var cut = TranscriptEdits.Cut(project, [silence.CutFor(0.3)]);
        cut.Duration.Should().BeApproximately(5 - 2.7, 1e-6);

        // Nearly two seconds of it left either side of a cut: found again as one.
        var partly = TranscriptEdits.Cut(project, [new CutRange(2, 2.5)]);
        SilenceFinder.Find(partly, SilenceFinder.VoiceTrack(partly)!, (_, _) => levels).Should().ContainSingle().Which.Length.Should().BeApproximately(2.5, 0.05);
    }

    [Fact]
    public void The_voice_track_is_the_transcripts_else_the_microphones()
    {
        var project = TestProjects.Project(5);
        SilenceFinder.VoiceTrack(project)!.Name.Should().Be("Microphone");
        var system = TranscriptEdits.WithTranscript(project, "rec", new MediaTranscript(2, "en", []));
        SilenceFinder.VoiceTrack(system)!.Name.Should().Be("System audio");
    }

    [Theory]
    [InlineData("um", true)]
    [InlineData("Um,", true)]
    [InlineData("UH.", true)]
    [InlineData("umm", true)]
    [InlineData("uhh...", true)]
    [InlineData("er", true)]
    [InlineData("erm", true)]
    [InlineData("ah", true)]
    [InlineData("Hmm,", true)]
    [InlineData("hm", true)]
    [InlineData("hello", false)]
    [InlineData("umbrella", false)]
    [InlineData("her", false)]
    [InlineData("a", false)]
    [InlineData("اممم", true)]
    [InlineData("امممم،", true)]
    [InlineData("إممم", true)]
    [InlineData("إمم", true)]
    [InlineData("آه", true)]
    [InlineData("اه", true)]
    [InlineData("أم", false)] // "or", "mother"
    [InlineData("أمم", false)] // "nations"
    [InlineData("ام", false)]
    [InlineData("امم", false)]
    [InlineData("يعني", false)]
    [InlineData("مرحبا", false)]
    public void Filler_words_are_recognized(string word, bool filler) => FillerFinder.IsFiller(word).Should().Be(filler);

    [Fact]
    public void Yani_is_a_filler_only_when_asked() => FillerFinder.IsFiller("يعني،", yani: true).Should().BeTrue();

    [Fact]
    public void Fillers_and_repeated_words_are_found_in_the_transcript()
    {
        var words = new List<SpokenWord>
        {
            new(0, 0.3, "So,"), new(0.4, 0.6, "um,"), new(0.7, 0.9, "the"), new(1, 1.2, "The"), new(1.3, 1.6, "cat"), new(3, 3.3, "cat"), new(3.4, 3.6, "ate."),
        };
        var project = TranscriptEdits.WithTranscript(TestProjects.Project(5), "rec", new MediaTranscript(1, "en", words));
        var view = TranscriptView.Build(project);
        var fillers = FillerFinder.Find(project, view);
        fillers.Select(f => (f.Kind, f.Text)).Should().Equal((FillerKind.Word, "um,"), (FillerKind.Repeat, "the"));

        // Removing them all closes up: the cut word's neighbours play on.
        var cut = TranscriptEdits.CutWords(project, fillers.Select(f => f.Position!.Value));
        TranscriptView.Build(cut).Words.Where(w => !w.IsCut).Select(w => w.Word.Text).Should().Equal("So,", "The", "cat", "cat", "ate.");
    }

    [Fact]
    public void Voiced_sound_that_has_no_word_is_flagged()
    {
        // A word, then an unwritten "um" with a pitch, then a click without one, then the next word.
        var levels = Levels(
            (1, -80, false), (0.5, -20, true), (0.3, -80, false), (0.3, -22, true), (0.4, -80, false), (0.2, -25, false), (0.3, -80, false), (0.5, -20, true), (1, -80, false));
        var words = new List<SpokenWord> { new(1, 1.5, "one"), new(1.6, 3.5, "two") };
        var project = TranscriptEdits.WithTranscript(TestProjects.Project(5), "rec", new MediaTranscript(1, "en", TranscriptBuilder.Refine(words, levels)));
        var transcript = project.Asset("rec")!.Transcript!;
        transcript.Words[1].Start.Should().BeApproximately(3, 0.05, "the sounds before it, parted by quiet, aren't that word");
        var view = TranscriptView.Build(project);
        var fillers = FillerFinder.Find(project, view, (_, _) => levels);
        var voiced = fillers.Should().ContainSingle().Which;
        voiced.Kind.Should().Be(FillerKind.Voiced);
        voiced.Start.Should().BeApproximately(1.77, 0.03);
        voiced.End.Should().BeApproximately(2.13, 0.03);

        var cut = TranscriptEdits.Cut(project, [new CutRange(voiced.Start, voiced.End)]);
        TranscriptView.Build(cut).Words.Should().OnlyContain(w => !w.IsCut, "taking the sound out leaves the words");
    }

    [Fact]
    public void An_export_burns_one_language_in_and_saves_a_file_for_each()
    {
        var project = TestProjects.Project(5);
        var (one, english) = CaptionEdits.AddTrack(project, [new CaptionItem { Id = "e", Start = 1, Length = 1, Text = "Hello" }], "Captions (English)");
        var (two, arabic) = CaptionEdits.AddTrack(one, [new CaptionItem { Id = "a", Start = 1, Length = 1, Text = "مرحبا" }], "Captions (Arabic)");
        var prepared = new Export.ExportRequest(two, Export.ExportPresets.Mp4H264Project, Core.Recording.QualityPreset.Balanced, "x.mp4", BurnTrack: arabic).Prepared();
        prepared.Track(arabic)!.Hidden.Should().BeFalse();
        prepared.Track(english)!.Hidden.Should().BeTrue();
        new Export.ExportRequest(two, Export.ExportPresets.Mp4H264Project, Core.Recording.QualityPreset.Balanced, "x.mp4").Prepared().Track(english)!.Hidden.Should().BeFalse("all those shown, without a choice");
        new Export.ExportRequest(two, Export.ExportPresets.Mp4H264Project, Core.Recording.QualityPreset.Balanced, "x.mp4", BurnCaptions: false, BurnTrack: arabic).Prepared().Track(arabic)!.Hidden.Should().BeTrue();

        Export.SubtitleFiles.Cues(two, arabic).Should().ContainSingle().Which.Text.Should().Be("مرحبا");
        Export.SubtitleFiles.Cues(two, "none").Should().BeEmpty();
        var taken = new HashSet<string>();
        Export.SubtitleFiles.SafeName("Captions (English)", taken).Should().Be("Captions (English)");
        Export.SubtitleFiles.SafeName("Captions (English)", taken).Should().Be("Captions (English) 2");
        Export.SubtitleFiles.SafeName("a/b:c", taken).Should().Be("abc");
        Export.SubtitleFiles.SafeName("???", taken).Should().Be("captions");
    }

    [Fact]
    public void A_project_saved_with_a_transcript_reads_back_and_keeps_no_box()
    {
        var project = TranscriptEdits.Correct(Spoken(3), "rec", 1, "one");
        project = project with { Tracks = [.. project.Tracks, new Track("cap", TrackKind.Caption, "Captions", [], Captions: new CaptionStyle(Box: null))] };
        var read = ProjectJson.Deserialize(ProjectJson.Serialize(project));
        read.Version.Should().Be(VideoProject.CurrentVersion);
        read.Asset("rec")!.Transcript!.Should().BeEquivalentTo(project.Asset("rec")!.Transcript!);
        read.Asset("rec")!.Transcript!.Words[1].Heard.Should().Be("w1");
        read.Track("cap")!.Captions!.Box.Should().BeNull("since version 3 no box stays no box");
    }

    [Fact]
    public void A_version_2_project_is_brought_up_to_this_version()
    {
        var json = """
            { "version": 2, "canvas": { "width": 640, "height": 360, "fps": 30, "background": { "kind": "Color", "color": "#102030" } },
              "assets": [ { "id": "a", "path": "C:\\x.mp4", "duration": 5, "streams": [] } ],
              "tracks": [ { "id": "c", "kind": "Caption", "name": "Captions", "items": [], "captions": { "color": "#FFFFFF" } } ] }
            """;
        var project = ProjectJson.Deserialize(System.Text.Encoding.UTF8.GetBytes(json));
        project.Version.Should().Be(VideoProject.CurrentVersion);
        project.Asset("a")!.Transcript.Should().BeNull();
        project.Track("c")!.Captions!.Box.Should().Be(CaptionStyle.DefaultBox, "what older versions read a style without one as");
    }

    /// <summary>How long the view takes with a long transcript and many cuts, measured when asked.</summary>
    [Fact(Explicit = true)]
    public void A_long_transcript_with_many_cuts_is_quick_to_see_through()
    {
        var seconds = 3600.0;
        var words = Enumerable.Range(0, 7200).Select(i => new SpokenWord(i * 0.5, (i * 0.5) + 0.3, $"w{i}")).ToList();
        var project = TranscriptEdits.WithTranscript(TestProjects.Project(seconds), "rec", new MediaTranscript(1, "en", words));
        project = TranscriptEdits.Cut(project, Enumerable.Range(0, 300).Select(i => new CutRange((i * 11.0) + 0.32, (i * 11.0) + 0.48)));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        const int runs = 20;
        TranscriptView? view = null;
        for (var i = 0; i < runs; i++)
        {
            view = TranscriptView.Build(project);
        }

        var ms = watch.Elapsed.TotalMilliseconds / runs;
        TestContext.Current.TestOutputHelper?.WriteLine($"{view!.Words.Count} words through {Mic(project).Clips.Count()} clips: {ms:0.0} ms a view");
        ms.Should().BeLessThan(16, "a view on every edit, and a slider's drag is many edits");
    }

    public static TheoryData<string, string> OldProjects => new()
    {
        { "v1.1.1", "Project" }, { "phase-20", "Project" }, { "phase-20", "Vertical" },
    };

    [Theory]
    [MemberData(nameof(OldProjects))]
    public void Old_projects_open_and_their_caption_words_become_the_transcript(string folder, string file)
    {
        var cache = Directory.CreateTempSubdirectory("cashgear-old-").FullName;
        try
        {
            var project = ProjectFile.Open(Path.Combine(AppContext.BaseDirectory, "Fixtures", folder, file + ".cgcap"), cache).Project;
            project.Version.Should().Be(VideoProject.CurrentVersion);
            project.Assets.Should().OnlyContain(a => a.Transcript == null, "nothing changes until the offer is taken up");
            foreach (var track in TranscriptBuilder.CaptionTracksWithWords(project))
            {
                var words = track.Items.OfType<CaptionItem>().Where(c => c.Words is { Count: > 0 }).SelectMany(c => c.Words!).Count();
                var made = TranscriptBuilder.FromCaptions(project, track.Id, "en");
                var transcript = made.Assets.Single(a => a.Transcript is not null).Transcript!;
                transcript.Words.Should().NotBeEmpty();
                transcript.Words.Where(w => !w.IsSentence).Should().HaveCountLessThanOrEqualTo(words);
                TranscriptView.Build(made).Words.Should().NotBeEmpty();

                // It saves and reads back as this version.
                var again = ProjectJson.Deserialize(ProjectJson.Serialize(made));
                again.Assets.Single(a => a.Transcript is not null).Transcript!.Words.Should().HaveCount(transcript.Words.Count);
            }

            if (folder == "phase-20" && file == "Project")
            {
                TranscriptBuilder.CaptionTracksWithWords(project).Should().ContainSingle("this fixture has words on its captions");
            }
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }
}
