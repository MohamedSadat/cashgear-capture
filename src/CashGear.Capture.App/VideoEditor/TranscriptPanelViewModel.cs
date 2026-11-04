using System.Collections.ObjectModel;
using System.IO;
using CashGear.Capture.App.Resources;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Licensing;
using CashGear.Capture.Media;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Playback;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CashGear.Capture.App.VideoEditor;

/// <summary>A caption track whose words can become the transcript.</summary>
public sealed record CaptionWordsOffer(string TrackId, string Text);

/// <summary>What the panel shows under the words.</summary>
public enum TranscriptReview
{
    None,
    Silences,
    Fillers,
}

/// <summary>A pause in the review list: play it, keep it or shorten it.</summary>
public sealed class SilenceRow(TranscriptPanelViewModel panel, Silence silence, string text)
{
    public Silence Silence { get; } = silence;

    public string Text { get; } = text;

    public string PlayName => VideoEditorText.PlayRowName(Text);

    public string ActName => VideoEditorText.ShortenRowName(Text);

    public string KeepName => VideoEditorText.KeepRowName(Text);

    public IRelayCommand PlayCommand { get; } = new RelayCommand(() => panel.PlaySpan(silence.Start, silence.End));

    public IRelayCommand ActCommand { get; } = new RelayCommand(() => panel.ShortenOne(silence));

    public IRelayCommand KeepCommand { get; } = new RelayCommand(() => panel.Keep(silence));
}

/// <summary>A possible filler in the review list: play it, keep it or remove it.</summary>
public sealed class FillerRow(TranscriptPanelViewModel panel, Filler filler, string text)
{
    public Filler Filler { get; } = filler;

    public string Text { get; } = text;

    public string PlayName => VideoEditorText.PlayRowName(Text);

    public string ActName => VideoEditorText.RemoveRowName(Text);

    public string KeepName => VideoEditorText.KeepRowName(Text);

    public IRelayCommand PlayCommand { get; } = new RelayCommand(() => panel.PlaySpan(filler.Start, filler.End));

    public IRelayCommand ActCommand { get; } = new RelayCommand(() => panel.RemoveOne(filler));

    public IRelayCommand KeepCommand { get; } = new RelayCommand(() => panel.Keep(filler));
}

/// <summary>
/// The transcript panel's state: the words as the timeline has them (<see cref="TranscriptView"/>, made again whenever the
/// project changes, so undo and timeline cuts show at once), the selection, the word being said, and the review lists of
/// silences and filler words. Reading and seeking are free; cutting, correcting and the review lists are
/// <see cref="ProFeature.TranscriptEditing"/>. Every change is one undo step of the editor's history.
/// </summary>
public sealed partial class TranscriptPanelViewModel : ObservableObject
{
    private readonly VideoEditorViewModel _owner;
    private readonly VideoEditorServices _services;
    private readonly HashSet<string> _kept = [];
    private readonly Dictionary<(string Path, int Stream), SoundLevels?> _levels = [];
    private VideoProject? _seen;
    private bool _measuring;

    internal TranscriptPanelViewModel(VideoEditorViewModel owner, VideoEditorServices services)
    {
        _owner = owner;
        _services = services;
        var state = services.State.Current.VideoEditor ?? new VideoEditorState();
        _showCut = state.ShowCutWords;
        View = TranscriptView.Build(owner.Project);
        Refresh();
        _isShown = HasTranscript || HasOffers;
    }

    /// <summary>The words changed, or which of them show.</summary>
    public event EventHandler? WordsChanged;

    /// <summary>The selection or the word being said changed.</summary>
    public event EventHandler? MarksChanged;

    public TranscriptView View { get; private set; }

    public bool HasTranscript => !View.IsEmpty;

    public ObservableCollection<CaptionWordsOffer> Offers { get; } = [];

    public bool HasOffers => Offers.Count > 0;

    /// <summary>Whether to say so: there is nothing to read, and a transcript can be made.</summary>
    public bool ShowsEmpty => !HasTranscript;

    public string MakeButtonText => HasTranscript ? VideoEditorText.MakeTranscriptAgainButton : VideoEditorText.MakeTranscriptButton;

    /// <summary>The panel says so when some of it can only be cut by the sentence.</summary>
    public bool ShowsSentenceNote => View.Words.Any(w => w.Word.IsSentence);

    [ObservableProperty]
    private bool _isShown;

    [ObservableProperty]
    private bool _showCut;

    /// <summary>The first word of the selection, by position in <see cref="View"/>; -1 with none.</summary>
    public int Anchor { get; private set; } = -1;

    /// <summary>Where the arrow keys move from: the end of the selection that moves; -1 with none.</summary>
    public int Caret { get; private set; } = -1;

    /// <summary>The word being said at the playhead, or -1.</summary>
    public int Current { get; private set; } = -1;

    /// <summary>The selected words, by position, cut ones only when they show.</summary>
    public IReadOnlyList<int> Selected
    {
        get
        {
            if (Anchor < 0 || Caret < 0)
            {
                return [];
            }

            var from = Math.Min(Anchor, Caret);
            var to = Math.Max(Anchor, Caret);
            return [.. Enumerable.Range(from, to - from + 1).Where(p => p < View.Words.Count && Shows(p))];
        }
    }

    /// <summary>What the arrow keys are on, for screen readers.</summary>
    [ObservableProperty]
    private string _caretText = "";

    public bool CanCut => Selected.Any(p => !View.Words[p].IsCut);

    public bool CanRestore => Selected.Any(p => View.Words[p].IsCut);

    public bool Shows(int position) => ShowCut || !View.Words[position].IsCut;

    /// <summary>Whether the transcript of this media runs right to left.</summary>
    public bool IsRightToLeft(string assetId) => _owner.Project.Asset(assetId)?.Transcript?.IsRightToLeft == true;

    /// <summary>A word's time, as the timeline writes it.</summary>
    public string TimeOf(int position) => _owner.FormatTime(View.Words[position].Start);

    /// <summary>The word's name for screen readers.</summary>
    public string NameOf(int position)
    {
        var word = View.Words[position];
        return word.IsCut ? VideoEditorText.WordCut(word.Word.Text)
            : word.Word.Heard is { } heard ? VideoEditorText.WordCorrected(word.Word.Text, heard, _owner.FormatTime(word.Start))
            : VideoEditorText.WordAt(word.Word.Text, _owner.FormatTime(word.Start));
    }

    /// <summary>The project changed, or the panel just opened: the words again, and the lists that come from them.</summary>
    internal void Refresh()
    {
        var project = _owner.Project;
        if (_seen is not null && ReferenceEquals(_seen.Tracks, project.Tracks) && ReferenceEquals(_seen.Assets, project.Assets))
        {
            _seen = project;
            return;
        }

        _seen = project;
        _levels.Clear();
        View = TranscriptView.Build(project);
        Offers.Clear();
        if (View.IsEmpty)
        {
            foreach (var track in TranscriptBuilder.CaptionTracksWithWords(project))
            {
                Offers.Add(new CaptionWordsOffer(track.Id, VideoEditorText.UseCaptionWords(TrackNames.Display(track.Name))));
            }
        }

        Anchor = Math.Min(Anchor, View.Words.Count - 1);
        Caret = Math.Min(Caret, View.Words.Count - 1);
        if (Anchor < 0 || Caret < 0)
        {
            Anchor = Caret = -1;
        }

        OnPropertyChanged(nameof(HasTranscript));
        OnPropertyChanged(nameof(HasOffers));
        OnPropertyChanged(nameof(ShowsEmpty));
        OnPropertyChanged(nameof(MakeButtonText));
        OnPropertyChanged(nameof(ShowsSentenceNote));
        UpdateCaret();
        if (Review != TranscriptReview.None)
        {
            RefreshReview();
        }

        WordsChanged?.Invoke(this, EventArgs.Empty);
        MarksChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The playhead moved: which word is being said.</summary>
    internal void PlayheadMoved(double time)
    {
        var now = View.IndexAt(time);
        if (now != Current)
        {
            Current = now;
            MarksChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    partial void OnShowCutChanged(bool value)
    {
        _ = _services.State.UpdateAsync(s => s with { VideoEditor = (s.VideoEditor ?? new VideoEditorState()) with { ShowCutWords = value } });
        if (Caret >= 0 && !Shows(Caret))
        {
            ClearSelection();
        }

        WordsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Selects the word at <paramref name="position"/>, or from where the selection started to it.</summary>
    public void Select(int position, bool extend = false)
    {
        if (position < 0 || position >= View.Words.Count)
        {
            return;
        }

        Caret = position;
        if (!extend || Anchor < 0)
        {
            Anchor = position;
        }

        UpdateCaret();
        MarksChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SelectAll()
    {
        var shown = Enumerable.Range(0, View.Words.Count).Where(Shows).ToList();
        if (shown.Count > 0)
        {
            Anchor = shown[0];
            Caret = shown[^1];
            UpdateCaret();
            MarksChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ClearSelection()
    {
        Anchor = Caret = -1;
        UpdateCaret();
        MarksChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The next word that shows, <paramref name="by"/> words on from the caret (or the first or last with no caret).</summary>
    public int Step(int by)
    {
        var at = Caret;
        if (at < 0)
        {
            at = by > 0 ? -1 : View.Words.Count;
        }

        var step = Math.Sign(by);
        var left = Math.Abs(by);
        var found = -1;
        for (var i = at + step; i >= 0 && i < View.Words.Count && left > 0; i += step)
        {
            if (Shows(i))
            {
                found = i;
                left--;
            }
        }

        return found;
    }

    /// <summary>A click or Enter on a word: the video goes there. Pausing stays as it is.</summary>
    public void SeekTo(int position)
    {
        if (position >= 0 && position < View.Words.Count)
        {
            _owner.Seek(View.Words[position].Start);
        }
    }

    /// <summary>Delete: the selected words out of every unlocked track, one undo step. The next word is selected.</summary>
    [RelayCommand]
    public void CutSelected()
    {
        var chosen = Selected.Where(p => !View.Words[p].IsCut).ToList();
        if (chosen.Count == 0 || !_owner.AllowedFeature(ProFeature.TranscriptEditing))
        {
            return;
        }

        _owner.StopPlayingNow();
        var ranges = TranscriptEdits.RangesOf(_owner.Project, View, chosen, LevelsOf);
        var after = View.ShownAfter(chosen[^1]);
        _owner.History.Apply(VideoEditorText.UndoCutWords, TranscriptEdits.Cut(_owner.Project, ranges));
        var next = after is null ? -1 : FindPosition(after.AssetId, after.Index);
        if (next >= 0)
        {
            Select(next);
        }
        else
        {
            ClearSelection();
        }

        if (ranges.Count > 0)
        {
            _owner.Seek(Math.Min(ranges[0].From, _owner.Project.Duration));
        }
    }

    /// <summary>Ctrl+R: the selected cut words back where they were, one undo step.</summary>
    [RelayCommand]
    public void RestoreSelected()
    {
        var chosen = Selected.Where(p => View.Words[p].IsCut).ToList();
        if (chosen.Count == 0 || !_owner.AllowedFeature(ProFeature.TranscriptEditing))
        {
            return;
        }

        _owner.StopPlayingNow();
        var word = View.Words[chosen[0]];
        _owner.History.Apply(VideoEditorText.UndoRestoreWords, TranscriptEdits.Restore(_owner.Project, chosen, LevelsOf));
        var at = FindPosition(word.AssetId, word.Index);
        if (at >= 0)
        {
            Select(at);
            SeekTo(at);
        }
    }

    /// <summary>A new spelling: the transcript and the captions change, never the media.</summary>
    public void Correct(int position, string text)
    {
        if (position < 0 || position >= View.Words.Count || !_owner.AllowedFeature(ProFeature.TranscriptEditing))
        {
            return;
        }

        var word = View.Words[position];
        _owner.History.Apply(VideoEditorText.UndoCorrectWord, TranscriptEdits.Correct(_owner.Project, word.AssetId, word.Index, text));
    }

    /// <summary>The window that makes a transcript from a sound track's recording.</summary>
    [RelayCommand]
    public void MakeTranscript() => _owner.OpenTranscript();

    /// <summary>A caption track's words as the transcript, one undo step; then the sound is measured to bring them nearer.</summary>
    [RelayCommand]
    public async Task UseCaptionWordsAsync(CaptionWordsOffer? offer)
    {
        if (offer is null)
        {
            return;
        }

        var track = _owner.Project.Track(offer.TrackId);
        var language = track is null ? "" : LanguageOf(track.Name);
        var made = TranscriptBuilder.FromCaptions(_owner.Project, offer.TrackId, language, LevelsOf);
        _owner.History.Apply(VideoEditorText.UndoUseCaptionWords, made);
        await MeasureAsync();
        if (!ReferenceEquals(_owner.Project, made))
        {
            return; // changed since: leave what was edited alone
        }

        foreach (var asset in made.Assets.Where(a => a.Transcript is not null))
        {
            if (LevelsOf(asset, asset.Transcript!.Stream) is { } levels)
            {
                _owner.History.Apply(VideoEditorText.UndoUseCaptionWords, TranscriptEdits.WithTranscript(_owner.Project, asset.Id, asset.Transcript with { Words = TranscriptBuilder.Refine(asset.Transcript.Words, levels) }), "use-caption-words");
            }
        }
    }

    /// <summary>A language from a caption track's name ("Captions (Arabic)"), or none.</summary>
    private static string LanguageOf(string trackName) =>
        trackName.Contains(VideoEditorText.LanguageArabic, StringComparison.OrdinalIgnoreCase) ? "ar"
        : trackName.Contains(VideoEditorText.LanguageEnglish, StringComparison.OrdinalIgnoreCase) ? "en"
        : "";

    /// <summary>Captions of the words as this cut plays them, on a new track.</summary>
    [RelayCommand]
    public void MakeCaptions()
    {
        if (!_owner.AllowedFeature(ProFeature.Captions) || !HasTranscript)
        {
            return;
        }

        _owner.StopPlayingNow();
        var (project, _) = TranscriptEdits.CaptionsFromTranscript(_owner.Project, LanguageNameOfTranscript());
        _owner.History.Apply(VideoEditorText.UndoCaptionsFromTranscript, project);
    }

    private string? LanguageNameOfTranscript() =>
        _owner.Project.Assets.FirstOrDefault(a => a.Transcript is not null)?.Transcript?.Language is { Length: > 0 } code
            ? VideoEditorText.CaptionsInLanguage(TranscribeViewModel.LanguageName(code))
            : null;

    // The review lists.

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsSilences))]
    [NotifyPropertyChangedFor(nameof(ShowsFillers))]
    private TranscriptReview _review;

    public bool ShowsSilences => Review == TranscriptReview.Silences;

    public bool ShowsFillers => Review == TranscriptReview.Fillers;

    public ObservableCollection<SilenceRow> Silences { get; } = [];

    public ObservableCollection<FillerRow> Fillers { get; } = [];

    /// <summary>Pauses at least this long are listed, in seconds.</summary>
    [ObservableProperty]
    private double _silenceLonger = SilenceFinder.DefaultLongest;

    /// <summary>What is left of a pause that is shortened, in seconds.</summary>
    [ObservableProperty]
    private double _silenceShortenTo = SilenceFinder.DefaultShortenTo;

    /// <summary>Whether "يعني" counts as a filler. It is also a real word, so only when asked.</summary>
    [ObservableProperty]
    private bool _countYani;

    partial void OnSilenceLongerChanged(double value) => RefreshReview();

    partial void OnCountYaniChanged(bool value) => RefreshReview();

    public bool HasNoSilences => ShowsSilences && Silences.Count == 0;

    public bool HasNoFillers => ShowsFillers && Fillers.Count == 0;

    [RelayCommand]
    public async Task ShowSilencesAsync()
    {
        if (!_owner.AllowedFeature(ProFeature.TranscriptEditing))
        {
            return;
        }

        Review = TranscriptReview.Silences;
        await MeasureAsync();
        RefreshReview();
    }

    [RelayCommand]
    public async Task ShowFillersAsync()
    {
        if (!_owner.AllowedFeature(ProFeature.TranscriptEditing))
        {
            return;
        }

        Review = TranscriptReview.Fillers;
        await MeasureAsync();
        RefreshReview();
    }

    [RelayCommand]
    public void CloseReview() => Review = TranscriptReview.None;

    [RelayCommand]
    public void ShortenAll()
    {
        var ranges = Silences.Select(r => r.Silence.CutFor(SilenceShortenTo)).Where(r => r.Length > 0).ToList();
        if (ranges.Count > 0 && _owner.AllowedFeature(ProFeature.TranscriptEditing))
        {
            _owner.StopPlayingNow();
            _owner.History.Apply(VideoEditorText.UndoShortenSilences, TranscriptEdits.Cut(_owner.Project, ranges));
        }
    }

    internal void ShortenOne(Silence silence)
    {
        if (silence.CutFor(SilenceShortenTo) is { Length: > 0 } range && _owner.AllowedFeature(ProFeature.TranscriptEditing))
        {
            _owner.StopPlayingNow();
            _owner.History.Apply(VideoEditorText.UndoShortenSilences, TranscriptEdits.Cut(_owner.Project, [range]));
        }
    }

    [RelayCommand]
    public void RemoveAllFillers() => RemoveFillers([.. Fillers.Select(f => f.Filler)]);

    internal void RemoveOne(Filler filler) => RemoveFillers([filler]);

    private void RemoveFillers(IReadOnlyList<Filler> fillers)
    {
        if (fillers.Count == 0 || !_owner.AllowedFeature(ProFeature.TranscriptEditing))
        {
            return;
        }

        _owner.StopPlayingNow();
        var ranges = TranscriptEdits.RangesOf(_owner.Project, View, fillers.Where(f => f.Position is not null).Select(f => f.Position!.Value), LevelsOf).ToList();
        ranges.AddRange(fillers.Where(f => f.Position is null).Select(f => new CutRange(f.Start, f.End)));
        _owner.History.Apply(VideoEditorText.UndoRemoveFillers, TranscriptEdits.Cut(_owner.Project, ranges));
    }

    /// <summary>A row kept: it leaves the list until the panel looks again.</summary>
    internal void Keep(Silence silence)
    {
        _kept.Add(silence.Key);
        RefreshReview();
    }

    internal void Keep(Filler filler)
    {
        _kept.Add(filler.Key);
        RefreshReview();
    }

    /// <summary>From just before to just after a row's time, then it stops.</summary>
    internal void PlaySpan(double from, double to) => _owner.PlayRange(Math.Max(0, from - 0.5), to + 0.5);

    /// <summary>The lists again from the project as it is.</summary>
    private void RefreshReview()
    {
        Silences.Clear();
        Fillers.Clear();
        var project = _owner.Project;
        if (Review == TranscriptReview.Silences && SilenceFinder.VoiceTrack(project) is { } track)
        {
            foreach (var silence in SilenceFinder.Find(project, track, LevelsOf, SilenceLonger).Where(s => !_kept.Contains(s.Key)))
            {
                Silences.Add(new SilenceRow(this, silence, VideoEditorText.SilenceRow(_owner.FormatTime(silence.Start), silence.Length)));
            }
        }
        else if (Review == TranscriptReview.Fillers)
        {
            foreach (var filler in FillerFinder.Find(project, View, LevelsOf, CountYani).Where(f => !_kept.Contains(f.Key)))
            {
                var at = _owner.FormatTime(filler.Start);
                Fillers.Add(new FillerRow(this, filler, filler.Kind switch
                {
                    FillerKind.Word => VideoEditorText.FillerWordRow(filler.Text, at),
                    FillerKind.Repeat => VideoEditorText.FillerRepeatRow(filler.Text, at),
                    _ => VideoEditorText.FillerSoundRow(at),
                }));
            }
        }

        OnPropertyChanged(nameof(HasNoSilences));
        OnPropertyChanged(nameof(HasNoFillers));
    }

    /// <summary>The sound's levels when they are known, without working anything out.</summary>
    internal SoundLevels? LevelsOf(MediaAsset asset, int stream)
    {
        if (_services.Levels is not { } store)
        {
            return null;
        }

        if (!_levels.TryGetValue((asset.Path, stream), out var levels) || levels is null)
        {
            levels = store.Known(asset, stream);
            _levels[(asset.Path, stream)] = levels;
        }

        return levels;
    }

    /// <summary>Works out the levels of the streams the panel needs: the voice track's clips and the transcripts'. With progress, cancelled with the window.</summary>
    internal async Task MeasureAsync()
    {
        if (_services.Levels is not { } store || _measuring)
        {
            return;
        }

        var project = _owner.Project;
        var wanted = new List<(MediaAsset Asset, int Stream)>();
        foreach (var asset in project.Assets.Where(a => a.Transcript is not null))
        {
            wanted.Add((asset, asset.Transcript!.Stream));
        }

        if (SilenceFinder.VoiceTrack(project) is { } track)
        {
            foreach (var clip in track.Clips.Where(c => project.Asset(c.AssetId) is not null))
            {
                wanted.Add((project.Asset(clip.AssetId)!, clip.Stream));
            }
        }

        _measuring = true;
        try
        {
            foreach (var (asset, stream) in wanted.DistinctBy(w => (w.Asset.Id, w.Stream)).Where(w => File.Exists(w.Asset.Path)))
            {
                if (store.Known(asset, stream) is null)
                {
                    _owner.WorkText = VideoEditorText.MeasuringSound(0);
                    await store.GetAsync(asset, stream, new Progress<double>(p => _owner.WorkText = VideoEditorText.MeasuringSound(p)), _owner.ClosingToken);
                }
            }

            _levels.Clear();
        }
        catch (OperationCanceledException)
        {
            // the window closed
        }
        catch (Exception e) when (e is FfmpegException or IOException or UnauthorizedAccessException)
        {
            _owner.ShowProblem(VideoEditorText.SoundNotMeasured(e.Message));
        }
        finally
        {
            _measuring = false;
            _owner.WorkText = "";
        }
    }

    private int FindPosition(string assetId, int index)
    {
        for (var i = 0; i < View.Words.Count; i++)
        {
            if (View.Words[i].AssetId == assetId && View.Words[i].Index == index)
            {
                return i;
            }
        }

        return -1;
    }

    private void UpdateCaret()
    {
        var count = Selected.Count;
        CaretText = Caret < 0 || Caret >= View.Words.Count ? "" : count > 1 ? VideoEditorText.WordsSelected(count) : NameOf(Caret);
        CutSelectedCommand.NotifyCanExecuteChanged();
        RestoreSelectedCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanCut));
        OnPropertyChanged(nameof(CanRestore));
    }
}
