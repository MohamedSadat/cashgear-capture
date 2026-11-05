using System.ComponentModel;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Outputs.Http;
using CashGear.Capture.Outputs.Resources;

namespace CashGear.Capture.Outputs.Sharing;

/// <summary>
/// Google's resumable upload, the same for YouTube and Drive: a POST with the metadata gives a session address in
/// <c>Location</c>, then PUTs with <c>Content-Range</c> in multiples of 256 KiB, each answered 308 with how much has
/// arrived, the last with the file's resource. A PUT with <c>bytes */length</c> asks where things stand.
/// </summary>
[Localizable(false)]
internal sealed class GoogleUploadProtocol(SignInDestination owner, string startUrl, string metadata, string mediaType, string host) : IUploadProtocol
{
    /// <summary>A multiple of 256 KiB, as the protocol asks.</summary>
    public int ChunkSize { get; set; } = 64 * 256 * 1024;

    public async Task<string> StartAsync(long length, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, startUrl) { Content = SharingCalls.Json(metadata) };
        request.Headers.TryAddWithoutValidation("X-Upload-Content-Length", length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("X-Upload-Content-Type", mediaType);
        using var response = await owner.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await SharingCalls.FailureAsync(response, host, ct).ConfigureAwait(false);
        }

        return response.Headers.Location?.ToString() ?? throw new UploadHiccupException("no Location");
    }

    public async Task<UploadAnswer> PositionAsync(string session, long length, long known, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, session) { Content = new ByteArrayContent([]) };
        request.Content.Headers.ContentRange = new ContentRangeHeaderValue(length);
        using var response = await owner.SendAsync(request, ct).ConfigureAwait(false);
        return await AnswerAsync(response, ct).ConfigureAwait(false);
    }

    public async Task<UploadAnswer> SendAsync(string session, Stream chunk, long offset, int count, long length, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, session) { Content = new StreamContent(chunk) };
        request.Content.Headers.ContentLength = count;
        request.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + count - 1, length);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        using var response = await owner.SendAsync(request, ct, HttpCompletionOption.ResponseContentRead).ConfigureAwait(false);
        return await AnswerAsync(response, ct).ConfigureAwait(false);
    }

    public async Task AbandonAsync(string session, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, session);
        using var response = await owner.SendAsync(request, ct).ConfigureAwait(false);
    }

    private async Task<UploadAnswer> AnswerAsync(HttpResponseMessage response, CancellationToken ct)
    {
        switch ((int)response.StatusCode)
        {
            case 200 or 201:
                return new UploadAnswer(0, await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            case 308:
                return new UploadAnswer(response.Headers.TryGetValues("Range", out var ranges) && ParseRange(ranges.FirstOrDefault()) is { } end ? end + 1 : 0);
            case 404 or 410:
                throw new UploadSessionLostException($"HTTP {(int)response.StatusCode}");
            default:
                throw await SharingCalls.FailureAsync(response, host, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The last byte of "bytes=0-1234".</summary>
    private static long? ParseRange(string? range)
    {
        var dash = range?.LastIndexOf('-') ?? -1;
        return dash >= 0 && long.TryParse(range![(dash + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var end) ? end : null;
    }
}

/// <summary>Sign-in details of YouTube and Drive: one registration, a sign-in each, as each asks for different things.</summary>
internal static class GoogleApps
{
    [Localizable(false)]
    public static OAuthApp For(SharingContext context, string key, string name, string clientId, string? secret, string scope) => new(
        key,
        name,
        context.Endpoints.GoogleAuthorize,
        context.Endpoints.GoogleToken,
        clientId,
        secret,
        [scope],
        new Dictionary<string, string> { ["access_type"] = "offline", ["prompt"] = "consent" });
}

/// <summary>
/// YouTube Data API v3: a video with its title, description, tags and privacy, then its playlist and thumbnail. The link
/// is <c>https://youtu.be/id</c>. A video goes up Private unless the user chose otherwise.
/// </summary>
public sealed partial class YouTubeDestination(SharingContext context, YouTubeSettings settings, string clientId, string? clientSecret)
    : SignInDestination(context, GoogleApps.For(context, DestinationIds.YouTube, OutputsStrings.YouTube, clientId, clientSecret, Scope))
{
    /// <summary>The scope that can also add to a playlist, which the narrower <c>youtube.upload</c> can't.</summary>
    private const string Scope = "https://www.googleapis.com/auth/youtube";

    public const int MaxTitle = 100;
    public const int MaxDescriptionBytes = 5000;
    public const int MaxTagsLength = 500;

    public override string Id => DestinationIds.YouTube;

    public override bool SupportsImages => false;

    /// <summary>The account's playlists, for the export window.</summary>
    public async Task<IReadOnlyList<(string Id, string Title)>> PlaylistsAsync(CancellationToken ct)
    {
        using var reply = await CallAsync(HttpMethod.Get, $"{Context.Endpoints.GoogleApi}/youtube/v3/playlists?part=snippet&mine=true&maxResults=50", null, ct).ConfigureAwait(false);
        return [.. reply.RootElement.GetProperty("items").EnumerateArray().Select(i => (i.GetProperty("id").GetString()!, i.GetProperty("snippet").GetProperty("title").GetString()!))];
    }

    /// <summary>What YouTube will take as a title: no angle brackets, 100 characters, not empty.</summary>
    public static string CleanTitle(string? title, string fallback)
    {
        var cleaned = (title ?? string.Empty).Replace('<', ' ').Replace('>', ' ').Trim();
        cleaned = cleaned.Length == 0 ? fallback : cleaned;
        return cleaned.Length <= MaxTitle ? cleaned : cleaned[..MaxTitle];
    }

    /// <summary>What YouTube will take as a description: no angle brackets, 5000 bytes.</summary>
    public static string CleanDescription(string? description)
    {
        var cleaned = (description ?? string.Empty).Replace('<', ' ').Replace('>', ' ');
        while (Encoding.UTF8.GetByteCount(cleaned) > MaxDescriptionBytes)
        {
            cleaned = cleaned[..^Math.Max(1, (Encoding.UTF8.GetByteCount(cleaned) - MaxDescriptionBytes) / 4)];
        }

        return cleaned;
    }

    /// <summary>Tags as far as they fit in the 500 characters YouTube allows, where one with a space counts its quotes.</summary>
    public static IReadOnlyList<string> CleanTags(IEnumerable<string>? tags)
    {
        var kept = new List<string>();
        var used = 0;
        foreach (var tag in (tags ?? []).Select(t => t.Replace('<', ' ').Replace('>', ' ').Trim()).Where(t => t.Length > 0))
        {
            var cost = tag.Length + (tag.Contains(' ', StringComparison.Ordinal) ? 2 : 0) + (kept.Count > 0 ? 1 : 0);
            if (used + cost > MaxTagsLength)
            {
                break;
            }

            kept.Add(tag);
            used += cost;
        }

        return kept;
    }

    /// <summary>A playlist's id out of what was typed: the id itself, or an address with <c>list=</c> in it.</summary>
    public static string? PlaylistId(string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed))
        {
            return null;
        }

        var match = ListParameter().Match(typed);
        return match.Success ? match.Groups[1].Value : typed.Trim();
    }

    internal override IUploadProtocol Protocol(UploadWork work)
    {
        var details = work.Details;
        var fallback = Path.GetFileNameWithoutExtension(work.Name);
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteStartObject("snippet");
            json.WriteString("title", CleanTitle(details?.Title, fallback));
            json.WriteString("description", CleanDescription(details?.Description ?? settings.Description));
            json.WriteStartArray("tags");
            foreach (var tag in CleanTags(details?.Tags ?? settings.Tags))
            {
                json.WriteStringValue(tag);
            }

            json.WriteEndArray();
            json.WriteEndObject();
            json.WriteStartObject("status");
            json.WriteString("privacyStatus", (details?.Privacy ?? settings.Privacy).ToString().ToLowerInvariant());
            json.WriteBoolean("selfDeclaredMadeForKids", settings.MadeForKids);
            json.WriteEndObject();
            json.WriteEndObject();
        }

        return new GoogleUploadProtocol(
            this,
            $"{Context.Endpoints.GoogleUpload}/youtube/v3/videos?uploadType=resumable&part=snippet,status",
            Encoding.UTF8.GetString(buffer.ToArray()),
            HttpUploadDestination.MediaType(work.Name),
            new Uri(Context.Endpoints.GoogleUpload).Host);
    }

    protected override async Task<string> FindAccountAsync(CancellationToken ct)
    {
        using var reply = await CallAsync(HttpMethod.Get, $"{Context.Endpoints.GoogleApi}/youtube/v3/channels?part=snippet&mine=true", null, ct).ConfigureAwait(false);
        return reply.RootElement.GetProperty("items")[0].GetProperty("snippet").GetProperty("title").GetString() ?? DisplayName;
    }

    protected override async Task<OutputResult> FinishAsync(string finished, UploadWork work, CancellationToken ct)
    {
        using var video = JsonDocument.Parse(finished);
        var id = video.RootElement.GetProperty("id").GetString()!;
        var notes = new List<string>();
        if (PlaylistId(work.Details?.Playlist ?? settings.Playlist) is { } playlist)
        {
            var problem = await TryAsync(() => AddToPlaylistAsync(id, playlist, ct)).ConfigureAwait(false);
            if (problem is not null)
            {
                notes.Add(OutputsStrings.PlaylistNotAdded(problem));
            }
        }

        if (work.Details?.ThumbnailPath is { } thumbnail)
        {
            var problem = await TryAsync(() => SendThumbnailAsync(id, thumbnail, ct)).ConfigureAwait(false);
            if (problem is not null)
            {
                notes.Add(OutputsStrings.ThumbnailNotSent(problem));
            }
        }

        var uploaded = OutputsStrings.UploadedTo(DisplayName, $"https://youtu.be/{id}");
        return new OutputResult(true, notes.Count == 0 ? uploaded : uploaded + "\n" + string.Join('\n', notes)) { Link = $"https://youtu.be/{id}" };
    }

    private async Task AddToPlaylistAsync(string video, string playlist, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new PlaylistItem(new PlaylistSnippet(playlist, new VideoResource("youtube#video", video))), SharingJsonContext.Default.PlaylistItem);
        using var reply = await CallAsync(HttpMethod.Post, $"{Context.Endpoints.GoogleApi}/youtube/v3/playlistItems?part=snippet", body, ct).ConfigureAwait(false);
    }

    private async Task SendThumbnailAsync(string video, string path, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(SharingHttp.CallLimit);
        var bytes = await File.ReadAllBytesAsync(path, limit.Token).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Context.Endpoints.GoogleUpload}/youtube/v3/thumbnails/set?videoId={Uri.EscapeDataString(video)}&uploadType=media") { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(HttpUploadDestination.MediaType(path));
        using var response = await SendAsync(request, limit.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await SharingCalls.FailureAsync(response, new Uri(Context.Endpoints.GoogleUpload).Host, ct).ConfigureAwait(false);
        }
    }

    [GeneratedRegex(@"[?&]list=([\w-]+)", RegexOptions.CultureInvariant)]
    private static partial Regex ListParameter();
}

/// <summary>Google Drive API v3 with the <c>drive.file</c> scope: this app sees only what it made. The link is the file's page.</summary>
public sealed class GoogleDriveDestination(SharingContext context, CloudFolderSettings settings, string clientId, string? clientSecret)
    : SignInDestination(context, GoogleApps.For(context, DestinationIds.GoogleDrive, OutputsStrings.GoogleDrive, clientId, clientSecret, Scope))
{
    private const string Scope = "https://www.googleapis.com/auth/drive.file";

    private const string FolderType = "application/vnd.google-apps.folder";

    public override string Id => DestinationIds.GoogleDrive;

    internal override IUploadProtocol Protocol(UploadWork work) => new DriveProtocol(this, work.Name);

    protected override async Task<string> FindAccountAsync(CancellationToken ct)
    {
        using var reply = await CallAsync(HttpMethod.Get, $"{Context.Endpoints.GoogleApi}/drive/v3/about?fields=user(emailAddress)", null, ct).ConfigureAwait(false);
        return reply.RootElement.GetProperty("user").GetProperty("emailAddress").GetString() ?? DisplayName;
    }

    protected override async Task<OutputResult> FinishAsync(string finished, UploadWork work, CancellationToken ct)
    {
        using var file = JsonDocument.Parse(finished);
        var id = file.RootElement.GetProperty("id").GetString()!;
        var link = file.RootElement.TryGetProperty("webViewLink", out var view) ? view.GetString() : null;
        link ??= $"https://drive.google.com/file/d/{id}/view";
        string? problem = null;
        if (settings.AnyoneWithLink)
        {
            problem = await TryAsync(async () =>
            {
                using var reply = await CallAsync(HttpMethod.Post, $"{Context.Endpoints.GoogleApi}/drive/v3/files/{Uri.EscapeDataString(id)}/permissions", """{"type":"anyone","role":"reader"}""", ct).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        var uploaded = OutputsStrings.UploadedTo(DisplayName, link);
        return new OutputResult(true, problem is null ? uploaded : uploaded + "\n" + OutputsStrings.LinkNotShared(problem)) { Link = link };
    }

    /// <summary>The folder's id (each level found, or made); null for the top.</summary>
    [Localizable(false)]
    private async Task<string?> EnsureFolderAsync(CancellationToken ct)
    {
        string? parent = null;
        foreach (var name in CloudNames.Parts(settings.Folder))
        {
            var escaped = name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);
            var query = $"name='{escaped}' and mimeType='{FolderType}' and trashed=false and '{parent ?? "root"}' in parents";
            using var found = await CallAsync(HttpMethod.Get, $"{Context.Endpoints.GoogleApi}/drive/v3/files?fields=files(id)&q={Uri.EscapeDataString(query)}", null, ct).ConfigureAwait(false);
            var files = found.RootElement.GetProperty("files");
            if (files.GetArrayLength() > 0)
            {
                parent = files[0].GetProperty("id").GetString();
                continue;
            }

            var body = JsonSerializer.Serialize(new DriveFile(name, FolderType, parent is null ? null : [parent]), SharingJsonContext.Default.DriveFile);
            using var made = await CallAsync(HttpMethod.Post, $"{Context.Endpoints.GoogleApi}/drive/v3/files?fields=id", body, ct).ConfigureAwait(false);
            parent = made.RootElement.GetProperty("id").GetString();
        }

        return parent;
    }

    /// <summary>Google's protocol, with the folder found first (only when a new session is needed).</summary>
    private sealed class DriveProtocol(GoogleDriveDestination owner, string name) : IUploadProtocol
    {
        private GoogleUploadProtocol Inner(string metadata = "") => new(
            owner,
            $"{owner.Context.Endpoints.GoogleUpload}/drive/v3/files?uploadType=resumable&fields=id,webViewLink",
            metadata,
            HttpUploadDestination.MediaType(name),
            new Uri(owner.Context.Endpoints.GoogleUpload).Host);

        public int ChunkSize => Inner().ChunkSize;

        public async Task<string> StartAsync(long length, CancellationToken ct)
        {
            var folder = await owner.EnsureFolderAsync(ct).ConfigureAwait(false);
            var metadata = JsonSerializer.Serialize(new DriveFile(name, null, folder is null ? null : [folder]), SharingJsonContext.Default.DriveFile);
            return await Inner(metadata).StartAsync(length, ct).ConfigureAwait(false);
        }

        public Task<UploadAnswer> PositionAsync(string session, long length, long known, CancellationToken ct) => Inner().PositionAsync(session, length, known, ct);

        public Task<UploadAnswer> SendAsync(string session, Stream chunk, long offset, int count, long length, CancellationToken ct) => Inner().SendAsync(session, chunk, offset, count, length, ct);

        public Task AbandonAsync(string session, CancellationToken ct) => Inner().AbandonAsync(session, ct);
    }
}
