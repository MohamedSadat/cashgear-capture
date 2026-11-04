using System.Text.Json.Serialization;
using CashGear.Capture.Core.Overlays;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Resources;

namespace CashGear.Capture.Core.Settings;

/// <summary>
/// A named set of recording settings, picked from the toolbar: what it records, its frame rate and quality, its sound,
/// its overlays, its webcam and the look the editor opens it with. What it doesn't name stays as Settings has it.
/// </summary>
/// <param name="Id">Stable, also for the command line.</param>
/// <param name="Name">What the user called it; null for a built-in one until renamed (its name follows the language).</param>
/// <param name="Area">What it records.</param>
/// <param name="Microphone">The microphone (null: none; <see cref="RecordingSettings.DefaultMicrophone"/>: Windows' default).</param>
/// <param name="Keystrokes">Which keys it shows.</param>
/// <param name="Webcam">Whether the webcam is in it.</param>
/// <param name="BurnIn">Overlays drawn into the video, or kept for later.</param>
/// <param name="Look">The look the editor opens it with (a look's id; null: the editor's default).</param>
public sealed record RecordingProfile(
    string Id,
    string? Name = null,
    RecordingAreaKind Area = RecordingAreaKind.Ask,
    int Fps = 30,
    QualityPreset Quality = QualityPreset.Balanced,
    bool IncludeCursor = true,
    bool SystemAudio = true,
    string? Microphone = null,
    AudioTrackLayout Tracks = AudioTrackLayout.Mixed,
    bool BurnIn = true,
    bool Clicks = true,
    KeystrokeMode Keystrokes = KeystrokeMode.Off,
    bool Webcam = false,
    WebcamLiveBackground WebcamBackground = WebcamLiveBackground.None,
    string? Look = null)
{
    /// <summary>The name to show: the user's, or a built-in one's in the language in use.</summary>
    [JsonIgnore]
    public string ShownName => Name is { Length: > 0 } name ? name : Id switch
    {
        RecordingProfiles.TutorialId => CoreStrings.ProfileTutorial,
        RecordingProfiles.MeetingId => CoreStrings.ProfileMeeting,
        RecordingProfiles.BugReportId => CoreStrings.ProfileBugReport,
        _ => Id,
    };

    public RecordingProfile Normalize() => this with
    {
        Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id.Trim(),
        Name = string.IsNullOrWhiteSpace(Name) ? null : Name.Trim(),
        Area = Enum.IsDefined(Area) ? Area : RecordingAreaKind.Ask,
        Fps = RecordingSettings.FrameRates.MinBy(f => Math.Abs(f - Fps)),
        Quality = Enum.IsDefined(Quality) ? Quality : QualityPreset.Balanced,
        Microphone = string.IsNullOrWhiteSpace(Microphone) ? null : Microphone.Trim(),
        Tracks = Enum.IsDefined(Tracks) ? Tracks : AudioTrackLayout.Mixed,
        Keystrokes = Enum.IsDefined(Keystrokes) ? Keystrokes : KeystrokeMode.Off,
        WebcamBackground = Enum.IsDefined(WebcamBackground) ? WebcamBackground : WebcamLiveBackground.None,
        Look = string.IsNullOrWhiteSpace(Look) ? null : Look.Trim(),
    };

    /// <summary>The settings a recording with this profile has.</summary>
    public AppSettings Apply(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings with
        {
            Recording = settings.Recording with
            {
                Fps = Fps,
                Quality = Quality,
                IncludeCursor = IncludeCursor,
                SystemAudio = SystemAudio,
                Microphone = Microphone,
                Tracks = Tracks,
            },
            Overlays = settings.Overlays with
            {
                BurnIn = BurnIn,
                Clicks = settings.Overlays.ClickSettings with { Enabled = Clicks },
                Keystrokes = settings.Overlays.KeystrokeSettings with { Mode = Keystrokes },
                Webcam = settings.Overlays.WebcamSettings with { Enabled = Webcam, LiveBackground = WebcamBackground },
            },
        };
    }

    /// <summary>A profile of the settings as they are now.</summary>
    public static RecordingProfile From(AppSettings settings, string name, RecordingAreaKind area = RecordingAreaKind.Ask)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var recording = settings.Recording;
        var overlays = settings.Overlays;
        return new RecordingProfile(
            Guid.NewGuid().ToString("N"),
            name,
            area,
            recording.Fps,
            recording.Quality,
            recording.IncludeCursor,
            recording.SystemAudio,
            recording.Microphone,
            recording.Tracks,
            overlays.BurnIn,
            overlays.ClickSettings.Enabled,
            overlays.KeystrokeSettings.Mode,
            overlays.WebcamSettings.Enabled,
            overlays.WebcamSettings.LiveBackground);
    }
}

/// <summary>The profiles a user has. They start as Tutorial, Meeting and Bug report, which can be changed or deleted like any other.</summary>
public sealed record RecordingProfiles(IReadOnlyList<RecordingProfile>? Profiles = null)
{
    public const string TutorialId = "tutorial";
    public const string MeetingId = "meeting";
    public const string BugReportId = "bug-report";

    /// <summary>The ones a new user finds.</summary>
    public static IReadOnlyList<RecordingProfile> BuiltIn { get; } =
    [
        new(TutorialId, Area: RecordingAreaKind.Ask, Fps: 30, Quality: QualityPreset.High, Microphone: RecordingSettings.DefaultMicrophone, Keystrokes: KeystrokeMode.Shortcuts, Look: "soft"),
        new(MeetingId, Area: RecordingAreaKind.Monitor, Fps: 15, Quality: QualityPreset.Balanced, Microphone: RecordingSettings.DefaultMicrophone, Tracks: AudioTrackLayout.Separate, Clicks: false, Look: "none"),
        new(BugReportId, Area: RecordingAreaKind.Window, Fps: 30, Quality: QualityPreset.Balanced, SystemAudio: false, Keystrokes: KeystrokeMode.AllKeys, BurnIn: false, Look: "none"),
    ];

    public IReadOnlyList<RecordingProfile>? Profiles { get; init; } = Profiles ?? BuiltIn;

    /// <summary>A profile by its id or its name, whichever the user said.</summary>
    public RecordingProfile? Find(string? idOrName) =>
        idOrName is null
            ? null
            : (Profiles ?? []).FirstOrDefault(p => string.Equals(p.Id, idOrName, StringComparison.OrdinalIgnoreCase))
                ?? (Profiles ?? []).FirstOrDefault(p => string.Equals(p.ShownName, idOrName, StringComparison.CurrentCultureIgnoreCase));

    public RecordingProfiles Normalize() => this with
    {
        Profiles = [.. (Profiles ?? BuiltIn).Select(p => p.Normalize()).DistinctBy(p => p.Id, StringComparer.OrdinalIgnoreCase)],
    };

    public bool Equals(RecordingProfiles? other) => other is not null && (Profiles ?? []).SequenceEqual(other.Profiles ?? []);

    public override int GetHashCode() => (Profiles ?? []).Count;
}
