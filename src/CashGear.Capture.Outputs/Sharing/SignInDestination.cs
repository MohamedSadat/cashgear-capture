using System.ComponentModel;
using System.Net.Http.Headers;
using System.Text.Json;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Outputs.Resources;

namespace CashGear.Capture.Outputs.Sharing;

/// <summary>Where the services are. Tests point them at a server on this PC; nothing the user can edit does.</summary>
public sealed record SharingEndpoints(
    string GoogleAuthorize,
    string GoogleToken,
    string GoogleApi,
    string GoogleUpload,
    string MicrosoftAuthorize,
    string MicrosoftToken,
    string Graph,
    string DropboxAuthorize,
    string DropboxToken,
    string DropboxApi,
    string DropboxContent)
{
    [Localizable(false)]
    public static SharingEndpoints Real { get; } = new(
        "https://accounts.google.com/o/oauth2/v2/auth",
        "https://oauth2.googleapis.com/token",
        "https://www.googleapis.com",
        "https://www.googleapis.com/upload",
        "https://login.microsoftonline.com/common/oauth2/v2.0/authorize",
        "https://login.microsoftonline.com/common/oauth2/v2.0/token",
        "https://graph.microsoft.com/v1.0",
        "https://www.dropbox.com/oauth2/authorize",
        "https://api.dropboxapi.com/oauth2/token",
        "https://api.dropboxapi.com",
        "https://content.dropboxapi.com");

    /// <summary>Everything under one address, each service in a folder of its own (a fake server).</summary>
    [Localizable(false)]
    public static SharingEndpoints At(string address) => new(
        address + "/google/authorize",
        address + "/google/token",
        address + "/google/api",
        address + "/google/upload",
        address + "/microsoft/authorize",
        address + "/microsoft/token",
        address + "/microsoft/graph",
        address + "/dropbox/authorize",
        address + "/dropbox/token",
        address + "/dropbox/api",
        address + "/dropbox/content");
}

/// <summary>What YouTube's upload needs beyond the file; what a cloud drive needs is only the file.</summary>
/// <param name="Title">The video's title (null: the file's name).</param>
/// <param name="Description">Its description, with the chapters' times when it has them (null: as Settings says).</param>
/// <param name="Tags">Its tags (null: as Settings says).</param>
/// <param name="Privacy">Who can see it (null: as Settings says).</param>
/// <param name="Playlist">The playlist it joins (null: as Settings says; empty: none).</param>
/// <param name="ThumbnailPath">A picture sent along with it.</param>
public sealed record UploadDetails(
    string? Title = null,
    string? Description = null,
    IReadOnlyList<string>? Tags = null,
    VideoPrivacy? Privacy = null,
    string? Playlist = null,
    string? ThumbnailPath = null);

/// <summary>One file on its way, as the background queue hands it to a destination.</summary>
/// <param name="Waiting">Told when the connection has gone and it is waiting to go on, and when it goes on.</param>
public sealed record UploadWork(string Path, string Name, IUploadCheckpoint Checkpoint, IProgress<long>? Progress = null, UploadDetails? Details = null, Action<bool>? Waiting = null);

/// <summary>A destination that sends a file in pieces and can go on from where it was stopped.</summary>
public interface IResumableDestination : IOutputDestination
{
    Task<OutputResult> UploadAsync(UploadWork work, CancellationToken ct);

    /// <summary>The job was cancelled: lets go of its session on the service.</summary>
    Task AbandonAsync(UploadWork work);
}

/// <summary>A destination with an account to sign in to (Settings' Sign in and Sign out buttons).</summary>
public interface ISignInDestination : IOutputDestination
{
    bool IsSignedIn { get; }

    /// <summary>The account signed in, when known.</summary>
    string? Account { get; }

    /// <summary>Opens the browser, waits for the user, and finds out the account's name. Fails with <see cref="SignInRequiredException"/> saying why.</summary>
    Task SignInAsync(CancellationToken ct);

    void SignOut();
}

/// <summary>What the services' destinations share.</summary>
public abstract class SignInDestination(SharingContext context, OAuthApp app) : ISignInDestination, IResumableDestination
{
    protected SharingContext Context { get; } = context;

    protected OAuthApp App { get; } = app;

    public abstract string Id { get; }

    public string DisplayName => App.Name;

    public bool SupportsVideo => true;

    public virtual bool SupportsImages => true;

    public bool IsUpload => true;

    public bool IsSignedIn => Context.OAuth.IsSignedIn(App);

    public string? Account => Context.OAuth.Account(App);

    public async Task SignInAsync(CancellationToken ct)
    {
        await Context.OAuth.SignInAsync(App, ct).ConfigureAwait(false);
        try
        {
            Context.OAuth.RememberAccount(App, await FindAccountAsync(ct).ConfigureAwait(false));
        }
        catch (Exception e) when (e is HttpRequestException or UploadRefusedException or UploadHiccupException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // Signed in all the same: it just can't say as whom yet.
        }
    }

    public void SignOut() => Context.OAuth.SignOut(App);

    public async Task<OutputResult> SendAsync(OutputItem item, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Kind == OutputKind.Image && !SupportsImages)
        {
            return OutputResult.Skip(OutputsStrings.NoPictures(DisplayName));
        }

        var path = await item.GetFileAsync(ct).ConfigureAwait(false);
        return await UploadAsync(new UploadWork(path, Path.GetFileName(path), new MemoryCheckpoint(), item.Progress), ct).ConfigureAwait(false);
    }

    public async Task<OutputResult> UploadAsync(UploadWork work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);
        try
        {
            var protocol = Protocol(work);
            var finished = await Context.Uploader.RunAsync(protocol, work.Path, work.Checkpoint, work.Progress, ct, work.Waiting).ConfigureAwait(false);
            return await FinishAsync(finished, work, ct).ConfigureAwait(false);
        }
        catch (SignInRequiredException e)
        {
            return OutputResult.Failed(e.Message);
        }
        catch (UploadRefusedException e)
        {
            return OutputResult.Failed(e.Message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return OutputResult.Failed(OutputsStrings.CantReadFile(Path.GetFileName(work.Path), e.Message));
        }
    }

    public Task AbandonAsync(UploadWork work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return ResumableUploader.AbandonAsync(Protocol(work), work.Checkpoint);
    }

    /// <summary>Signed in, and the service knows the token: says as whom. Nothing is uploaded.</summary>
    public async Task<OutputResult> TestAsync(CancellationToken ct)
    {
        if (!IsSignedIn)
        {
            return OutputResult.Failed(OutputsStrings.NotSignedIn(DisplayName));
        }

        try
        {
            var account = await FindAccountAsync(ct).ConfigureAwait(false);
            Context.OAuth.RememberAccount(App, account);
            return OutputResult.Done(OutputsStrings.SignedInAs(DisplayName, account));
        }
        catch (SignInRequiredException e)
        {
            return OutputResult.Failed(e.Message);
        }
        catch (Exception e) when (e is HttpRequestException or UploadRefusedException or UploadHiccupException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return OutputResult.Failed(OutputsStrings.CouldNotReach(new Uri(App.TokenUrl).Host, e.Message));
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return OutputResult.Failed(OutputsStrings.CouldNotReach(new Uri(App.TokenUrl).Host, e.Message));
        }
    }

    internal abstract IUploadProtocol Protocol(UploadWork work);

    /// <summary>The account's name, from the service.</summary>
    protected abstract Task<string> FindAccountAsync(CancellationToken ct);

    /// <summary>The whole file has arrived: the link, and what goes with it. Anything that fails here only loses that.</summary>
    protected abstract Task<OutputResult> FinishAsync(string finished, UploadWork work, CancellationToken ct);

    /// <summary>A request with the token, sent; a token the service refuses is forgotten and counts as a failure that passes.</summary>
    protected internal async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct, HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await Context.OAuth.AccessTokenAsync(App, ct).ConfigureAwait(false));
        var response = await Context.Http.Client.SendAsync(request, completion, ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            Context.OAuth.Invalidate(App);
        }

        return response;
    }

    /// <summary>A call that sends no file, within its own limit, returning the reply's JSON. A failure throws.</summary>
    protected async Task<JsonDocument> CallAsync(HttpMethod method, string url, string? json, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(SharingHttp.CallLimit);
        using var request = new HttpRequestMessage(method, url);
        if (json is not null)
        {
            request.Content = SharingCalls.Json(json);
        }

        using var response = await SendAsync(request, limit.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await SharingCalls.FailureAsync(response, new Uri(url).Host, ct).ConfigureAwait(false);
        }

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(limit.Token).ConfigureAwait(false));
    }

    /// <summary>A small step after the upload that may fail without losing the upload; says what stopped it, or null.</summary>
    protected static async Task<string?> TryAsync(Func<Task> step)
    {
        ArgumentNullException.ThrowIfNull(step);
        try
        {
            await step().ConfigureAwait(false);
            return null;
        }
        catch (Exception e) when (e is HttpRequestException or UploadRefusedException or UploadHiccupException or SignInRequiredException or JsonException or KeyNotFoundException or InvalidOperationException or IOException)
        {
            return e.Message;
        }
    }
}

/// <summary>What the services' destinations need, kept for the app's life (the destinations are made anew from the settings each time).</summary>
public sealed class SharingContext(OAuthClient oauth, SharingHttp http, ResumableUploader uploader, SharingEndpoints endpoints)
{
    public OAuthClient OAuth { get; } = oauth;

    public SharingHttp Http { get; } = http;

    public ResumableUploader Uploader { get; } = uploader;

    public SharingEndpoints Endpoints { get; } = endpoints;
}

/// <summary>The app's registrations with the services, built in by the packaging script.</summary>
public sealed record BuiltInClients(SharingClients Clients);

/// <summary>
/// YouTube, Google Drive, OneDrive and Dropbox, each where the build (or settings.json) has its registration: without one
/// it can't sign in, so it isn't offered.
/// </summary>
public sealed class SignInDestinations(SharingContext context, BuiltInClients builtIn) : IDestinationSource
{
    public IEnumerable<IOutputDestination> Create(AppSettings settings, Credentials.ICredentialStore credentials)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var sharing = settings.Destinations.Sharing ?? new SharingSettings();
        var clients = (sharing.Clients ?? new SharingClients()).Over(builtIn.Clients);
        var endpoints = context.Endpoints;
        if (clients.Google is { } google)
        {
            yield return new YouTubeDestination(context, sharing.YouTube ?? new YouTubeSettings(), google, clients.GoogleSecret);
            yield return new GoogleDriveDestination(context, sharing.GoogleDrive ?? new CloudFolderSettings(), google, clients.GoogleSecret);
        }

        if (clients.Microsoft is { } microsoft)
        {
            yield return new OneDriveDestination(context, sharing.OneDrive ?? new CloudFolderSettings(), microsoft);
        }

        if (clients.Dropbox is { } dropbox)
        {
            yield return new DropboxDestination(context, sharing.Dropbox ?? new CloudFolderSettings(), dropbox);
        }
    }
}

/// <summary>Names of files as the services allow them.</summary>
internal static class CloudNames
{
    /// <summary>The name with what OneDrive and Dropbox refuse replaced, and no trailing dot or space.</summary>
    public static string Clean(string name)
    {
        var cleaned = new string([.. name.Select(c => c is '"' or '*' or ':' or '<' or '>' or '?' or '/' or '\\' or '|' || c < ' ' ? '_' : c)]).Trim().TrimEnd('.');
        return cleaned.Length == 0 ? "_" : cleaned;
    }

    public static IReadOnlyList<string> Parts(string? folder) => (folder ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
}
