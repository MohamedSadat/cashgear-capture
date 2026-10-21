using System.Text.Json;
using System.Text.Json.Serialization;

namespace CashGear.Capture.Licensing;

/// <summary>Where this PC's licence stands.</summary>
public enum LicenseState
{
    /// <summary>No licence: the free edition.</summary>
    None,

    /// <summary>Licensed and activated on this PC, checked with the endpoint recently.</summary>
    Active,

    /// <summary>Licensed, but the endpoint hasn't confirmed it lately: still working until the grace period ends.</summary>
    Grace,

    /// <summary>The endpoint couldn't be reached for longer than the grace period: back to free until it can.</summary>
    GraceOver,

    /// <summary>A licence that isn't activated on this PC yet.</summary>
    NotActivated,

    /// <summary>The licence or its activation is damaged, forged, of another product or another PC.</summary>
    Invalid,

    /// <summary>The licence's end date has passed.</summary>
    Expired,
}

/// <summary>This PC's licence as the app sees it.</summary>
/// <param name="Edition">What is unlocked now.</param>
/// <param name="License">The licence, when there is a readable one.</param>
/// <param name="State">Why the edition is what it is.</param>
/// <param name="GraceUntil">In <see cref="LicenseState.Grace"/>: when the licence stops working without a check.</param>
public sealed record LicenseStatus(Edition Edition, LicenseInfo? License, LicenseState State, DateTimeOffset? GraceUntil = null)
{
    public static LicenseStatus Free { get; } = new(Edition.Free, null, LicenseState.None);
}

/// <summary>What the app keeps of its licence (license.json in its data folder).</summary>
/// <param name="License">The licence file's text.</param>
/// <param name="Token">The activation token's text, once activated.</param>
/// <param name="LastChecked">When the endpoint last confirmed the activation.</param>
public sealed record StoredLicense(
    [property: JsonPropertyName("license")] string License,
    [property: JsonPropertyName("token")] string? Token,
    [property: JsonPropertyName("lastChecked")] DateTimeOffset? LastChecked)
{
    public string ToText() => JsonSerializer.Serialize(this, LicensingJson.Default.StoredLicense);

    public static StoredLicense? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(text, LicensingJson.Default.StoredLicense);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Decides the edition from the licence, its activation token and the time, offline:
/// <list type="bullet">
/// <item>the licence must be signed by the seller's key, for this product, and not expired;</item>
/// <item>the token must be signed by the same key, for this licence and this PC;</item>
/// <item>until the token's refresh date the licence is <see cref="LicenseState.Active"/>; after it, the app checks
/// with the endpoint, and while it can't, the licence keeps working for <see cref="GracePeriod"/> after the last good
/// check (<see cref="LicenseState.Grace"/>), then the free edition applies until a check succeeds.</item>
/// </list>
/// </summary>
public sealed class LicenseEvaluator(string publicKey)
{
    /// <summary>How long a licence keeps working when the activation endpoint can't be reached.</summary>
    public static readonly TimeSpan GracePeriod = TimeSpan.FromDays(14);

    /// <summary>The licence in a licence file's text, when it is genuine (else null and why).</summary>
    public (LicenseInfo? License, LicenseState State) ReadLicense(string? text, DateTimeOffset now)
    {
        var document = SignedDocument.TryParse(text);
        if (document is null || !Signing.Verify(document, publicKey))
        {
            return (null, LicenseState.Invalid);
        }

        LicenseInfo? license;
        try
        {
            license = JsonSerializer.Deserialize(document.Content()!, LicensingJson.Default.LicenseInfo);
        }
        catch (JsonException)
        {
            return (null, LicenseState.Invalid);
        }

        if (license is null || !string.Equals(license.Product, LicenseInfo.ProductName, StringComparison.Ordinal) || license.MaxMachines < 1)
        {
            return (null, LicenseState.Invalid);
        }

        return license.Expires is { } expires && now >= expires ? (license, LicenseState.Expired) : (license, LicenseState.Active);
    }

    /// <summary>The token in <paramref name="text"/>, when it is genuine and for <paramref name="license"/> on <paramref name="machineId"/>.</summary>
    public ActivationToken? ReadToken(string? text, LicenseInfo license, string machineId)
    {
        ArgumentNullException.ThrowIfNull(license);
        var document = SignedDocument.TryParse(text);
        if (document is null || !Signing.Verify(document, publicKey))
        {
            return null;
        }

        try
        {
            var token = JsonSerializer.Deserialize(document.Content()!, LicensingJson.Default.ActivationToken);
            return token is not null
                && string.Equals(token.LicenseId, license.Id, StringComparison.Ordinal)
                && string.Equals(token.MachineId, machineId, StringComparison.Ordinal)
                ? token
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The edition for what is stored, on <paramref name="machineId"/>, at <paramref name="now"/>.</summary>
    public LicenseStatus Evaluate(StoredLicense? stored, string machineId, DateTimeOffset now)
    {
        if (stored is null)
        {
            return LicenseStatus.Free;
        }

        var (license, state) = ReadLicense(stored.License, now);
        if (license is null || state != LicenseState.Active)
        {
            return new LicenseStatus(Edition.Free, license, state);
        }

        if (stored.Token is null)
        {
            return new LicenseStatus(Edition.Free, license, LicenseState.NotActivated);
        }

        if (ReadToken(stored.Token, license, machineId) is not { } token)
        {
            return new LicenseStatus(Edition.Free, license, LicenseState.Invalid);
        }

        if (now < token.RefreshAfter)
        {
            return new LicenseStatus(license.Edition, license, LicenseState.Active);
        }

        var lastGood = Max(stored.LastChecked ?? token.Issued, token.Issued);
        var graceUntil = Max(lastGood, token.RefreshAfter) + GracePeriod;
        return now < graceUntil
            ? new LicenseStatus(license.Edition, license, LicenseState.Grace, graceUntil)
            : new LicenseStatus(Edition.Free, license, LicenseState.GraceOver, graceUntil);
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}

/// <summary>Which features need Pro.</summary>
public sealed class FeaturePolicy
{
    private FeaturePolicy(bool allFree) => IsAllFree = allFree;

    /// <summary>This version: every feature works without a licence.</summary>
    public static FeaturePolicy AllFree { get; } = new(allFree: true);

    /// <summary>Free and Pro: the <see cref="ProFeature"/>s need a Pro licence (for when the app is sold).</summary>
    public static FeaturePolicy ProSplit { get; } = new(allFree: false);

    public bool IsAllFree { get; }

    public bool IsEnabled(ProFeature feature, LicenseStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return IsAllFree
            || status.Edition == Edition.Pro && (status.License?.Features is not { } features || features.Contains(feature));
    }
}
