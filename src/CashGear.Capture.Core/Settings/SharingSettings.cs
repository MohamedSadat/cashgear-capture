using System.ComponentModel;

namespace CashGear.Capture.Core.Settings;

/// <summary>Who can see a video on YouTube.</summary>
public enum VideoPrivacy
{
    /// <summary>Only the channel's owner: nothing is shown to anyone until they say so.</summary>
    Private,

    /// <summary>Anyone with the link.</summary>
    Unlisted,

    Public,
}

/// <summary>What a YouTube upload says when it is sent by a workflow (the export window can change it for one upload).</summary>
/// <param name="Privacy">Who can see it. Private unless the user says otherwise.</param>
/// <param name="Description">The description.</param>
/// <param name="Tags">Its tags.</param>
/// <param name="Playlist">The playlist it is added to (its id; a playlist's address is taken too); null for none.</param>
/// <param name="MadeForKids">What YouTube asks of every upload: whether it is made for children.</param>
public sealed record YouTubeSettings(
    VideoPrivacy Privacy = VideoPrivacy.Private,
    string Description = "",
    IReadOnlyList<string>? Tags = null,
    string? Playlist = null,
    bool MadeForKids = false)
{
    public IReadOnlyList<string>? Tags { get; init; } = Tags ?? [];

    public YouTubeSettings Normalize() => this with
    {
        Privacy = Enum.IsDefined(Privacy) ? Privacy : VideoPrivacy.Private,
        Description = Description ?? string.Empty,
        Tags = [.. (Tags ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)],
        Playlist = string.IsNullOrWhiteSpace(Playlist) ? null : Playlist.Trim(),
    };

    /// <summary>Records compare their lists by reference; this compares them by content.</summary>
    public bool Equals(YouTubeSettings? other) =>
        other is not null
        && Privacy == other.Privacy
        && Description == other.Description
        && DestinationSettings.Same(Tags, other.Tags)
        && Playlist == other.Playlist
        && MadeForKids == other.MadeForKids;

    public override int GetHashCode() => HashCode.Combine(Privacy, Description, (Tags ?? []).Count, Playlist, MadeForKids);
}

/// <summary>Where uploads go in a cloud drive (Google Drive, OneDrive, Dropbox).</summary>
/// <param name="Folder">The folder, made if missing; a slash goes down a level; empty for the drive's top.</param>
/// <param name="AnyoneWithLink">Whether the link opens for anyone who has it. Off by default: the link then works only for the account's owner.</param>
public sealed record CloudFolderSettings(string? Folder = null, bool AnyoneWithLink = false)
{
    [Localizable(false)] // a folder's name on the service, the same in every language
    public static string DefaultFolder => "CashGear Capture";

    public string? Folder { get; init; } = Folder ?? DefaultFolder;

    public CloudFolderSettings Normalize() => this with
    {
        Folder = string.Join('/', (Folder ?? string.Empty).Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
    };
}

/// <summary>
/// The app's registrations with the services, when settings.json overrides the ones built in by the packaging script.
/// None is a secret in the OAuth sense (an installed app can't keep one), but they are not the user's to lose either.
/// </summary>
/// <param name="Google">The OAuth client id for YouTube and Google Drive.</param>
/// <param name="GoogleSecret">The "client secret" Google gives a desktop app along with its id, which its token endpoint asks for.</param>
/// <param name="Microsoft">The Entra application id for OneDrive.</param>
/// <param name="Dropbox">The Dropbox app key.</param>
public sealed record SharingClients(string? Google = null, string? GoogleSecret = null, string? Microsoft = null, string? Dropbox = null)
{
    /// <summary>A build without any registration.</summary>
    public static SharingClients None { get; } = new();

    public SharingClients Normalize() => this with
    {
        Google = Clean(Google),
        GoogleSecret = Clean(GoogleSecret),
        Microsoft = Clean(Microsoft),
        Dropbox = Clean(Dropbox),
    };

    /// <summary>These, with what isn't set taken from <paramref name="builtIn"/>.</summary>
    public SharingClients Over(SharingClients builtIn)
    {
        ArgumentNullException.ThrowIfNull(builtIn);
        return new SharingClients(
            Clean(Google) ?? builtIn.Google,
            Clean(Google) is null ? builtIn.GoogleSecret : Clean(GoogleSecret),
            Clean(Microsoft) ?? builtIn.Microsoft,
            Clean(Dropbox) ?? builtIn.Dropbox);
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}

/// <summary>The destinations with a sign-in: YouTube, Google Drive, OneDrive and Dropbox. Their tokens are kept with DPAPI, never here.</summary>
public sealed record SharingSettings(
    YouTubeSettings? YouTube = null,
    CloudFolderSettings? GoogleDrive = null,
    CloudFolderSettings? OneDrive = null,
    CloudFolderSettings? Dropbox = null,
    SharingClients? Clients = null)
{
    public YouTubeSettings? YouTube { get; init; } = YouTube ?? new YouTubeSettings();

    public CloudFolderSettings? GoogleDrive { get; init; } = GoogleDrive ?? new CloudFolderSettings();

    public CloudFolderSettings? OneDrive { get; init; } = OneDrive ?? new CloudFolderSettings();

    public CloudFolderSettings? Dropbox { get; init; } = Dropbox ?? new CloudFolderSettings();

    public SharingClients? Clients { get; init; } = Clients ?? new SharingClients();

    public SharingSettings Normalize() => this with
    {
        YouTube = (YouTube ?? new YouTubeSettings()).Normalize(),
        GoogleDrive = (GoogleDrive ?? new CloudFolderSettings()).Normalize(),
        OneDrive = (OneDrive ?? new CloudFolderSettings()).Normalize(),
        Dropbox = (Dropbox ?? new CloudFolderSettings()).Normalize(),
        Clients = (Clients ?? new SharingClients()).Normalize(),
    };
}
