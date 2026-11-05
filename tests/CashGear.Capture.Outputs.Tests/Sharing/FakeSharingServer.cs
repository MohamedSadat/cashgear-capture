using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.Outputs.Tests.Sharing;

/// <summary>What goes wrong, and when: at the first request whose chunk starts at or after <see cref="At"/>.</summary>
internal sealed record Fault(long At, FaultKind Kind, int Status = 503, long AfterBytes = 0, int? RetryAfterSeconds = null);

internal enum FaultKind
{
    /// <summary>The connection goes in the middle of the chunk: what had arrived is kept, in whole units.</summary>
    DropMidBody,

    /// <summary>The chunk arrives whole and is kept, but the connection goes before the answer.</summary>
    DropBeforeReply,

    /// <summary>An answer with a status.</summary>
    Status,

    /// <summary>The service forgets the session.</summary>
    LoseSession,
}

/// <summary>A file a fake service has been sent.</summary>
internal sealed class FakeUpload(string id, string service, long unit, string? metadata, string? path)
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    public string Id => id;

    public string Service => service;

    public long Unit => unit;

    public string? Metadata => metadata;

    public string? Path => path;

    public long Received { get; private set; }

    public long Total { get; set; }

    public bool Finished { get; set; }

    public bool Lost { get; set; }

    public byte[]? Hash { get; private set; }

    /// <summary>Whether a token came with the chunks.</summary>
    public bool SawToken { get; set; }

    public void Keep(ReadOnlySpan<byte> bytes)
    {
        _hash.AppendData(bytes);
        Received += bytes.Length;
    }

    public void End() => Hash = _hash.GetHashAndReset();
}

/// <summary>
/// A pretend Google, Microsoft and Dropbox on 127.0.0.1, as strict as the real ones where it matters: PKCE is checked, a
/// token has to be good, chunks have to come where they should. Tests make it drop connections and forget sessions.
/// </summary>
internal sealed class FakeSharingServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, (string Challenge, string Redirect, string Client)> _codes = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _access = new();
    private readonly ConcurrentDictionary<string, bool> _refresh = new();
    private readonly List<Fault> _faults = [];
    private int _counter;

    private FakeSharingServer(WebApplication app, string address)
    {
        _app = app;
        Address = address.TrimEnd('/');
    }

    public string Address { get; }

    public ConcurrentDictionary<string, FakeUpload> Uploads { get; } = new();

    /// <summary>Every byte of file that reached the server, kept or not.</summary>
    public long BytesOnTheWire => Interlocked.Read(ref _wire);

    private long _wire;

    public int TokenRefreshes => Volatile.Read(ref _refreshes);

    private int _refreshes;

    /// <summary>Requests, as "METHOD path" (the query left out).</summary>
    public ConcurrentQueue<string> Requests { get; } = new();

    /// <summary>Bodies of JSON requests, by path.</summary>
    public ConcurrentDictionary<string, string> Bodies { get; } = new();

    /// <summary>The queries of authorize requests.</summary>
    public ConcurrentQueue<IQueryCollection> Authorizes { get; } = new();

    /// <summary>The thumbnail YouTube was sent.</summary>
    public byte[]? Thumbnail { get; private set; }

    /// <summary>The next sign-in's page says no.</summary>
    public bool DeclineSignIn { get; set; }

    /// <summary>What a Google desktop client must send along.</summary>
    public string? GoogleSecret { get; set; }

    /// <summary>New refresh tokens each time, as Microsoft gives.</summary>
    public bool RotateRefreshTokens { get; set; }

    /// <summary>Access tokens last this long.</summary>
    public TimeSpan TokenLife { get; set; } = TimeSpan.FromHours(1);

    /// <summary>No request gets an answer: the connection simply goes.</summary>
    public bool OutOfService { get; set; }

    /// <summary>Held until a test lets go: the body isn't read (a slow line).</summary>
    public TaskCompletionSource? Slow { get; set; }

    /// <summary>Folders Drive has, by name.</summary>
    public ConcurrentDictionary<string, string> DriveFolders { get; } = new();

    public IReadOnlyDictionary<string, string> YouTubePlaylists { get; set; } = new Dictionary<string, string> { ["PL1"] = "Tutorials" };

    public static async Task<FakeSharingServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = null);
        builder.Logging.ClearProviders();
        var app = builder.Build();
        FakeSharingServer? server = null;
        app.Run(async context => await server!.HandleAsync(context));
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        server = new FakeSharingServer(app, address);
        return server;
    }

    public async ValueTask DisposeAsync()
    {
        Slow?.TrySetResult();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    public void Add(Fault fault)
    {
        lock (_faults)
        {
            _faults.Add(fault);
        }
    }

    /// <summary>Every access token stops working at once, as if an hour had passed.</summary>
    public void ExpireAccessTokens() => _access.Clear();

    /// <summary>Refresh tokens stop working: revoked by the user, say.</summary>
    public void RevokeRefreshTokens() => _refresh.Clear();

    public FakeUpload OnlyUpload() => Uploads.Values.Single();

    private string Next(string prefix) => $"{prefix}-{Interlocked.Increment(ref _counter)}";

    private async Task HandleAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        Requests.Enqueue($"{context.Request.Method} {path}");
        if (OutOfService)
        {
            context.Abort();
            return;
        }

        if (path.EndsWith("/authorize", StringComparison.Ordinal))
        {
            Authorize(context);
        }
        else if (path.EndsWith("/token", StringComparison.Ordinal))
        {
            await TokenAsync(context, path.StartsWith("/google", StringComparison.Ordinal));
        }
        else if (path.StartsWith("/session/g/", StringComparison.Ordinal))
        {
            await GoogleChunkAsync(context, path["/session/g/".Length..]);
        }
        else if (path.StartsWith("/session/m/", StringComparison.Ordinal))
        {
            await GraphChunkAsync(context, path["/session/m/".Length..]);
        }
        else if (!Authorized(context))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = new { code = 401, message = "Invalid token" } });
        }
        else if (path.StartsWith("/google/", StringComparison.Ordinal))
        {
            await GoogleAsync(context, path);
        }
        else if (path.StartsWith("/microsoft/graph", StringComparison.Ordinal))
        {
            await GraphAsync(context, path["/microsoft/graph".Length..]);
        }
        else if (path.StartsWith("/dropbox/", StringComparison.Ordinal))
        {
            await DropboxAsync(context, path);
        }
        else
        {
            context.Response.StatusCode = 404;
        }
    }

    private void Authorize(HttpContext context)
    {
        var query = context.Request.Query;
        Authorizes.Enqueue(query);
        var redirect = query["redirect_uri"].ToString();
        var state = Uri.EscapeDataString(query["state"].ToString());
        if (DeclineSignIn)
        {
            context.Response.Redirect($"{redirect}?error=access_denied&error_description={Uri.EscapeDataString("The user said no")}&state={state}");
            return;
        }

        if (query["response_type"] != "code" || query["code_challenge_method"] != "S256" || query["code_challenge"].ToString().Length != 43)
        {
            context.Response.StatusCode = 400;
            return;
        }

        var code = Next("code");
        _codes[code] = (query["code_challenge"].ToString(), redirect, query["client_id"].ToString());
        context.Response.Redirect($"{redirect}?code={code}&state={state}");
    }

    private async Task TokenAsync(HttpContext context, bool google)
    {
        var form = await context.Request.ReadFormAsync();
        if (google && GoogleSecret is not null && form["client_secret"] != GoogleSecret)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_client" });
            return;
        }

        string refresh;
        if (form["grant_type"] == "authorization_code")
        {
            if (!_codes.TryRemove(form["code"].ToString(), out var issued)
                || issued.Redirect != form["redirect_uri"]
                || issued.Client != form["client_id"]
                || Challenge(form["code_verifier"].ToString()) != issued.Challenge)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new { error = "invalid_grant", error_description = "Bad code, redirect or verifier" });
                return;
            }

            refresh = Next("refresh");
            _refresh[refresh] = true;
        }
        else if (form["grant_type"] == "refresh_token" && _refresh.ContainsKey(form["refresh_token"].ToString()))
        {
            Interlocked.Increment(ref _refreshes);
            refresh = form["refresh_token"].ToString();
            if (RotateRefreshTokens)
            {
                _refresh.TryRemove(refresh, out _);
                refresh = Next("refresh");
                _refresh[refresh] = true;
            }
        }
        else
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_grant", error_description = "Token has been revoked" });
            return;
        }

        var access = Next("access");
        _access[access] = DateTimeOffset.UtcNow + TokenLife;
        await context.Response.WriteAsJsonAsync(new { access_token = access, expires_in = (int)TokenLife.TotalSeconds, refresh_token = refresh, token_type = "bearer" });
    }

    private static string Challenge(string verifier) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private bool Authorized(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.Ordinal) && _access.TryGetValue(header["Bearer ".Length..], out var until) && until > DateTimeOffset.UtcNow;
    }

    private static async Task<string> BodyAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private async Task GoogleAsync(HttpContext context, string path)
    {
        var request = context.Request;
        if (path.EndsWith("/thumbnails/set", StringComparison.Ordinal))
        {
            using var copy = new MemoryStream();
            await request.Body.CopyToAsync(copy);
            Thumbnail = copy.ToArray();
            await context.Response.WriteAsJsonAsync(new { kind = "youtube#thumbnailSetResponse" });
        }
        else if (path.Contains("/upload/", StringComparison.Ordinal) && request.Method == "POST")
        {
            var body = await BodyAsync(context);
            Bodies[path] = body;
            var id = Next("g");
            Uploads[id] = new FakeUpload(id, path.Contains("youtube", StringComparison.Ordinal) ? "youtube" : "drive", 256 * 1024, body, null) { Total = long.Parse(request.Headers["X-Upload-Content-Length"].ToString(), System.Globalization.CultureInfo.InvariantCulture) };
            context.Response.Headers.Location = $"{Address}/session/g/{id}";
        }
        else if (path.EndsWith("/youtube/v3/channels", StringComparison.Ordinal))
        {
            await context.Response.WriteAsJsonAsync(new { items = new[] { new { snippet = new { title = "My Channel" } } } });
        }
        else if (path.EndsWith("/youtube/v3/playlists", StringComparison.Ordinal))
        {
            await context.Response.WriteAsJsonAsync(new { items = YouTubePlaylists.Select(p => new { id = p.Key, snippet = new { title = p.Value } }) });
        }
        else if (path.EndsWith("/youtube/v3/playlistItems", StringComparison.Ordinal))
        {
            Bodies[path] = await BodyAsync(context);
            await context.Response.WriteAsJsonAsync(new { id = "item" });
        }
        else if (path.EndsWith("/drive/v3/about", StringComparison.Ordinal))
        {
            await context.Response.WriteAsJsonAsync(new { user = new { emailAddress = "me@example.test" } });
        }
        else if (path.EndsWith("/drive/v3/files", StringComparison.Ordinal))
        {
            if (request.Method == "GET")
            {
                var query = request.Query["q"].ToString();
                var known = DriveFolders.Where(f => query.Contains($"name='{f.Key.Replace("'", "\\'", StringComparison.Ordinal)}'", StringComparison.Ordinal)).Select(f => new { id = f.Value }).ToList();
                await context.Response.WriteAsJsonAsync(new { files = known });
            }
            else
            {
                var body = await BodyAsync(context);
                using var made = JsonDocument.Parse(body);
                var id = Next("folder");
                DriveFolders[made.RootElement.GetProperty("name").GetString()!] = id;
                await context.Response.WriteAsJsonAsync(new { id });
            }
        }
        else if (path.EndsWith("/permissions", StringComparison.Ordinal))
        {
            Bodies[path] = await BodyAsync(context);
            await context.Response.WriteAsJsonAsync(new { id = "permission" });
        }
        else
        {
            context.Response.StatusCode = 404;
        }
    }

    private static string GoogleResource(FakeUpload upload) => upload.Service == "youtube"
        ? $$"""{ "id": "video-{{upload.Id}}" }"""
        : $$"""{ "id": "file-{{upload.Id}}", "webViewLink": "https://drive.example.test/{{upload.Id}}" }""";

    /// <summary>"bytes a-b/n" or "bytes */n".</summary>
    private static (long? From, long To, long Total) Range(string header)
    {
        var text = header["bytes ".Length..];
        var slash = text.IndexOf('/', StringComparison.Ordinal);
        var total = long.Parse(text[(slash + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        if (text[0] == '*')
        {
            return (null, -1, total);
        }

        var dash = text.IndexOf('-', StringComparison.Ordinal);
        return (long.Parse(text[..dash], System.Globalization.CultureInfo.InvariantCulture), long.Parse(text[(dash + 1)..slash], System.Globalization.CultureInfo.InvariantCulture), total);
    }

    private Fault? TakeFault(long position)
    {
        lock (_faults)
        {
            var fault = _faults.FirstOrDefault(f => position >= f.At);
            if (fault is not null)
            {
                _faults.Remove(fault);
            }

            return fault;
        }
    }

    /// <summary>Reads a chunk, keeping what the fault lets through. False if the connection went, or the answer was given.</summary>
    private async Task<bool> TakeChunkAsync(HttpContext context, FakeUpload upload, int length)
    {
        if (Slow is { } slow)
        {
            await slow.Task;
        }

        var fault = TakeFault(upload.Received);
        if (fault?.Kind == FaultKind.Status)
        {
            // Read it first, as a server does before it can fail it.
            var ignored = new byte[64 * 1024];
            while (await context.Request.Body.ReadAsync(ignored, context.RequestAborted) > 0)
            {
            }

            context.Response.StatusCode = fault.Status;
            if (fault.RetryAfterSeconds is { } seconds)
            {
                context.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            return false;
        }

        if (fault?.Kind == FaultKind.LoseSession)
        {
            upload.Lost = true;
            context.Response.StatusCode = upload.Service == "dropbox" ? 409 : 404;
            if (upload.Service == "dropbox")
            {
                await context.Response.WriteAsync("""{"error":{".tag":"not_found"}}""");
            }

            return false;
        }

        var buffer = new byte[length];
        var got = 0;
        var limit = fault?.Kind == FaultKind.DropMidBody ? Math.Min(length, fault.AfterBytes) : length;
        try
        {
            while (got < limit)
            {
                var read = await context.Request.Body.ReadAsync(buffer.AsMemory(got, (int)limit - got));
                if (read == 0)
                {
                    break;
                }

                got += read;
                Interlocked.Add(ref _wire, read);
            }
        }
        catch (Exception e) when (e is IOException or Microsoft.AspNetCore.Http.BadHttpRequestException or OperationCanceledException)
        {
            // the client went
        }

        if (fault?.Kind == FaultKind.DropMidBody || got < length)
        {
            var whole = upload.Unit > 0 ? got / upload.Unit * upload.Unit : 0;
            upload.Keep(buffer.AsSpan(0, (int)whole));
            context.Abort();
            return false;
        }

        upload.Keep(buffer.AsSpan(0, got));
        if (fault?.Kind == FaultKind.DropBeforeReply)
        {
            if (upload.Total > 0 && upload.Received >= upload.Total)
            {
                upload.Finished = true;
                upload.End();
            }

            context.Abort();
            return false;
        }

        return true;
    }

    private async Task GoogleChunkAsync(HttpContext context, string id)
    {
        if (!Uploads.TryGetValue(id, out var upload) || upload.Lost)
        {
            context.Response.StatusCode = 404;
            return;
        }

        if (context.Request.Method == "DELETE")
        {
            upload.Lost = true;
            context.Response.StatusCode = 499;
            return;
        }

        if (!Authorized(context))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = new { code = 401, message = "Invalid token" } });
            return;
        }

        upload.SawToken = true;
        var (from, to, total) = Range(context.Request.Headers.ContentRange.ToString());
        if (from is { } start)
        {
            if (start != upload.Received)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new { error = new { code = 400, message = $"Expected {upload.Received}, got {start}" } });
                return;
            }

            if (to + 1 < total && (to + 1 - start) % (256 * 1024) != 0)
            {
                context.Response.StatusCode = 400;
                return;
            }

            if (!await TakeChunkAsync(context, upload, (int)(to + 1 - start)))
            {
                return;
            }

            if (upload.Received == total && !upload.Finished)
            {
                upload.Finished = true;
                upload.End();
            }
        }

        if (upload.Finished)
        {
            context.Response.StatusCode = 200;
            await context.Response.WriteAsync(GoogleResource(upload));
            return;
        }

        context.Response.StatusCode = 308;
        if (upload.Received > 0)
        {
            context.Response.Headers.Range = $"bytes=0-{upload.Received - 1}";
        }
    }

    private async Task GraphAsync(HttpContext context, string path)
    {
        if (path == "/me")
        {
            await context.Response.WriteAsJsonAsync(new { userPrincipalName = "me@example.test" });
        }
        else if (path.EndsWith(":/createUploadSession", StringComparison.Ordinal))
        {
            Bodies[path] = await BodyAsync(context);
            var id = Next("m");
            var name = path["/me/drive/root:/".Length..^":/createUploadSession".Length];
            Uploads[id] = new FakeUpload(id, "onedrive", 320 * 1024, null, name);
            await context.Response.WriteAsJsonAsync(new { uploadUrl = $"{Address}/session/m/{id}", expirationDateTime = "2030-01-01T00:00:00Z" });
        }
        else if (path.EndsWith("/createLink", StringComparison.Ordinal))
        {
            Bodies[path] = await BodyAsync(context);
            await context.Response.WriteAsJsonAsync(new { link = new { webUrl = "https://1drv.example.test/shared" } });
        }
        else
        {
            context.Response.StatusCode = 404;
        }
    }

    private async Task GraphChunkAsync(HttpContext context, string id)
    {
        if (!Uploads.TryGetValue(id, out var upload) || upload.Lost)
        {
            context.Response.StatusCode = 404;
            return;
        }

        if (context.Request.Headers.Authorization.Count > 0)
        {
            upload.SawToken = true;
        }

        if (context.Request.Method == "DELETE")
        {
            upload.Lost = true;
            context.Response.StatusCode = 204;
            return;
        }

        if (context.Request.Method == "GET")
        {
            await context.Response.WriteAsJsonAsync(new { nextExpectedRanges = new[] { $"{upload.Received}-" } });
            return;
        }

        var (from, to, total) = Range(context.Request.Headers.ContentRange.ToString());
        if (from != upload.Received)
        {
            context.Response.StatusCode = 416;
            return;
        }

        if (to + 1 < total && (to + 1 - from.Value) % (320 * 1024) != 0)
        {
            context.Response.StatusCode = 400;
            return;
        }

        upload.Total = total;
        if (!await TakeChunkAsync(context, upload, (int)(to + 1 - from.Value)))
        {
            return;
        }

        if (upload.Received == total)
        {
            upload.Finished = true;
            upload.End();
            context.Response.StatusCode = 201;
            await context.Response.WriteAsync($$"""{ "id": "item-{{id}}", "webUrl": "https://onedrive.example.test/{{id}}" }""");
            return;
        }

        context.Response.StatusCode = 202;
        await context.Response.WriteAsJsonAsync(new { nextExpectedRanges = new[] { $"{upload.Received}-" } });
    }

    private async Task DropboxAsync(HttpContext context, string path)
    {
        var argument = context.Request.Headers["Dropbox-API-Arg"].ToString();
        if (path.EndsWith("/users/get_current_account", StringComparison.Ordinal))
        {
            await BodyAsync(context);
            await context.Response.WriteAsJsonAsync(new { email = "me@example.test" });
        }
        else if (path.EndsWith("/sharing/create_shared_link_with_settings", StringComparison.Ordinal))
        {
            Bodies[path] = await BodyAsync(context);
            await context.Response.WriteAsJsonAsync(new { url = "https://dropbox.example.test/s/shared" });
        }
        else if (path.EndsWith("/upload_session/start", StringComparison.Ordinal))
        {
            await BodyAsync(context);
            var id = Next("d");
            Uploads[id] = new FakeUpload(id, "dropbox", 0, argument, null);
            await context.Response.WriteAsJsonAsync(new { session_id = id });
        }
        else if (path.EndsWith("/upload_session/append_v2", StringComparison.Ordinal) || path.EndsWith("/upload_session/finish", StringComparison.Ordinal))
        {
            await DropboxChunkAsync(context, path, argument);
        }
        else
        {
            context.Response.StatusCode = 404;
        }
    }

    private async Task DropboxChunkAsync(HttpContext context, string path, string argument)
    {
        using var parsed = JsonDocument.Parse(argument);
        var cursor = parsed.RootElement.GetProperty("cursor");
        var id = cursor.GetProperty("session_id").GetString()!;
        var offset = cursor.GetProperty("offset").GetInt64();
        if (!Uploads.TryGetValue(id, out var upload) || upload.Lost)
        {
            context.Response.StatusCode = 409;
            await context.Response.WriteAsync("""{"error":{".tag":"lookup_failed","lookup_failed":{".tag":"not_found"}}}""");
            return;
        }

        if (offset != upload.Received)
        {
            context.Response.StatusCode = 409;
            await context.Response.WriteAsync("""{"error":{".tag":"lookup_failed","lookup_failed":{".tag":"incorrect_offset","correct_offset":""" + upload.Received.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}}");
            return;
        }

        var length = (int)(context.Request.ContentLength ?? 0);
        if (path.EndsWith("/finish", StringComparison.Ordinal))
        {
            var commit = parsed.RootElement.GetProperty("commit").GetProperty("path").GetString()!;
            upload.Finished = true;
            upload.End();
            Bodies[path] = argument;
            await context.Response.WriteAsJsonAsync(new { id = "id:" + id, name = commit[(commit.LastIndexOf('/') + 1)..], path_display = commit });
            return;
        }

        if (await TakeChunkAsync(context, upload, length))
        {
            await context.Response.WriteAsync("null");
        }
    }
}
