using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Outputs.Credentials;

namespace CashGear.Capture.Outputs.Tests;

/// <summary>A temporary folder per test, deleted afterwards.</summary>
public sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cashgear-tests", "outputs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public AppPaths AppPaths => new(System.IO.Path.Combine(Path, "settings"), System.IO.Path.Combine(Path, "data", "logs"), System.IO.Path.Combine(Path, "captures"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // a server still had a file open; the temp folder is cleaned eventually
        }
    }
}

internal sealed class FakeSettings(AppSettings settings) : ISettingsService
{
    public AppSettings Current { get; set; } = settings.Normalize();

    public event EventHandler? Changed;

    public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task UpdateAsync(Func<AppSettings, AppSettings> update, CancellationToken ct = default)
    {
        Current = update(Current).Normalize();
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }
}

/// <summary>Secrets in memory.</summary>
internal sealed class MemoryCredentials : ICredentialStore
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? Get(string key) => Values.GetValueOrDefault(key);

    public void Set(string key, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            Values.Remove(key);
        }
        else
        {
            Values[key] = value;
        }
    }

    public void RemoveAll(string prefix)
    {
        foreach (var key in Values.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            Values.Remove(key);
        }
    }
}

/// <summary>Only the text clipboard is used here (Copy the link).</summary>
internal sealed class TextClipboard : IOutputService
{
    public List<string> Copied { get; } = [];

    public Task CopyTextToClipboardAsync(string text, CancellationToken ct = default)
    {
        Copied.Add(text);
        return Task.CompletedTask;
    }

    public Task<string?> SaveAsync(CapturedImage image, SaveOptions options, CancellationToken ct = default) => throw new NotSupportedException();

    public Task SaveToFileAsync(CapturedImage image, string path, ImageFileFormat format, int jpegQuality, CancellationToken ct = default) => throw new NotSupportedException();

    public Task CopyToClipboardAsync(CapturedImage image, CancellationToken ct = default) => throw new NotSupportedException();

    public Task CopyFilesToClipboardAsync(IReadOnlyList<string> paths, CancellationToken ct = default) => throw new NotSupportedException();
}

internal static class TestCaptures
{
    /// <summary>A capture of one colour, at a monitor scale.</summary>
    public static CapturedImage Image(int width, int height, double scale = 1, string? title = "Untitled - Notepad")
    {
        var image = CapturedImage.Allocate(width, height, new CaptureMetadata
        {
            Kind = CaptureKind.Region,
            SourceBounds = new PixelRect(0, 0, width, height),
            Dpi = new DpiScale(scale, scale),
            CapturedAt = new DateTimeOffset(2026, 10, 21, 9, 30, 0, TimeSpan.Zero),
            WindowTitle = title,
        });
        image.Pixels.Fill(0x80);
        return image;
    }

    /// <summary>A capture as an item whose file is a real PNG of its size, written to <paramref name="folder"/> when asked for.</summary>
    public static OutputItem Item(CapturedImage image, string folder, string name = "Capture_2026-10-21_09-30-00_region") =>
        OutputItem.ForImage(image, name, _ =>
        {
            var sub = Path.Combine(folder, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sub);
            var path = Path.Combine(sub, name + ".png");
            File.WriteAllBytes(path, Png.Solid(image.Width, image.Height));
            return Task.FromResult(path);
        });
}

/// <summary>A minimal PNG writer (one colour, no WPF needed).</summary>
internal static class Png
{
    private static readonly uint[] CrcTable = [.. Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++)
        {
            c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        }

        return c;
    })];

    public static byte[] Solid(int width, int height, byte gray = 0x80)
    {
        using var raw = new MemoryStream();
        using (var zlib = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[1 + width];
            Array.Fill(row, gray, 1, width);
            for (var y = 0; y < height; y++)
            {
                zlib.Write(row);
            }
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bit depth
        header[9] = 0; // grayscale
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", raw.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        var typed = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        stream.Write(typed);
        var crc = 0xFFFFFFFFu;
        foreach (var b in typed)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        var tail = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(tail, crc ^ 0xFFFFFFFFu);
        stream.Write(tail);
    }
}

internal static class Network
{
    /// <summary>A TCP port nobody listens on now (for servers that can't report the port they got).</summary>
    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>A self-signed certificate for 127.0.0.1 and localhost, with its private key usable by SChannel.</summary>
    public static X509Certificate2 SelfSignedCertificate(string name = "localhost")
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        // An ephemeral key can't be used by SChannel on Windows: go through PFX.
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
    }
}
