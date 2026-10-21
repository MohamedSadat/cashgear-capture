using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace CashGear.Capture.Licensing;

/// <summary>What the app sends the activation endpoint: the licence file and this PC's id (a hash, never the real one).</summary>
public sealed record ActivationRequest(
    [property: JsonPropertyName("license")] string License,
    [property: JsonPropertyName("machineId")] string MachineId);

/// <summary>Why the endpoint refused (HTTP 4xx): a code the app turns into words.</summary>
public sealed record ActivationError(
    [property: JsonPropertyName("error")] string Error);

/// <summary>The endpoint's reasons for a refusal.</summary>
public static class ActivationErrors
{
    /// <summary>The licence isn't one the endpoint signed, or is damaged.</summary>
    public const string InvalidLicense = "invalid-license";

    /// <summary>The licence is already active on as many PCs as it allows.</summary>
    public const string MachineLimit = "machine-limit";

    /// <summary>The seller withdrew the licence (refunded, for example).</summary>
    public const string Revoked = "revoked";

    /// <summary>The licence's end date has passed.</summary>
    public const string Expired = "expired";
}

/// <summary>The endpoint refused to activate; <see cref="Error"/> is one of <see cref="ActivationErrors"/>.</summary>
public sealed class ActivationRefusedException(string error) : Exception(error)
{
    public string Error { get; } = error;
}

/// <summary>
/// Activates a licence on this PC, or refreshes its activation: POSTs <see cref="ActivationRequest"/> to
/// <c>&lt;ActivationUrl&gt;/activate</c> and gets back a signed <see cref="ActivationToken"/> (as a
/// <see cref="SignedDocument"/>). A refusal throws <see cref="ActivationRefusedException"/>; an endpoint that can't
/// be reached throws <see cref="HttpRequestException"/> (the grace period covers that).
/// </summary>
public sealed class ActivationClient(HttpClient http)
{
    public async Task<string> ActivateAsync(Uri activationUrl, string licenseText, string machineId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(activationUrl);
        var endpoint = new Uri(activationUrl.AbsoluteUri.TrimEnd('/') + "/activate");
        using var response = await http.PostAsJsonAsync(endpoint, new ActivationRequest(licenseText, machineId), LicensingJson.Default.ActivationRequest, ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }

        if ((int)response.StatusCode is >= 400 and < 500 and not (int)HttpStatusCode.NotFound)
        {
            var error = await response.Content.ReadFromJsonAsync(LicensingJson.Default.ActivationError, ct).ConfigureAwait(false);
            throw new ActivationRefusedException(error?.Error ?? ActivationErrors.InvalidLicense);
        }

        throw new HttpRequestException($"The activation endpoint answered {(int)response.StatusCode}.", null, response.StatusCode);
    }
}
