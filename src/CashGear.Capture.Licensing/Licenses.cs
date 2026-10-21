using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace CashGear.Capture.Licensing;

/// <summary>What a licence unlocks.</summary>
public enum Edition
{
    Free,
    Pro,
}

/// <summary>The features a Pro licence adds (in this version every one is free: <see cref="FeaturePolicy.AllFree"/>).</summary>
public enum ProFeature
{
    /// <summary>Video editor: text, arrows, shapes, blur and pixelate (milestone 3).</summary>
    VideoEditorAnnotations,

    /// <summary>Video editor: speed, ramps, crossfades and freeze frames (milestone 4).</summary>
    VideoEditorSpeed,

    /// <summary>Video editor: clicks, keystrokes and pointer effects from the metadata (milestone 5).</summary>
    VideoEditorEffects,

    /// <summary>Zooms, by hand and automatic.</summary>
    AutoZoom,

    /// <summary>Captions: typed, imported, and from speech.</summary>
    Captions,

    /// <summary>Animated GIFs from the video editor and after a recording.</summary>
    GifExport,
}

/// <summary>A licence's content (signed into a <c>.cglicense</c> file).</summary>
/// <param name="Id">The licence's id, unique per sale.</param>
/// <param name="Product">Always <see cref="ProductName"/>: a licence of another product is refused.</param>
/// <param name="Licensee">Who it was sold to.</param>
/// <param name="Edition">What it unlocks.</param>
/// <param name="Features">The features it unlocks; null for all of its edition's.</param>
/// <param name="MaxMachines">On how many PCs it can be activated.</param>
/// <param name="Issued">When it was made.</param>
/// <param name="Expires">When it stops working; null for never (a subscription has a date).</param>
/// <param name="ActivationUrl">Where it is activated (and refreshed); part of what is signed, so it can't be redirected.</param>
public sealed record LicenseInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("product")] string Product,
    [property: JsonPropertyName("licensee")] string Licensee,
    [property: JsonPropertyName("edition")] Edition Edition,
    [property: JsonPropertyName("features")] IReadOnlyList<ProFeature>? Features,
    [property: JsonPropertyName("maxMachines")] int MaxMachines,
    [property: JsonPropertyName("issued")] DateTimeOffset Issued,
    [property: JsonPropertyName("expires")] DateTimeOffset? Expires,
    [property: JsonPropertyName("activationUrl")] string? ActivationUrl)
{
    [Localizable(false)] // a value of the signed licence file, compared as it is
    public const string ProductName = "CashGear Capture";

    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, LicensingJson.Default.LicenseInfo);
}

/// <summary>What the activation endpoint signs for a PC: this licence may be used on this machine until it is refreshed.</summary>
/// <param name="LicenseId">The licence activated.</param>
/// <param name="MachineId">The PC (<see cref="MachineId"/>).</param>
/// <param name="Issued">When the endpoint signed it.</param>
/// <param name="RefreshAfter">When the app should check with the endpoint again; it keeps working for <see cref="LicenseEvaluator.GracePeriod"/> after the last good check if it can't.</param>
public sealed record ActivationToken(
    [property: JsonPropertyName("licenseId")] string LicenseId,
    [property: JsonPropertyName("machineId")] string MachineId,
    [property: JsonPropertyName("issued")] DateTimeOffset Issued,
    [property: JsonPropertyName("refreshAfter")] DateTimeOffset RefreshAfter)
{
    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, LicensingJson.Default.ActivationToken);
}

/// <summary>A PC's identity for activations: a hash of Windows' machine id, so the real one is never sent.</summary>
public static class MachineId
{
    /// <summary>This PC's id, or a hash of its name where Windows has no machine id.</summary>
    public static string ForThisPc()
    {
        var guid = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography", "MachineGuid", null) as string;
        return From(guid ?? Environment.MachineName);
    }

    /// <summary>The id for a machine GUID: SHA-256 with the product's salt, as hex.</summary>
    [Localizable(false)] // the hash's salt
    public static string From(string machineGuid) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("CashGear Capture machine|" + machineGuid.Trim().ToUpperInvariant())));
}

[JsonSourceGenerationOptions(UseStringEnumConverter = true, PropertyNameCaseInsensitive = true, WriteIndented = false)]
[JsonSerializable(typeof(SignedDocument))]
[JsonSerializable(typeof(LicenseInfo))]
[JsonSerializable(typeof(ActivationToken))]
[JsonSerializable(typeof(StoredLicense))]
[JsonSerializable(typeof(ActivationRequest))]
[JsonSerializable(typeof(ActivationError))]
internal sealed partial class LicensingJson : JsonSerializerContext;
