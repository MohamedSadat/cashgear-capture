using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using CashGear.Capture.Core.Output;

namespace CashGear.Capture.Core.Settings;

/// <summary>
/// The ids of the places a capture can go (see <see cref="DestinationSettings"/>). Built-in ones are fixed words;
/// configured ones are a prefix and the configured entry's <c>Id</c>, e.g. <c>server:office</c>.
/// </summary>
public static class DestinationIds
{
    public const string Editor = "editor";
    public const string File = "file";
    public const string Clipboard = "clipboard";
    public const string Printer = "printer";
    public const string Word = "word";
    public const string PowerPoint = "powerpoint";
    public const string Pdf = "pdf";
    public const string CopyLink = "copy-link";

    public const string ServerPrefix = "server:";
    public const string UploadPrefix = "upload:";
    public const string AppPrefix = "app:";

    public static string Server(string id) => ServerPrefix + id;

    public static string Upload(string id) => UploadPrefix + id;

    public static string App(string id) => AppPrefix + id;

    /// <summary>Equal ids (they are compared without regard to case).</summary>
    public static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Where captures and recordings go after they are taken (the after-capture <b>workflow</b>), and how each place
/// is set up. Passwords, passphrases and tokens are not here: they are kept encrypted with DPAPI apart from the
/// settings file.
/// </summary>
/// <param name="Workflow">The steps for a capture, in order. Steps after the editor wait for its Continue button.</param>
/// <param name="RecordingWorkflow">The steps for a recording, after it is saved to the capture folder.</param>
/// <param name="Print">The printer.</param>
/// <param name="Word">Word documents.</param>
/// <param name="PowerPoint">PowerPoint slides.</param>
/// <param name="Servers">FTP, FTPS and SFTP servers.</param>
/// <param name="Uploads">HTTP upload endpoints.</param>
/// <param name="Apps">Programs a capture can be opened with.</param>
/// <param name="Pdf">PDF files.</param>
public sealed record DestinationSettings(
    IReadOnlyList<WorkflowStep>? Workflow = null,
    IReadOnlyList<WorkflowStep>? RecordingWorkflow = null,
    PrintSettings? Print = null,
    OfficeSettings? Word = null,
    OfficeSettings? PowerPoint = null,
    IReadOnlyList<ServerDestination>? Servers = null,
    IReadOnlyList<HttpDestination>? Uploads = null,
    IReadOnlyList<ExternalApp>? Apps = null,
    PdfSettings? Pdf = null)
{
    /// <summary>What a new install does: open the editor. Save and Copy are there to be switched on (as before the workflow).</summary>
    public static IReadOnlyList<WorkflowStep> DefaultWorkflow { get; } =
    [
        new(DestinationIds.File, Enabled: false),
        new(DestinationIds.Clipboard, Enabled: false),
        new(DestinationIds.Editor),
    ];

    public IReadOnlyList<WorkflowStep>? Workflow { get; init; } = Workflow ?? DefaultWorkflow;

    public IReadOnlyList<WorkflowStep>? RecordingWorkflow { get; init; } = RecordingWorkflow ?? [];

    public PrintSettings? Print { get; init; } = Print ?? new PrintSettings();

    public OfficeSettings? Word { get; init; } = Word ?? new OfficeSettings();

    public OfficeSettings? PowerPoint { get; init; } = PowerPoint ?? new OfficeSettings();

    public IReadOnlyList<ServerDestination>? Servers { get; init; } = Servers ?? [];

    public IReadOnlyList<HttpDestination>? Uploads { get; init; } = Uploads ?? [];

    public IReadOnlyList<ExternalApp>? Apps { get; init; } = Apps ?? [];

    public PdfSettings? Pdf { get; init; } = Pdf ?? new PdfSettings();

    /// <summary>Whether a capture would go anywhere at all.</summary>
    [JsonIgnore]
    public bool DoesAnything => (Workflow ?? []).Any(s => s.Enabled);

    /// <summary>Whether the capture workflow has this step switched on.</summary>
    public bool IsEnabled(string destination) => (Workflow ?? []).Any(s => s.Enabled && DestinationIds.Same(s.Destination, destination));

    /// <summary>
    /// A copy with the capture workflow's <paramref name="destination"/> step switched on or off (added at the end
    /// when it isn't in the list; the editor goes last, so the steps before it don't wait).
    /// </summary>
    public DestinationSettings WithStep(string destination, bool enabled)
    {
        var steps = (Workflow ?? []).ToList();
        var index = steps.FindIndex(s => DestinationIds.Same(s.Destination, destination));
        if (index >= 0)
        {
            steps[index] = steps[index] with { Enabled = enabled };
        }
        else if (enabled)
        {
            var editor = steps.FindIndex(s => DestinationIds.Same(s.Destination, DestinationIds.Editor));
            steps.Insert(editor >= 0 && !DestinationIds.Same(destination, DestinationIds.Editor) ? editor : steps.Count, new WorkflowStep(destination));
        }

        return this with { Workflow = steps };
    }

    public DestinationSettings Normalize()
    {
        var servers = UniqueIds((Servers ?? []).Where(s => s is not null).Select(s => s.Normalize()).ToList(), s => s.Id, (s, id) => s with { Id = id }, s => s.Name.Length > 0 ? s.Name : s.Host, "server");
        var uploads = UniqueIds((Uploads ?? []).Where(u => u is not null).Select(u => u.Normalize()).ToList(), u => u.Id, (u, id) => u with { Id = id }, u => u.Name, "upload");
        var apps = UniqueIds((Apps ?? []).Where(a => a is not null).Select(a => a.Normalize()).ToList(), a => a.Id, (a, id) => a with { Id = id }, a => a.Name.Length > 0 ? a.Name : System.IO.Path.GetFileNameWithoutExtension(a.Path), "app");
        return this with
        {
            Workflow = Steps(Workflow ?? DefaultWorkflow),
            RecordingWorkflow = Steps(RecordingWorkflow ?? []),
            Print = (Print ?? new PrintSettings()).Normalize(),
            Word = (Word ?? new OfficeSettings()).Normalize(),
            PowerPoint = (PowerPoint ?? new OfficeSettings()).Normalize(),
            Servers = servers,
            Uploads = uploads,
            Apps = apps,
            Pdf = (Pdf ?? new PdfSettings()).Normalize(),
        };
    }

    /// <summary>Records compare their lists by reference; this compares them by content.</summary>
    public bool Equals(DestinationSettings? other) =>
        other is not null
        && Same(Workflow, other.Workflow)
        && Same(RecordingWorkflow, other.RecordingWorkflow)
        && Equals(Print, other.Print)
        && Equals(Word, other.Word)
        && Equals(PowerPoint, other.PowerPoint)
        && Same(Servers, other.Servers)
        && Same(Uploads, other.Uploads)
        && Same(Apps, other.Apps)
        && Equals(Pdf, other.Pdf);

    public override int GetHashCode() =>
        HashCode.Combine(HashCode.Combine((Workflow ?? []).Count, (RecordingWorkflow ?? []).Count, Print, Word, PowerPoint, (Servers ?? []).Count, (Uploads ?? []).Count, (Apps ?? []).Count), Pdf);

    internal static bool Same<T>(IReadOnlyList<T>? a, IReadOnlyList<T>? b) => (a ?? []).SequenceEqual(b ?? []);

    /// <summary>Blank steps dropped, ids trimmed, each destination once (the first place it appears).</summary>
    private static List<WorkflowStep> Steps(IReadOnlyList<WorkflowStep> steps)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<WorkflowStep>();
        foreach (var step in steps)
        {
            if (step?.Destination is not { } id || string.IsNullOrWhiteSpace(id) || !seen.Add(id.Trim()))
            {
                continue;
            }

            result.Add(step with { Destination = id.Trim() });
        }

        return result;
    }

    /// <summary>
    /// Gives every entry an id that is unique in its list: a missing one is made from its name (so a hand-written
    /// entry keeps the same id every time the file is read), a repeated one gets a number.
    /// </summary>
    private static List<T> UniqueIds<T>(List<T> items, Func<T, string> id, Func<T, string, T> withId, Func<T, string> name, string fallback)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < items.Count; i++)
        {
            var wanted = id(items[i]).Length > 0 ? id(items[i]) : Slug(name(items[i]), fallback);
            var unique = wanted;
            for (var n = 2; !used.Add(unique); n++)
            {
                unique = wanted + "-" + n.ToString(CultureInfo.InvariantCulture);
            }

            if (unique != id(items[i]))
            {
                items[i] = withId(items[i], unique);
            }
        }

        return items;
    }

    /// <summary>"My FTP server" → "my-ftp-server"; letters and digits of any script are kept.</summary>
    public static string Slug(string? text, string fallback = "item")
    {
        var builder = new StringBuilder();
        foreach (var c in (text ?? string.Empty).Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        return slug.Length == 0 ? fallback : slug;
    }
}

/// <summary>One step of a workflow: a destination id (see <see cref="DestinationIds"/>), switched on or off.</summary>
public sealed record WorkflowStep(string Destination, bool Enabled = true);

/// <summary>How a capture is laid out on the page.</summary>
public enum PrintFit
{
    /// <summary>As large as fits inside the margins, all of it showing.</summary>
    FitToPage,

    /// <summary>Covering the whole area inside the margins; what sticks out is cut off.</summary>
    FillPage,

    /// <summary>As big as it was on the screen (its pixels at the monitor's scale).</summary>
    ActualSize,
}

public enum PageOrientation
{
    /// <summary>Landscape for a capture wider than tall, else portrait.</summary>
    Auto,
    Portrait,
    Landscape,
}

/// <param name="Fit">How the capture is sized on the page.</param>
/// <param name="Orientation">Portrait, landscape, or whichever suits the capture.</param>
/// <param name="MarginMm">The margin on every side, in millimetres (0 to 50).</param>
/// <param name="ShowDialog">Show the print dialog (printer, copies) each time; off prints straight away.</param>
/// <param name="Printer">The printer used without the dialog, by name; null for Windows' default printer.</param>
public sealed record PrintSettings(
    PrintFit Fit = PrintFit.FitToPage,
    PageOrientation Orientation = PageOrientation.Auto,
    double MarginMm = PrintSettings.DefaultMarginMm,
    bool ShowDialog = true,
    string? Printer = null)
{
    public const double DefaultMarginMm = 10;
    public const double MaxMarginMm = 50;

    public PrintSettings Normalize() => this with
    {
        Fit = Enum.IsDefined(Fit) ? Fit : PrintFit.FitToPage,
        Orientation = Enum.IsDefined(Orientation) ? Orientation : PageOrientation.Auto,
        MarginMm = double.IsFinite(MarginMm) ? Math.Clamp(MarginMm, 0, MaxMarginMm) : DefaultMarginMm,
        Printer = string.IsNullOrWhiteSpace(Printer) ? null : Printer.Trim(),
    };
}

/// <summary>A PDF of each capture, one page, in the capture folder.</summary>
/// <param name="PageSize">The paper, or the capture's own size.</param>
/// <param name="Orientation">Portrait, landscape, or whichever suits the capture.</param>
/// <param name="MarginMm">The margin on every side, in millimetres (0 to 50).</param>
/// <param name="Fit">How the capture is sized on the page.</param>
public sealed record PdfSettings(
    PaperSize PageSize = PaperSize.Auto,
    PageOrientation Orientation = PageOrientation.Auto,
    double MarginMm = PrintSettings.DefaultMarginMm,
    PrintFit Fit = PrintFit.FitToPage)
{
    public PdfSettings Normalize() => this with
    {
        PageSize = Enum.IsDefined(PageSize) ? PageSize : PaperSize.Auto,
        Orientation = Enum.IsDefined(Orientation) ? Orientation : PageOrientation.Auto,
        MarginMm = double.IsFinite(MarginMm) ? Math.Clamp(MarginMm, 0, PrintSettings.MaxMarginMm) : PrintSettings.DefaultMarginMm,
        Fit = Enum.IsDefined(Fit) ? Fit : PrintFit.FitToPage,
    };
}

public enum OfficeMode
{
    /// <summary>A new document (or presentation) in the capture folder for every capture.</summary>
    NewFile,

    /// <summary>Added at the end of one document (or presentation) the user chose.</summary>
    Append,
}

/// <param name="Mode">A new file each time, or added to <paramref name="AppendPath"/>.</param>
/// <param name="AppendPath">The document captures are added to. Environment variables are expanded.</param>
public sealed record OfficeSettings(OfficeMode Mode = OfficeMode.NewFile, string? AppendPath = null)
{
    public OfficeSettings Normalize() => this with
    {
        Mode = Enum.IsDefined(Mode) ? Mode : OfficeMode.NewFile,
        AppendPath = string.IsNullOrWhiteSpace(AppendPath) ? null : AppendPath.Trim().Trim('"'),
    };
}

public enum ServerProtocol
{
    /// <summary>Plain FTP (nothing is encrypted, the password included).</summary>
    Ftp,

    /// <summary>FTP that switches to TLS after connecting (AUTH TLS), usually on port 21.</summary>
    FtpsExplicit,

    /// <summary>FTP inside TLS from the start, usually on port 990.</summary>
    FtpsImplicit,

    /// <summary>SSH file transfer, usually on port 22.</summary>
    Sftp,
}

/// <summary>An FTP, FTPS or SFTP server captures are uploaded to. The password and key passphrase are kept with DPAPI.</summary>
/// <param name="Id">Unique among the servers; the workflow refers to it as <c>server:Id</c>.</param>
/// <param name="Name">Shown in menus and messages.</param>
/// <param name="Port">0 for the protocol's usual port.</param>
/// <param name="UserName">Null to log in anonymously (FTP).</param>
/// <param name="RemoteFolder">Where files go on the server (created if missing): empty for the folder the user logs in to, a path starting with / from the server's root, else from the login folder.</param>
/// <param name="Passive">FTP: the client opens the data connections (works through NAT and firewalls).</param>
/// <param name="PublicUrl">The web address uploads can be seen at, with <c>{file}</c> for the file name; null for no link.</param>
/// <param name="TrustedFingerprint">FTPS: a certificate Windows doesn't trust that the user accepted. SFTP: the server's host key (always required).</param>
/// <param name="KeyFile">SFTP: a private key file to log in with instead of the password.</param>
public sealed record ServerDestination(
    string Id = "",
    string Name = "",
    ServerProtocol Protocol = ServerProtocol.Sftp,
    string Host = "",
    int Port = 0,
    string? UserName = null,
    string RemoteFolder = "",
    bool Passive = true,
    string? PublicUrl = null,
    string? TrustedFingerprint = null,
    string? KeyFile = null)
{
    /// <summary><see cref="Port"/>, or the protocol's usual port.</summary>
    [JsonIgnore]
    public int EffectivePort => Port > 0 ? Port : DefaultPort(Protocol);

    public static int DefaultPort(ServerProtocol protocol) => protocol switch
    {
        ServerProtocol.Sftp => 22,
        ServerProtocol.FtpsImplicit => 990,
        _ => 21,
    };

    public ServerDestination Normalize()
    {
        var folder = (RemoteFolder ?? string.Empty).Trim().Replace('\\', '/');
        folder = folder.Length > 1 ? folder.TrimEnd('/') : folder;
        return this with
        {
            Id = Clean(Id),
            Name = (Name ?? string.Empty).Trim(),
            Protocol = Enum.IsDefined(Protocol) ? Protocol : ServerProtocol.Sftp,
            Host = (Host ?? string.Empty).Trim(),
            Port = Port is > 0 and <= 65535 ? Port : 0,
            UserName = string.IsNullOrWhiteSpace(UserName) ? null : UserName.Trim(),
            RemoteFolder = folder,
            PublicUrl = string.IsNullOrWhiteSpace(PublicUrl) ? null : PublicUrl.Trim(),
            TrustedFingerprint = string.IsNullOrWhiteSpace(TrustedFingerprint) ? null : TrustedFingerprint.Trim(),
            KeyFile = string.IsNullOrWhiteSpace(KeyFile) ? null : KeyFile.Trim().Trim('"'),
        };
    }

    internal static string Clean(string? id) => DestinationSettings.Slug(id, string.Empty);
}

/// <summary>A name and a value (a form field or a header).</summary>
public sealed record NameValue(string Name, string Value);

/// <summary>An HTTP endpoint captures are posted to (multipart/form-data). The auth header's value is kept with DPAPI.</summary>
/// <param name="Id">Unique among the uploads; the workflow refers to it as <c>upload:Id</c>.</param>
/// <param name="Url">Where the form is posted (http or https).</param>
/// <param name="FileField">The form field that carries the file.</param>
/// <param name="Fields">More form fields, sent as they are.</param>
/// <param name="Headers">More request headers, sent as they are (not secret).</param>
/// <param name="AuthHeader">The header whose value (e.g. <c>Bearer …</c>) is secret; null for none.</param>
/// <param name="LinkPath">Where the link is in the JSON reply (<c>data.link</c>, <c>files[0].url</c>); null when the whole reply is the link.</param>
public sealed record HttpDestination(
    string Id = "",
    string Name = "",
    string Url = "",
    string FileField = HttpDestination.DefaultFileField,
    IReadOnlyList<NameValue>? Fields = null,
    IReadOnlyList<NameValue>? Headers = null,
    string? AuthHeader = null,
    string? LinkPath = null)
{
    public const string DefaultFileField = "file";

    public IReadOnlyList<NameValue>? Fields { get; init; } = Fields ?? [];

    public IReadOnlyList<NameValue>? Headers { get; init; } = Headers ?? [];

    public HttpDestination Normalize() => this with
    {
        Id = ServerDestination.Clean(Id),
        Name = (Name ?? string.Empty).Trim(),
        Url = (Url ?? string.Empty).Trim(),
        FileField = string.IsNullOrWhiteSpace(FileField) ? DefaultFileField : FileField.Trim(),
        Fields = Pairs(Fields),
        Headers = Pairs(Headers),
        AuthHeader = string.IsNullOrWhiteSpace(AuthHeader) ? null : AuthHeader.Trim(),
        LinkPath = string.IsNullOrWhiteSpace(LinkPath) ? null : LinkPath.Trim(),
    };

    private static List<NameValue> Pairs(IReadOnlyList<NameValue>? pairs) =>
        [.. (pairs ?? []).Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Name)).Select(p => new NameValue(p.Name.Trim(), p.Value ?? string.Empty))];

    /// <summary>Records compare their lists by reference; this compares them by content.</summary>
    public bool Equals(HttpDestination? other) =>
        other is not null
        && Id == other.Id
        && Name == other.Name
        && Url == other.Url
        && FileField == other.FileField
        && DestinationSettings.Same(Fields, other.Fields)
        && DestinationSettings.Same(Headers, other.Headers)
        && AuthHeader == other.AuthHeader
        && LinkPath == other.LinkPath;

    public override int GetHashCode() => HashCode.Combine(Id, Name, Url, FileField, (Fields ?? []).Count, (Headers ?? []).Count, AuthHeader, LinkPath);
}

/// <summary>A program captures can be opened with (Paint.NET, Photoshop, …).</summary>
/// <param name="Id">Unique among the apps; the workflow refers to it as <c>app:Id</c>.</param>
/// <param name="Path">The program (.exe), or its name when it is on the PATH.</param>
/// <param name="Arguments">Its command line, with <c>{file}</c> for the capture's file (and <c>{folder}</c> for its folder).</param>
public sealed record ExternalApp(string Id = "", string Name = "", string Path = "", string Arguments = ExternalApp.DefaultArguments)
{
    public const string DefaultArguments = "\"{file}\"";

    public ExternalApp Normalize() => this with
    {
        Id = ServerDestination.Clean(Id),
        Name = (Name ?? string.Empty).Trim(),
        Path = (Path ?? string.Empty).Trim().Trim('"'),
        Arguments = string.IsNullOrWhiteSpace(Arguments) ? DefaultArguments : Arguments.Trim(),
    };
}
