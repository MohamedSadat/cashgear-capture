using System.Net;
using System.Net.Http.Headers;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Outputs.Credentials;
using CashGear.Capture.Outputs.Resources;

namespace CashGear.Capture.Outputs.Http;

/// <summary>The app's one HTTP client (connections are pooled and refreshed every few minutes).</summary>
public sealed class UploadHttpClient : IDisposable
{
    public UploadHttpClient()
        : this(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), AutomaticDecompression = DecompressionMethods.All })
    {
    }

    public UploadHttpClient(HttpMessageHandler handler)
    {
        Client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) };
        Client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CashGear-Capture", typeof(UploadHttpClient).Assembly.GetName().Version?.ToString(3) ?? "1"));
    }

    public HttpClient Client { get; }

    public void Dispose() => Client.Dispose();
}

/// <summary>
/// Posts the capture (or recording) as multipart/form-data to a configured endpoint — a self-hosted API or an image
/// host — and takes the link from the reply.
/// </summary>
public sealed class HttpUploadDestination(HttpDestination upload, ICredentialStore credentials, UploadHttpClient http) : IOutputDestination
{
    /// <summary>The largest reply read (a link is short; a web page in reply is an error).</summary>
    private const int MaxReply = 1 << 20;

    public string Id => DestinationIds.Upload(upload.Id);

    public string DisplayName => upload.Name.Length > 0 ? upload.Name : HostOf(upload.Url);

    public bool SupportsVideo => true;

    public async Task<OutputResult> SendAsync(OutputItem item, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        var path = await item.GetFileAsync(ct).ConfigureAwait(false);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var result = await PostAsync(file, Path.GetFileName(path), ct).ConfigureAwait(false);
        return result.Succeeded ? result with { Message = OutputsStrings.UploadedTo(DisplayName, result.Link) } : result;
    }

    /// <summary>Uploads a 1 × 1 PNG called <c>cashgear-test.png</c> and shows the link the reply gives.</summary>
    public async Task<OutputResult> TestAsync(CancellationToken ct)
    {
        using var probe = new MemoryStream(TestImage.Png);
        var result = await PostAsync(probe, "cashgear-test.png", ct).ConfigureAwait(false);
        return result.Succeeded ? result with { Message = OutputsStrings.TestPictureUploaded(result.Link) } : result;
    }

    private async Task<OutputResult> PostAsync(Stream content, string fileName, CancellationToken ct)
    {
        if (!Uri.TryCreate(upload.Url, UriKind.Absolute, out var url) || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
        {
            return OutputResult.Failed(OutputsStrings.AddressNotHttp);
        }

        using var form = new MultipartFormDataContent();
        foreach (var field in upload.Fields ?? [])
        {
            form.Add(new StringContent(field.Value), field.Name);
        }

        var file = new StreamContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(MediaType(fileName));
        form.Add(file, upload.FileField, fileName);

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        foreach (var header in upload.Headers ?? [])
        {
            if (!request.Headers.TryAddWithoutValidation(header.Name, header.Value))
            {
                return OutputResult.Failed(OutputsStrings.BadHeader(header.Name));
            }
        }

        if (upload.AuthHeader is { } authHeader)
        {
            if (credentials.Get(CredentialKeys.Token(upload)) is not { } token)
            {
                return OutputResult.Failed(OutputsStrings.NoHeaderValue(authHeader));
            }

            if (!request.Headers.TryAddWithoutValidation(authHeader, token))
            {
                return OutputResult.Failed(OutputsStrings.BadHeader(authHeader));
            }
        }

        HttpResponseMessage response;
        try
        {
            response = await http.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return OutputResult.Failed(OutputsStrings.CouldNotReach(url.Host, ex.Message));
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return OutputResult.Failed(OutputsStrings.NoAnswer((long)Math.Round(http.Client.Timeout.TotalMinutes, MidpointRounding.AwayFromZero), url.Host));
        }

        using (response)
        {
            var reply = await ReadAsync(response, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return OutputResult.Failed(OutputsStrings.ServerAnswered(url.Host, (int)response.StatusCode, response.ReasonPhrase, LinkPath.Start(reply)));
            }

            string link;
            try
            {
                link = LinkPath.Extract(reply, upload.LinkPath);
            }
            catch (FormatException ex)
            {
                return OutputResult.Failed(OutputsStrings.LinkNotInReply(ex.Message));
            }

            return Uri.TryCreate(link, UriKind.Absolute, out var linkUri) && (linkUri.Scheme == Uri.UriSchemeHttps || linkUri.Scheme == Uri.UriSchemeHttp)
                ? new OutputResult(true, OutputsStrings.Uploaded) { Link = linkUri.ToString() }
                : OutputResult.Failed(upload.LinkPath is null
                    ? OutputsStrings.ReplyNotLink(LinkPath.Start(reply))
                    : OutputsStrings.NotWebAddress(upload.LinkPath, LinkPath.Start(link)));
        }
    }

    private static async Task<string> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[MaxReply];
        var length = 0;
        int read;
        while (length < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(length), ct).ConfigureAwait(false)) > 0)
        {
            length += read;
        }

        var charset = response.Content.Headers.ContentType?.CharSet;
        var encoding = System.Text.Encoding.UTF8;
        try
        {
            encoding = charset is null ? encoding : System.Text.Encoding.GetEncoding(charset.Trim('"'));
        }
        catch (ArgumentException)
        {
            // an unknown charset: UTF-8
        }

        return encoding.GetString(buffer, 0, length);
    }

    internal static string MediaType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".bmp" => "image/bmp",
        ".gif" => "image/gif",
        ".mp4" => "video/mp4",
        _ => "application/octet-stream",
    };

    private static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : OutputsStrings.HttpUpload;
}

/// <summary>A 1 × 1 transparent PNG, for tests of uploads.</summary>
internal static class TestImage
{
    public static byte[] Png { get; } = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
}
