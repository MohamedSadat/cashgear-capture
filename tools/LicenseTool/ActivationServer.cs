using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CashGear.Capture.Licensing.Tool;

/// <summary>
/// A reference activation endpoint: <c>POST /activate</c> with an <see cref="ActivationRequest"/> checks the licence
/// (signed with this key, this product, not expired, not revoked) and the number of PCs it is active on, records the
/// PC and answers a signed <see cref="ActivationToken"/> that asks to be refreshed after <see cref="RefreshAfter"/>. A
/// PC already on the list is refreshed. The activations are kept in a JSON file. Enough to run a small sale, or to
/// show what a hosted endpoint must do.
/// </summary>
internal sealed class ActivationServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _privateKey;
    private readonly LicenseEvaluator _evaluator;
    private readonly string _store;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private Task? _serving;

    public ActivationServer(string prefix, string privateKey, string store, TimeSpan refreshAfter, TimeProvider? time = null)
    {
        _privateKey = privateKey;
        _evaluator = new LicenseEvaluator(Signing.PublicKeyOf(privateKey));
        _store = store;
        RefreshAfter = refreshAfter;
        _time = time ?? TimeProvider.System;
        _listener.Prefixes.Add(prefix.EndsWith('/') ? prefix : prefix + "/");
    }

    public TimeSpan RefreshAfter { get; }

    /// <summary>Requests answered, for tests and the console.</summary>
    public int Requests { get; private set; }

    public void Start()
    {
        _listener.Start();
        _serving = Task.Run(ServeAsync);
    }

    /// <summary>Withdraws a licence: its PCs can't refresh their activations any more.</summary>
    public void Revoke(string licenseId)
    {
        lock (_gate)
        {
            var store = Load();
            Entry(store, licenseId)["revoked"] = true;
            Save(store);
        }
    }

    /// <summary>The PCs a licence is active on.</summary>
    public IReadOnlyList<string> Machines(string licenseId)
    {
        lock (_gate)
        {
            return [.. ((Load()["licenses"]?[licenseId]?["machines"] as JsonArray) ?? []).Select(m => m!.GetValue<string>())];
        }
    }

    public void Dispose()
    {
        _listener.Close();
        _serving?.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>The answer to one activation request: the status code and the body.</summary>
    internal (HttpStatusCode Status, string Body) Handle(string body)
    {
        ActivationRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(body, LicensingJson.Default.ActivationRequest);
        }
        catch (JsonException)
        {
            request = null;
        }

        var now = _time.GetUtcNow();
        if (request is null || string.IsNullOrWhiteSpace(request.MachineId))
        {
            return Refuse(ActivationErrors.InvalidLicense);
        }

        var (license, state) = _evaluator.ReadLicense(request.License, now);
        if (license is null)
        {
            return Refuse(ActivationErrors.InvalidLicense);
        }

        if (state == LicenseState.Expired)
        {
            return Refuse(ActivationErrors.Expired);
        }

        lock (_gate)
        {
            var store = Load();
            var entry = Entry(store, license.Id);
            if (entry["revoked"]?.GetValue<bool>() == true)
            {
                return Refuse(ActivationErrors.Revoked);
            }

            var machines = (JsonArray)entry["machines"]!;
            if (!machines.Any(m => m!.GetValue<string>() == request.MachineId))
            {
                if (machines.Count >= license.MaxMachines)
                {
                    return Refuse(ActivationErrors.MachineLimit);
                }

                machines.Add(request.MachineId);
                Save(store);
            }
        }

        var token = new ActivationToken(license.Id, request.MachineId, now, now + RefreshAfter);
        return (HttpStatusCode.OK, Signing.Sign(token.ToBytes(), _privateKey).ToText());
    }

    private static (HttpStatusCode, string) Refuse(string error) =>
        (HttpStatusCode.Forbidden, JsonSerializer.Serialize(new ActivationError(error), LicensingJson.Default.ActivationError));

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException)
            {
                return;
            }

            try
            {
                (HttpStatusCode Status, string Body) answer;
                if (context.Request.HttpMethod == "POST" && context.Request.Url?.AbsolutePath.TrimEnd('/').EndsWith("/activate", StringComparison.OrdinalIgnoreCase) == true)
                {
                    using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                    answer = Handle(await reader.ReadToEndAsync());
                }
                else
                {
                    answer = (HttpStatusCode.NotFound, "{}");
                }

                Requests++;
                var bytes = Encoding.UTF8.GetBytes(answer.Body);
                context.Response.StatusCode = (int)answer.Status;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
            catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException)
            {
                // the client went away
            }
        }
    }

    private JsonObject Load() =>
        File.Exists(_store) && JsonNode.Parse(File.ReadAllText(_store)) is JsonObject root ? root : new JsonObject { ["licenses"] = new JsonObject() };

    private void Save(JsonObject store)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_store))!);
        File.WriteAllText(_store, store.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static JsonObject Entry(JsonObject store, string licenseId)
    {
        var licenses = store["licenses"] as JsonObject ?? (JsonObject)(store["licenses"] = new JsonObject());
        if (licenses[licenseId] is not JsonObject entry)
        {
            entry = new JsonObject { ["machines"] = new JsonArray(), ["revoked"] = false };
            licenses[licenseId] = entry;
        }

        return entry;
    }
}
