using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.Outputs.Tests.Http;

/// <summary>What the server received.</summary>
internal sealed record ReceivedUpload(
    string Method,
    string Path,
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyDictionary<string, string> Fields,
    string? FileField,
    string? FileName,
    string? FileType,
    byte[] FileBytes);

/// <summary>An HTTP server on 127.0.0.1 (Kestrel) that keeps each multipart upload and answers as the test says.</summary>
internal sealed class TestHttpServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TestHttpServer(WebApplication app, string address)
    {
        _app = app;
        Address = address.TrimEnd('/');
    }

    /// <summary>http://127.0.0.1:port</summary>
    public string Address { get; }

    public List<ReceivedUpload> Received { get; } = [];

    /// <summary>The status code and body of the reply (JSON with a link by default).</summary>
    public Func<ReceivedUpload, (int Status, string ContentType, string Body)> Reply { get; set; } =
        upload => (200, "application/json", $$"""{ "data": { "link": "https://img.example.test/{{upload.FileName}}" } }""");

    public static async Task<TestHttpServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        TestHttpServer? server = null;
        app.Run(async context => await server!.HandleAsync(context));
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        server = new TestHttpServer(app, address);
        return server;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private async Task HandleAsync(HttpContext context)
    {
        var request = context.Request;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        string? field = null, name = null, type = null;
        var bytes = Array.Empty<byte>();
        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync();
            foreach (var (key, value) in form)
            {
                fields[key] = value.ToString();
            }

            if (form.Files.FirstOrDefault() is { } file)
            {
                field = file.Name;
                name = file.FileName;
                type = file.ContentType;
                using var copy = new MemoryStream();
                await file.CopyToAsync(copy);
                bytes = copy.ToArray();
            }
        }

        var upload = new ReceivedUpload(
            request.Method,
            request.Path,
            request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase),
            fields,
            field,
            name,
            type,
            bytes);
        lock (Received)
        {
            Received.Add(upload);
        }

        var (status, contentType, body) = Reply(upload);
        context.Response.StatusCode = status;
        context.Response.ContentType = contentType;
        await context.Response.WriteAsync(body);
    }
}
