using System.Text.Json;
using System.Text.Json.Serialization;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace CashGear.Capture.Licensing;

/// <summary>
/// A signed document as it is stored and sent: its content (JSON, base64-encoded so the exact bytes that were signed
/// are kept) and an Ed25519 signature of those bytes. Licence files (<c>.cglicense</c>) and activation tokens are both.
/// </summary>
/// <param name="Payload">The content's UTF-8 JSON, base64.</param>
/// <param name="Signature">The Ed25519 signature of the content's bytes, base64.</param>
public sealed record SignedDocument(
    [property: JsonPropertyName("payload")] string Payload,
    [property: JsonPropertyName("signature")] string Signature)
{
    /// <summary>The document as a file's text.</summary>
    public string ToText() => JsonSerializer.Serialize(this, LicensingJson.Default.SignedDocument);

    /// <summary>Reads a document's text; null when it isn't one.</summary>
    public static SignedDocument? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            var document = JsonSerializer.Deserialize(text, LicensingJson.Default.SignedDocument);
            return document is { Payload.Length: > 0, Signature.Length: > 0 } ? document : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The content's bytes, or null when the payload isn't base64.</summary>
    public byte[]? Content()
    {
        try
        {
            return Convert.FromBase64String(Payload);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>Ed25519 keys and signatures (BouncyCastle). Keys are base64 of their 32 raw bytes.</summary>
public static class Signing
{
    /// <summary>A new key pair: the private key signs licences and tokens (keep it secret, out of the repository); the public key goes into the app.</summary>
    public static (string PrivateKey, string PublicKey) NewKeyPair()
    {
        var generator = new Ed25519KeyPairGenerator();
        generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var pair = generator.GenerateKeyPair();
        return (
            Convert.ToBase64String(((Ed25519PrivateKeyParameters)pair.Private).GetEncoded()),
            Convert.ToBase64String(((Ed25519PublicKeyParameters)pair.Public).GetEncoded()));
    }

    /// <summary>The public key of a private key.</summary>
    public static string PublicKeyOf(string privateKey) =>
        Convert.ToBase64String(new Ed25519PrivateKeyParameters(Convert.FromBase64String(privateKey)).GeneratePublicKey().GetEncoded());

    /// <summary>Signs <paramref name="content"/> with <paramref name="privateKey"/>.</summary>
    public static SignedDocument Sign(byte[] content, string privateKey)
    {
        ArgumentNullException.ThrowIfNull(content);
        var signer = new Ed25519Signer();
        signer.Init(forSigning: true, new Ed25519PrivateKeyParameters(Convert.FromBase64String(privateKey)));
        signer.BlockUpdate(content, 0, content.Length);
        return new SignedDocument(Convert.ToBase64String(content), Convert.ToBase64String(signer.GenerateSignature()));
    }

    /// <summary>Whether <paramref name="document"/> was signed by <paramref name="publicKey"/>'s private key and not changed since.</summary>
    public static bool Verify(SignedDocument document, string publicKey)
    {
        ArgumentNullException.ThrowIfNull(document);
        try
        {
            var content = document.Content();
            var signature = Convert.FromBase64String(document.Signature);
            if (content is null || signature.Length != Ed25519.SignatureSize)
            {
                return false;
            }

            var verifier = new Ed25519Signer();
            verifier.Init(forSigning: false, new Ed25519PublicKeyParameters(Convert.FromBase64String(publicKey)));
            verifier.BlockUpdate(content, 0, content.Length);
            return verifier.VerifySignature(signature);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return false;
        }
    }

    private static class Ed25519
    {
        public const int SignatureSize = 64;
    }
}
