using System.Collections.ObjectModel;
using CashGear.Capture.App.Resources;
using CashGear.Capture.Core.Overlays;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.VideoEditor.Editing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CashGear.Capture.App.Recording;

/// <summary>A look to open a profile's recordings with, or the editor's own default (<see cref="Id"/> null).</summary>
public sealed record LookChoice(string Label, string? Id)
{
    public override string ToString() => Label;
}

/// <summary>One profile being changed.</summary>
public sealed partial class ProfileRow(RecordingProfile profile) : ObservableObject
{
    private readonly string _id = profile.Id;
    private readonly string? _specificMicrophone = profile.Microphone is RecordingSettings.DefaultMicrophone ? null : profile.Microphone;

    [ObservableProperty]
    private string _name = profile.ShownName;

    [ObservableProperty]
    private RecordingAreaKind _area = profile.Area;

    [ObservableProperty]
    private int _fps = profile.Fps;

    [ObservableProperty]
    private QualityPreset _quality = profile.Quality;

    [ObservableProperty]
    private bool _includeCursor = profile.IncludeCursor;

    [ObservableProperty]
    private bool _systemAudio = profile.SystemAudio;

    [ObservableProperty]
    private bool _microphone = profile.Microphone is not null;

    [ObservableProperty]
    private AudioTrackLayout _tracks = profile.Tracks;

    [ObservableProperty]
    private bool _burnIn = profile.BurnIn;

    [ObservableProperty]
    private bool _clicks = profile.Clicks;

    [ObservableProperty]
    private KeystrokeMode _keystrokes = profile.Keystrokes;

    [ObservableProperty]
    private bool _webcam = profile.Webcam;

    [ObservableProperty]
    private WebcamLiveBackground _webcamBackground = profile.WebcamBackground;

    [ObservableProperty]
    private string? _look = profile.Look;

    /// <summary>The profile as it now is. A built-in one keeps following the language until it is renamed.</summary>
    public RecordingProfile ToProfile(RecordingProfile? was) => new(
        _id,
        was is not null && was.ShownName == Name ? was.Name : Name,
        Area,
        Fps,
        Quality,
        IncludeCursor,
        SystemAudio,
        Microphone ? _specificMicrophone ?? RecordingSettings.DefaultMicrophone : null,
        Tracks,
        BurnIn,
        Clicks,
        Keystrokes,
        Webcam,
        WebcamBackground,
        Look);

    public override string ToString() => Name;
}

/// <summary>The window of named recording profiles: added, duplicated, changed and deleted, kept when saved.</summary>
public sealed partial class RecordingProfilesViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IReadOnlyDictionary<string, RecordingProfile> _was;

    public RecordingProfilesViewModel(ISettingsService settings)
    {
        _settings = settings;
        var profiles = settings.Current.RecordingProfiles.Profiles ?? [];
        _was = profiles.ToDictionary(p => p.Id);
        Rows = [.. profiles.Select(p => new ProfileRow(p))];
        _selected = Rows.FirstOrDefault();
        Looks = [new LookChoice(RecordingText.LookAsEditor, null), .. CashGear.Capture.VideoEditor.Editing.Looks.BuiltIn.Select(l => new LookChoice(l.Name, l.Id))];
    }

    public ObservableCollection<ProfileRow> Rows { get; }

    public IReadOnlyList<RecordingAreaKind> Areas { get; } = Enum.GetValues<RecordingAreaKind>();

    public IReadOnlyList<int> FrameRates { get; } = RecordingSettings.FrameRates;

    public IReadOnlyList<QualityPreset> Qualities { get; } = Enum.GetValues<QualityPreset>();

    public IReadOnlyList<AudioTrackLayout> TrackLayouts { get; } = Enum.GetValues<AudioTrackLayout>();

    public IReadOnlyList<KeystrokeMode> KeyModes { get; } = Enum.GetValues<KeystrokeMode>();

    public IReadOnlyList<WebcamLiveBackground> Backgrounds { get; } = Enum.GetValues<WebcamLiveBackground>();

    public IReadOnlyList<LookChoice> Looks { get; }

    /// <summary>Called when saved, to close the window.</summary>
    public Action? Done { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelected))]
    [NotifyCanExecuteChangedFor(nameof(DuplicateCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    private ProfileRow? _selected;

    public bool HasSelected => Selected is not null;

    /// <summary>A new profile of the settings as they are now.</summary>
    [RelayCommand]
    private void Add()
    {
        var row = new ProfileRow(RecordingProfile.From(_settings.Current, RecordingText.NewProfileName));
        Rows.Add(row);
        Selected = row;
    }

    [RelayCommand(CanExecute = nameof(HasSelected))]
    private void Duplicate()
    {
        var copy = Selected!.ToProfile(null) with { Id = Guid.NewGuid().ToString("N"), Name = RecordingText.ProfileCopyName(Selected.Name) };
        var row = new ProfileRow(copy);
        Rows.Insert(Rows.IndexOf(Selected) + 1, row);
        Selected = row;
    }

    [RelayCommand(CanExecute = nameof(HasSelected))]
    private void Delete()
    {
        var at = Rows.IndexOf(Selected!);
        Rows.RemoveAt(at);
        Selected = Rows.Count == 0 ? null : Rows[Math.Min(at, Rows.Count - 1)];
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var profiles = Rows.Select(r =>
        {
            var made = r.ToProfile(null);
            return r.ToProfile(_was.GetValueOrDefault(made.Id));
        }).ToList();
        await _settings.UpdateAsync(s => s with { RecordingProfiles = new RecordingProfiles(profiles) });
        Done?.Invoke();
    }
}
