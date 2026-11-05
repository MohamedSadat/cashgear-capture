using System.ComponentModel;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Outputs.Resources;

namespace CashGear.Capture.Outputs.Sharing;

/// <summary>
/// OneDrive through Microsoft Graph: an upload session whose address takes the file without a token, in fragments of
/// multiples of 320 KiB. A GET on it says what it still expects. The link is the file's page.
/// </summary>
public sealed class OneDriveDestination(SharingContext context, CloudFolderSettings settings, string clientId)
    : SignInDestination(context, Registration(context, clientId))
{
    public override string Id => DestinationIds.OneDrive;

    /// <summary>"http://localhost" with any port is what Entra takes for a public client without extra set-up (RFC 8252 prefers the address; it allows this).</summary>
    [Localizable(false)]
    private static OAuthApp Registration(SharingContext context, string clientId) => new(
        DestinationIds.OneDrive,
        OutputsStrings.OneDrive,
        context.Endpoints.MicrosoftAuthorize,
        context.Endpoints.MicrosoftToken,
        clientId,
        null,
        ["Files.ReadWrite", "User.Read", "offline_access"],
        RedirectHost: "localhost");

    internal override IUploadProtocol Protocol(UploadWork work) => new GraphProtocol(this, work.Name);

    protected override async Task<string> FindAccountAsync(CancellationToken ct)
    {
        using var me = await CallAsync(HttpMethod.Get, $"{Context.Endpoints.Graph}/me", null, ct).ConfigureAwait(false);
        return me.RootElement.TryGetProperty("userPrincipalName", out var name) && name.GetString() is { Length: > 0 } principal ? principal : DisplayName;
    }

    protected override async Task<OutputResult> FinishAsync(string finished, UploadWork work, CancellationToken ct)
    {
        using var item = JsonDocument.Parse(finished);
        var id = item.RootElement.GetProperty("id").GetString()!;
        var link = item.RootElement.TryGetProperty("webUrl", out var web) ? web.GetString() : null;
        string? problem = null;
        if (settings.AnyoneWithLink)
        {
            problem = await TryAsync(async () =>
            {
                using var shared = await CallAsync(HttpMethod.Post, $"{Context.Endpoints.Graph}/me/drive/items/{Uri.EscapeDataString(id)}/createLink", """{"type":"view","scope":"anonymous"}""", ct).ConfigureAwait(false);
                link = shared.RootElement.GetProperty("link").GetProperty("webUrl").GetString();
            }).ConfigureAwait(false);
        }

        var uploaded = OutputsStrings.UploadedTo(DisplayName, link);
        return new OutputResult(true, problem is null ? uploaded : uploaded + "\n" + OutputsStrings.LinkNotShared(problem)) { Link = link };
    }

    [Localizable(false)]
    private sealed class GraphProtocol(OneDriveDestination owner, string name) : IUploadProtocol
    {
        /// <summary>10 MiB: a multiple of 320 KiB, as the service asks, and in its recommended range.</summary>
        public int ChunkSize => 32 * 320 * 1024;

        public async Task<string> StartAsync(long length, CancellationToken ct)
        {
            var parts = CloudNames.Parts(owner.Settings.Folder).Select(CloudNames.Clean).Append(CloudNames.Clean(name)).Select(Uri.EscapeDataString);
            var url = $"{owner.Context.Endpoints.Graph}/me/drive/root:/{string.Join('/', parts)}:/createUploadSession";
            using var reply = await owner.CallAsync(HttpMethod.Post, url, """{"item":{"@microsoft.graph.conflictBehavior":"rename"}}""", ct).ConfigureAwait(false);
            return reply.RootElement.GetProperty("uploadUrl").GetString() ?? throw new UploadHiccupException("no uploadUrl");
        }

        public async Task<UploadAnswer> PositionAsync(string session, long length, long known, CancellationToken ct)
        {
            using var response = await owner.Context.Http.Client.GetAsync(session, ct).ConfigureAwait(false);
            if ((int)response.StatusCode is 404 or 410)
            {
                throw new UploadSessionLostException($"HTTP {(int)response.StatusCode}");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw await SharingCalls.FailureAsync(response, new Uri(owner.Context.Endpoints.Graph).Host, ct).ConfigureAwait(false);
            }

            return new UploadAnswer(Expected(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) ?? known);
        }

        public async Task<UploadAnswer> SendAsync(string session, Stream chunk, long offset, int count, long length, CancellationToken ct)
        {
            // No token: the address is the permission, and the service refuses one sent along.
            using var request = new HttpRequestMessage(HttpMethod.Put, session) { Content = new StreamContent(chunk) };
            request.Content.Headers.ContentLength = count;
            request.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + count - 1, length);
            using var response = await owner.Context.Http.Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            switch ((int)response.StatusCode)
            {
                case 200 or 201:
                    return new UploadAnswer(length, await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                case 202:
                    return new UploadAnswer(Expected(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) ?? offset + count);
                case 404 or 410:
                    throw new UploadSessionLostException($"HTTP {(int)response.StatusCode}");
                case 416:
                    throw new UploadHiccupException("HTTP 416", TimeSpan.Zero); // it has more or less than was thought: ask
                default:
                    throw await SharingCalls.FailureAsync(response, new Uri(owner.Context.Endpoints.Graph).Host, ct).ConfigureAwait(false);
            }
        }

        public async Task AbandonAsync(string session, CancellationToken ct)
        {
            using var response = await owner.Context.Http.Client.DeleteAsync(session, ct).ConfigureAwait(false);
        }

        /// <summary>The start of the first of <c>nextExpectedRanges</c>.</summary>
        private static long? Expected(string json)
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("nextExpectedRanges", out var ranges) || ranges.GetArrayLength() == 0)
            {
                return null;
            }

            var first = ranges[0].GetString() ?? string.Empty;
            var dash = first.IndexOf('-', StringComparison.Ordinal);
            return long.TryParse(dash >= 0 ? first[..dash] : first, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var start) ? start : null;
        }
    }

    private CloudFolderSettings Settings => settings;
}

/// <summary>
/// Dropbox: <c>upload_session/start</c>, <c>append_v2</c> with the offset it should be at (a wrong one is answered with
/// the right one, which is how a retry finds its place) and <c>finish</c>. The arguments go in a header, so everything
/// outside ASCII in them is escaped.
/// </summary>
public sealed class DropboxDestination(SharingContext context, CloudFolderSettings settings, string appKey)
    : SignInDestination(context, Registration(context, appKey))
{
    /// <summary>The port registered with Dropbox, which compares the whole address.</summary>
    public const int RedirectPort = 53917;

    public override string Id => DestinationIds.Dropbox;

    [Localizable(false)]
    private static OAuthApp Registration(SharingContext context, string appKey) => new(
        DestinationIds.Dropbox,
        OutputsStrings.Dropbox,
        context.Endpoints.DropboxAuthorize,
        context.Endpoints.DropboxToken,
        appKey,
        null,
        ["files.content.write", "sharing.write", "account_info.read"],
        new Dictionary<string, string> { ["token_access_type"] = "offline" },
        RedirectPort: RedirectPort);

    internal override IUploadProtocol Protocol(UploadWork work) => new DropboxProtocol(this, work.Name);

    protected override async Task<string> FindAccountAsync(CancellationToken ct)
    {
        using var account = await CallAsync(HttpMethod.Post, $"{Context.Endpoints.DropboxApi}/2/users/get_current_account", "null", ct).ConfigureAwait(false);
        return account.RootElement.TryGetProperty("email", out var email) && email.GetString() is { Length: > 0 } address ? address : DisplayName;
    }

    protected override async Task<OutputResult> FinishAsync(string finished, UploadWork work, CancellationToken ct)
    {
        using var file = JsonDocument.Parse(finished);
        var path = file.RootElement.GetProperty("path_display").GetString()!;
        var folder = path[..Math.Max(0, path.LastIndexOf('/'))];
        var name = path[(path.LastIndexOf('/') + 1)..];
        var link = $"https://www.dropbox.com/home{string.Join('/', folder.Split('/').Select(Uri.EscapeDataString))}?preview={Uri.EscapeDataString(name)}";
        string? problem = null;
        if (settings.AnyoneWithLink)
        {
            problem = await TryAsync(async () => link = await ShareAsync(path, ct).ConfigureAwait(false)).ConfigureAwait(false);
        }

        var uploaded = OutputsStrings.UploadedTo(DisplayName, link);
        return new OutputResult(true, problem is null ? uploaded : uploaded + "\n" + OutputsStrings.LinkNotShared(problem)) { Link = link };
    }

    private async Task<string> ShareAsync(string path, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new DropboxShare(path, new DropboxShareSettings("public")), SharingJsonContext.Default.DropboxShare);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(SharingHttp.CallLimit);
        var url = $"{Context.Endpoints.DropboxApi}/2/sharing/create_shared_link_with_settings";
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = SharingCalls.Json(body) };
        using var response = await SendAsync(request, limit.Token).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(limit.Token).ConfigureAwait(false);
        using var reply = JsonDocument.Parse(text);
        if (response.IsSuccessStatusCode)
        {
            return reply.RootElement.GetProperty("url").GetString()!;
        }

        // Shared before: the link that is there.
        if (reply.RootElement.TryGetProperty("error", out var error)
            && error.TryGetProperty("shared_link_already_exists", out var exists)
            && exists.TryGetProperty("metadata", out var metadata))
        {
            return metadata.GetProperty("url").GetString()!;
        }

        throw new UploadRefusedException(OutputsStrings.ServiceRefused(new Uri(url).Host, (int)response.StatusCode, text.Length > 200 ? text[..200] : text));
    }

    [Localizable(false)]
    private sealed class DropboxProtocol(DropboxDestination owner, string name) : IUploadProtocol
    {
        /// <summary>A multiple of 4 MiB, as Dropbox recommends.</summary>
        public int ChunkSize => 16 * 1024 * 1024;

        private string Content => owner.Context.Endpoints.DropboxContent;

        public async Task<string> StartAsync(long length, CancellationToken ct)
        {
            using var response = await PostAsync($"{Content}/2/files/upload_session/start", """{"close":false}""", new ByteArrayContent([]), ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw await SharingCalls.FailureAsync(response, new Uri(Content).Host, ct).ConfigureAwait(false);
            }

            using var reply = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return reply.RootElement.GetProperty("session_id").GetString()!;
        }

        /// <summary>Dropbox can't be asked: the next append with the wrong offset is told the right one.</summary>
        public Task<UploadAnswer> PositionAsync(string session, long length, long known, CancellationToken ct) => Task.FromResult(new UploadAnswer(known));

        public async Task<UploadAnswer> SendAsync(string session, Stream chunk, long offset, int count, long length, CancellationToken ct)
        {
            if (count == 0)
            {
                return await FinishAsync(session, offset, ct).ConfigureAwait(false);
            }

            var content = new StreamContent(chunk);
            content.Headers.ContentLength = count;
            var argument = JsonSerializer.Serialize(new DropboxAppend(new DropboxCursor(session, offset), false), SharingJsonContext.Default.DropboxAppend);
            using var response = await PostAsync($"{Content}/2/files/upload_session/append_v2", argument, content, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return new UploadAnswer(offset + count);
            }

            return await WrongPlaceAsync(response, ct).ConfigureAwait(false);
        }

        /// <summary>Lets go by doing nothing: Dropbox forgets a session after a week, and it has no call to end one.</summary>
        public Task AbandonAsync(string session, CancellationToken ct) => Task.CompletedTask;

        private async Task<UploadAnswer> FinishAsync(string session, long offset, CancellationToken ct)
        {
            var folder = string.Concat(CloudNames.Parts(owner.Settings.Folder).Select(p => "/" + CloudNames.Clean(p)));
            var argument = JsonSerializer.Serialize(new DropboxFinish(new DropboxCursor(session, offset), new DropboxCommit($"{folder}/{CloudNames.Clean(name)}", "add", true)), SharingJsonContext.Default.DropboxFinish);
            using var response = await PostAsync($"{Content}/2/files/upload_session/finish", argument, new ByteArrayContent([]), ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? new UploadAnswer(offset, await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false))
                : await WrongPlaceAsync(response, ct).ConfigureAwait(false);
        }

        /// <summary>A 409: the offset it should have been at, a session that is gone, or something else.</summary>
        private async Task<UploadAnswer> WrongPlaceAsync(HttpResponseMessage response, CancellationToken ct)
        {
            if ((int)response.StatusCode == 409)
            {
                var text = await SharingCalls.SafeReadAsync(response, ct).ConfigureAwait(false);
                using var reply = JsonDocument.Parse(text);
                var error = reply.RootElement.GetProperty("error");
                var lookup = error.TryGetProperty("lookup_failed", out var failed) ? failed : error;
                if (lookup.TryGetProperty("correct_offset", out var correct))
                {
                    return new UploadAnswer(correct.GetInt64());
                }

                var tag = lookup.TryGetProperty(".tag", out var named) ? named.GetString() : null;
                if (tag is "not_found" or "closed" or "not_closed")
                {
                    throw new UploadSessionLostException(tag);
                }

                throw new UploadRefusedException(OutputsStrings.ServiceRefused(new Uri(Content).Host, 409, tag ?? text));
            }

            throw await SharingCalls.FailureAsync(response, new Uri(Content).Host, ct).ConfigureAwait(false);
        }

        private async Task<HttpResponseMessage> PostAsync(string url, string argument, HttpContent content, CancellationToken ct)
        {
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            request.Headers.TryAddWithoutValidation("Dropbox-API-Arg", argument);
            return await owner.SendAsync(request, ct, HttpCompletionOption.ResponseContentRead).ConfigureAwait(false);
        }
    }

    private CloudFolderSettings Settings => settings;
}

internal sealed record PlaylistItem([property: JsonPropertyName("snippet")] PlaylistSnippet Snippet);

internal sealed record PlaylistSnippet([property: JsonPropertyName("playlistId")] string PlaylistId, [property: JsonPropertyName("resourceId")] VideoResource ResourceId);

internal sealed record VideoResource([property: JsonPropertyName("kind")] string Kind, [property: JsonPropertyName("videoId")] string VideoId);

internal sealed record DriveFile(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("mimeType"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MimeType,
    [property: JsonPropertyName("parents"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Parents);

internal sealed record DropboxCursor([property: JsonPropertyName("session_id")] string SessionId, [property: JsonPropertyName("offset")] long Offset);

internal sealed record DropboxAppend([property: JsonPropertyName("cursor")] DropboxCursor Cursor, [property: JsonPropertyName("close")] bool Close);

internal sealed record DropboxCommit([property: JsonPropertyName("path")] string Path, [property: JsonPropertyName("mode")] string Mode, [property: JsonPropertyName("autorename")] bool AutoRename);

internal sealed record DropboxFinish([property: JsonPropertyName("cursor")] DropboxCursor Cursor, [property: JsonPropertyName("commit")] DropboxCommit Commit);

internal sealed record DropboxShare([property: JsonPropertyName("path")] string Path, [property: JsonPropertyName("settings")] DropboxShareSettings Settings);

internal sealed record DropboxShareSettings([property: JsonPropertyName("requested_visibility")] string RequestedVisibility);

/// <summary>The default encoder escapes everything outside ASCII, which Dropbox's header needs and the others don't mind.</summary>
[JsonSerializable(typeof(PlaylistItem))]
[JsonSerializable(typeof(DriveFile))]
[JsonSerializable(typeof(DropboxAppend))]
[JsonSerializable(typeof(DropboxFinish))]
[JsonSerializable(typeof(DropboxShare))]
internal sealed partial class SharingJsonContext : JsonSerializerContext;
