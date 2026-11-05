using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CashGear.Capture.Outputs.Credentials;
using CashGear.Capture.Outputs.Resources;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.Outputs.Sharing;

/// <summary>A sign-in that is needed before anything can go there: nobody has signed in, or the service no longer takes the token.</summary>
public sealed class SignInRequiredException : Exception
{
    public SignInRequiredException()
    {
    }

    public SignInRequiredException([Localizable(true)] string message)
        : base(message)
    {
    }

    public SignInRequiredException([Localizable(true)] string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Opens the service's sign-in page in the user's own browser, as RFC 8252 asks (tests follow the address themselves).</summary>
public interface ISignInBrowser
{
    void Open(Uri address);
}

internal sealed class SystemBrowser : ISignInBrowser
{
    public void Open(Uri address) => Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true })?.Dispose();
}

/// <summary>One service as an OAuth 2.0 client for installed apps sees it.</summary>
/// <param name="Key">What its tokens are kept under (<c>youtube</c>): each destination signs in by itself.</param>
/// <param name="Name">Its name, for messages.</param>
/// <param name="ClientSecret">Only Google gives one for a desktop app; an installed app can't keep it secret, so it proves nothing, and PKCE is what protects the code.</param>
/// <param name="AuthorizeExtras">What the sign-in page needs to give a refresh token.</param>
/// <param name="RedirectHost"><c>127.0.0.1</c> (RFC 8252 section 7.3) or, where a service only takes that, <c>localhost</c>.</param>
/// <param name="RedirectPort">0 for any free port. A service that compares the whole address needs one it knows.</param>
public sealed record OAuthApp(
    string Key,
    string Name,
    string AuthorizeUrl,
    string TokenUrl,
    string ClientId,
    string? ClientSecret,
    IReadOnlyList<string> Scopes,
    IReadOnlyDictionary<string, string>? AuthorizeExtras = null,
    string RedirectHost = "127.0.0.1",
    int RedirectPort = 0);

/// <summary>
/// The one OAuth 2.0 client (authorization code with PKCE, loopback redirect, RFC 8252). Refresh tokens are kept with
/// DPAPI like the passwords, at once (a sign-in is its own act, not part of the Settings window's OK); access tokens
/// only in memory. Nothing here ever logs a token or a code.
/// </summary>
public sealed partial class OAuthClient(ICredentialStore credentials, SharingHttp http, ISignInBrowser browser, ILogger<OAuthClient> logger, TimeProvider? time = null)
{
    /// <summary>How long the user has to finish signing in, in the browser.</summary>
    public static readonly TimeSpan SignInLimit = TimeSpan.FromMinutes(5);

    /// <summary>An access token this close to its end is refreshed first.</summary>
    private static readonly TimeSpan ExpirySlack = TimeSpan.FromMinutes(2);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, AccessToken> _tokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SemaphoreSlim> _refreshes = new(StringComparer.OrdinalIgnoreCase);

    public bool IsSignedIn(OAuthApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return credentials.Get(CredentialKeys.SignIn(app.Key)) is not null;
    }

    /// <summary>The account shown as signed in, when it is known.</summary>
    public string? Account(OAuthApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return credentials.Get(CredentialKeys.SignInAccount(app.Key));
    }

    public void RememberAccount(OAuthApp app, string? account)
    {
        ArgumentNullException.ThrowIfNull(app);
        credentials.Set(CredentialKeys.SignInAccount(app.Key), account);
    }

    /// <summary>
    /// Opens the sign-in page, waits for the service to come back to a listener on this PC alone, and keeps the tokens.
    /// Fails with <see cref="SignInRequiredException"/> saying why; cancelling (or the time limit) throws.
    /// </summary>
    public async Task SignInAsync(OAuthApp app, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(app);
        var verifier = Pkce.NewVerifier();
        var state = Pkce.NewVerifier();
        using var listener = new LoopbackRedirect(app.RedirectHost, app.RedirectPort);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(SignInLimit);

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["response_type"] = "code",
            ["client_id"] = app.ClientId,
            ["redirect_uri"] = listener.RedirectUri,
            ["scope"] = string.Join(' ', app.Scopes),
            ["state"] = state,
            ["code_challenge"] = Pkce.Challenge(verifier),
            ["code_challenge_method"] = "S256",
        };
        foreach (var (key, value) in app.AuthorizeExtras ?? new Dictionary<string, string>())
        {
            parameters[key] = value;
        }

        var query = string.Join('&', parameters.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
        var address = new Uri(app.AuthorizeUrl + (app.AuthorizeUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?") + query);
        LogSigningIn(app.Key, listener.RedirectUri);
        try
        {
            browser.Open(address);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            throw new SignInRequiredException(OutputsStrings.SignInNoBrowser(e.Message), e);
        }

        IReadOnlyDictionary<string, string> answer;
        try
        {
            answer = await listener.WaitAsync(state, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SignInRequiredException(OutputsStrings.SignInTimedOut(app.Name));
        }

        if (answer.TryGetValue("error", out var error))
        {
            throw new SignInRequiredException(OutputsStrings.SignInRefused(app.Name, answer.GetValueOrDefault("error_description") ?? error));
        }

        if (!answer.TryGetValue("code", out var code) || code.Length == 0)
        {
            throw new SignInRequiredException(OutputsStrings.SignInRefused(app.Name, OutputsStrings.SignInNoCode));
        }

        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = listener.RedirectUri,
            ["client_id"] = app.ClientId,
            ["code_verifier"] = verifier,
        };
        if (app.ClientSecret is { } secret)
        {
            form["client_secret"] = secret;
        }

        using var reply = await PostFormAsync(app.TokenUrl, form, ct).ConfigureAwait(false);
        if (!reply.Ok)
        {
            throw new SignInRequiredException(OutputsStrings.SignInRefused(app.Name, reply.Error));
        }

        if (reply.Document?.RootElement.TryGetProperty("refresh_token", out var refresh) != true || refresh.GetString() is not { Length: > 0 } refreshToken)
        {
            throw new SignInRequiredException(OutputsStrings.SignInNoRefreshToken(app.Name));
        }

        credentials.Set(CredentialKeys.SignIn(app.Key), refreshToken);
        credentials.Set(CredentialKeys.SignInAccount(app.Key), null);
        Keep(app, reply.Document.RootElement);
        LogSignedIn(app.Key);
    }

    /// <summary>A token that is good now: the one in memory, or a fresh one. Network trouble throws <see cref="HttpRequestException"/>, which passes; a refused token <see cref="SignInRequiredException"/>, which doesn't.</summary>
    public async Task<string> AccessTokenAsync(OAuthApp app, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (Current(app) is { } token)
        {
            return token;
        }

        SemaphoreSlim gate;
        lock (_gate)
        {
            if (!_refreshes.TryGetValue(app.Key, out gate!))
            {
                gate = new SemaphoreSlim(1, 1);
                _refreshes[app.Key] = gate;
            }
        }

        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Current(app) is { } other)
            {
                return other;
            }

            if (credentials.Get(CredentialKeys.SignIn(app.Key)) is not { } refreshToken)
            {
                throw new SignInRequiredException(OutputsStrings.NotSignedIn(app.Name));
            }

            var form = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = app.ClientId,
            };
            if (app.ClientSecret is { } secret)
            {
                form["client_secret"] = secret;
            }

            using var reply = await PostFormAsync(app.TokenUrl, form, ct).ConfigureAwait(false);
            if (reply.Status is >= 500 or 408 or 429)
            {
                throw new HttpRequestException(reply.Error);
            }

            if (!reply.Ok)
            {
                // invalid_grant: revoked, expired or replaced. Only a new sign-in helps, and it says so.
                LogRefreshRefused(app.Key, reply.Status);
                throw new SignInRequiredException(OutputsStrings.SignInAgain(app.Name));
            }

            if (reply.Document!.RootElement.TryGetProperty("refresh_token", out var rotated) && rotated.GetString() is { Length: > 0 } next && next != refreshToken)
            {
                // Microsoft and others give a new one each time.
                credentials.Set(CredentialKeys.SignIn(app.Key), next);
            }

            return Keep(app, reply.Document.RootElement);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The service refused the token it was given: forget it, so the next one is fresh.</summary>
    public void Invalidate(OAuthApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        lock (_gate)
        {
            _tokens.Remove(app.Key);
        }
    }

    /// <summary>Forgets everything of this sign-in on this PC.</summary>
    public void SignOut(OAuthApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        Invalidate(app);
        credentials.RemoveAll(CredentialKeys.SignInPrefix(app.Key));
        LogSignedOut(app.Key);
    }

    private string? Current(OAuthApp app)
    {
        lock (_gate)
        {
            return _tokens.TryGetValue(app.Key, out var token) && token.Expires - ExpirySlack > _time.GetUtcNow() ? token.Value : null;
        }
    }

    private string Keep(OAuthApp app, JsonElement root)
    {
        var value = root.TryGetProperty("access_token", out var access) ? access.GetString() : null;
        if (string.IsNullOrEmpty(value))
        {
            throw new HttpRequestException(OutputsStrings.SignInNoCode);
        }

        var seconds = root.TryGetProperty("expires_in", out var expires) && expires.ValueKind == JsonValueKind.Number ? expires.GetDouble() : 3600;
        lock (_gate)
        {
            _tokens[app.Key] = new AccessToken(value, _time.GetUtcNow().AddSeconds(seconds));
        }

        return value;
    }

    private async Task<TokenReply> PostFormAsync(string url, Dictionary<string, string> form, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(SharingHttp.CallLimit);
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var response = await http.Client.SendAsync(request, limit.Token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(limit.Token).ConfigureAwait(false);
            JsonDocument? document = null;
            try
            {
                document = JsonDocument.Parse(text);
            }
            catch (JsonException)
            {
                // a proxy's page or the like: only the status counts then
            }

            var error = document?.RootElement.ValueKind == JsonValueKind.Object
                ? (document.RootElement.TryGetProperty("error_description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()
                    : document.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null)
                : null;
            return new TokenReply((int)response.StatusCode, response.IsSuccessStatusCode, document, error ?? $"HTTP {(int)response.StatusCode}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new HttpRequestException(OutputsStrings.NoReply(new Uri(url).Host));
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Signing in to {Service}, listening at {Redirect}")]
    private partial void LogSigningIn(string service, string redirect);

    [LoggerMessage(Level = LogLevel.Information, Message = "Signed in to {Service}")]
    private partial void LogSignedIn(string service);

    [LoggerMessage(Level = LogLevel.Information, Message = "Signed out of {Service}")]
    private partial void LogSignedOut(string service);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Service} refused the refresh token (HTTP {Status}); a new sign-in is needed")]
    private partial void LogRefreshRefused(string service, int status);

    private sealed record AccessToken(string Value, DateTimeOffset Expires);

    private sealed record TokenReply(int Status, bool Ok, JsonDocument? Document, string Error) : IDisposable
    {
        public void Dispose() => Document?.Dispose();
    }
}

/// <summary>Proof Key for Code Exchange (RFC 7636), with S256.</summary>
internal static class Pkce
{
    /// <summary>32 random bytes in URL-safe base64: 43 characters, the shortest the RFC allows and as strong as SHA-256.</summary>
    public static string NewVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Where the service sends the browser back to: a socket on the loopback address alone, no matter which port, closed
/// again after the answer. Requests without the right <c>state</c> (a page poking at local ports) are turned away and it
/// goes on listening.
/// </summary>
[Localizable(false)]
internal sealed class LoopbackRedirect : IDisposable
{
    private readonly TcpListener _listener;

    public LoopbackRedirect(string host, int port)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        RedirectUri = $"http://{host}:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
    }

    public string RedirectUri { get; }

    public void Dispose() => _listener.Stop();

    public async Task<IReadOnlyDictionary<string, string>> WaitAsync(string state, CancellationToken ct)
    {
        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            await using var stream = client.GetStream();
            using var slow = CancellationTokenSource.CreateLinkedTokenSource(ct);
            slow.CancelAfter(TimeSpan.FromSeconds(5));
            string line;
            try
            {
                line = await ReadRequestLineAsync(stream, slow.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException || (e is OperationCanceledException && !ct.IsCancellationRequested))
            {
                continue; // a connection that says nothing, such as a browser's speculative one
            }

            var answer = Parse(line);
            if (answer is null || !answer.TryGetValue("state", out var given) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(state)))
            {
                await ReplyAsync(stream, "404 Not Found", string.Empty, ct).ConfigureAwait(false);
                continue;
            }

            await ReplyAsync(stream, "200 OK", Page(answer.ContainsKey("error")), ct).ConfigureAwait(false);
            return answer;
        }
    }

    private static async Task<string> ReadRequestLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            length += read;
            var end = Array.IndexOf(buffer, (byte)'\n', 0, length);
            if (end >= 0)
            {
                return Encoding.ASCII.GetString(buffer, 0, end).TrimEnd('\r');
            }
        }

        return Encoding.ASCII.GetString(buffer, 0, length);
    }

    /// <summary>The query of "GET /?a=b HTTP/1.1"; null for anything else.</summary>
    private static Dictionary<string, string>? Parse(string line)
    {
        var parts = line.Split(' ');
        if (parts.Length < 2 || parts[0] != "GET")
        {
            return null;
        }

        var mark = parts[1].IndexOf('?', StringComparison.Ordinal);
        if (mark < 0)
        {
            return null;
        }

        var answer = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in parts[1][(mark + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            var key = equals < 0 ? pair : pair[..equals];
            var value = equals < 0 ? string.Empty : pair[(equals + 1)..];
            answer[Uri.UnescapeDataString(key.Replace('+', ' '))] = Uri.UnescapeDataString(value.Replace('+', ' '));
        }

        return answer;
    }

    private static async Task ReplyAsync(NetworkStream stream, string status, string body, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, ct).ConfigureAwait(false);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static string Page(bool failed)
    {
        var text = WebUtility.HtmlEncode(failed ? OutputsStrings.SignInPageFailed : OutputsStrings.SignInPageDone);
        var direction = System.Globalization.CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft ? "rtl" : "ltr";
        return $"<!doctype html><html dir=\"{direction}\"><head><meta charset=\"utf-8\"><title>CashGear Capture</title></head><body style=\"font:16px system-ui;margin:3em\"><p>{text}</p></body></html>";
    }
}
