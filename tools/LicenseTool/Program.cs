using System.Globalization;
using CashGear.Capture.Licensing;
using CashGear.Capture.Licensing.Tool;

// The seller's tool for CashGear Capture licences (docs\LICENSING.md):
//
//   LicenseTool keygen --out <folder>
//       A new Ed25519 key pair: <folder>\license-signing.key (the private key: keep it secret and out of the
//       repository) and the public key, printed, for tools\package.ps1 -LicensePublicKey.
//   LicenseTool issue --key <file> --licensee <name> [--edition pro] [--machines 3] [--expires 2027-12-31]
//                     [--features AutoZoom,Captions] [--activation https://licences.example.com] [--id <id>] --out <file.cglicense>
//       A signed licence file to send to the buyer.
//   LicenseTool verify --public <base64> <file.cglicense>
//       Checks a licence file and prints what it holds.
//   LicenseTool serve --key <file> [--prefix http://127.0.0.1:8642/] [--store activations.json] [--refresh-days 7]
//       A reference activation endpoint (POST /activate).
//   LicenseTool revoke --key <file> --store <activations.json> --id <licence id>
//       Withdraws a licence: its PCs can't refresh their activations.

var options = Options.Parse(args.Skip(1).ToArray());
try
{
    return args.FirstOrDefault() switch
    {
        "keygen" => KeyGen(options),
        "issue" => Issue(options),
        "verify" => Verify(options),
        "serve" => Serve(options),
        "revoke" => Revoke(options),
        _ => Usage(),
    };
}
catch (Exception ex) when (ex is ArgumentException or IOException or FormatException or UnauthorizedAccessException)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

static int Usage()
{
    Console.WriteLine("LicenseTool keygen | issue | verify | serve | revoke (see tools\\LicenseTool\\Program.cs)");
    return 1;
}

static int KeyGen(Options options)
{
    var folder = options.Required("out");
    Directory.CreateDirectory(folder);
    var file = Path.Combine(folder, "license-signing.key");
    if (File.Exists(file))
    {
        throw new IOException($"{file} exists already: a new key would make every licence issued with it unusable.");
    }

    var (privateKey, publicKey) = Signing.NewKeyPair();
    File.WriteAllText(file, privateKey);
    Console.WriteLine($"Private key: {file} (keep it secret; back it up)");
    Console.WriteLine($"Public key:  {publicKey}");
    Console.WriteLine($"Package with: tools\\package.ps1 -LicensePublicKey {publicKey}");
    return 0;
}

static int Issue(Options options)
{
    var privateKey = File.ReadAllText(options.Required("key")).Trim();
    var license = new LicenseInfo(
        Id: options.Get("id") ?? Guid.NewGuid().ToString("N"),
        Product: LicenseInfo.ProductName,
        Licensee: options.Required("licensee"),
        Edition: Enum.Parse<Edition>(options.Get("edition") ?? "Pro", ignoreCase: true),
        Features: options.Get("features") is { } list ? [.. list.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(f => Enum.Parse<ProFeature>(f, ignoreCase: true))] : null,
        MaxMachines: int.Parse(options.Get("machines") ?? "3", CultureInfo.InvariantCulture),
        Issued: DateTimeOffset.UtcNow,
        Expires: options.Get("expires") is { } expires ? DateTimeOffset.Parse(expires, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) : null,
        ActivationUrl: options.Get("activation"));
    var output = options.Required("out");
    File.WriteAllText(output, Signing.Sign(license.ToBytes(), privateKey).ToText());
    Console.WriteLine($"Licence {license.Id} for {license.Licensee} ({license.Edition}, {license.MaxMachines} PCs) written to {output}");
    return 0;
}

static int Verify(Options options)
{
    var evaluator = new LicenseEvaluator(options.Required("public"));
    var (license, state) = evaluator.ReadLicense(File.ReadAllText(options.Positional ?? throw new ArgumentException("Which licence file?")), DateTimeOffset.UtcNow);
    Console.WriteLine(license is null ? $"Not a valid licence ({state})." : $"{state}: {license}");
    return license is not null && state == LicenseState.Active ? 0 : 1;
}

static int Serve(Options options)
{
    var prefix = options.Get("prefix") ?? "http://127.0.0.1:8642/";
    using var server = new ActivationServer(
        prefix,
        File.ReadAllText(options.Required("key")).Trim(),
        options.Get("store") ?? "activations.json",
        TimeSpan.FromDays(double.Parse(options.Get("refresh-days") ?? "7", CultureInfo.InvariantCulture)));
    server.Start();
    Console.WriteLine($"Activating at {prefix}activate; press Enter to stop.");
    Console.ReadLine();
    return 0;
}

static int Revoke(Options options)
{
    using var server = new ActivationServer("http://127.0.0.1:1/", File.ReadAllText(options.Required("key")).Trim(), options.Required("store"), TimeSpan.Zero);
    server.Revoke(options.Required("id"));
    Console.WriteLine($"Licence {options.Required("id")} revoked.");
    return 0;
}

internal sealed class Options
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public string? Positional { get; private set; }

    public static Options Parse(string[] args)
    {
        var options = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length)
            {
                options._values[args[i][2..]] = args[++i];
            }
            else
            {
                options.Positional = args[i];
            }
        }

        return options;
    }

    public string? Get(string name) => _values.TryGetValue(name, out var value) ? value : null;

    public string Required(string name) => Get(name) ?? throw new ArgumentException($"--{name} is needed.");
}
