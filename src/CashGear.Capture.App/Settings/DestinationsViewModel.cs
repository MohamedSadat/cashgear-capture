using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using CashGear.Capture.App.Output;
using CashGear.Capture.App.Resources;
using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Outputs;
using CashGear.Capture.Outputs.Credentials;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace CashGear.Capture.App.Settings;

/// <summary>Picks a file to open (behind an interface so the view model is testable).</summary>
public interface IFilePicker
{
    /// <summary>The chosen file, or null if the user cancelled. <paramref name="filter"/> is an Open dialog filter ("Word documents|*.docx").</summary>
    string? PickFile(string title, string filter, string? initialPath);
}

internal sealed class WpfFilePicker : IFilePicker
{
    public string? PickFile(string title, string filter, string? initialPath)
    {
        var folder = initialPath is null ? null : Path.GetDirectoryName(Environment.ExpandEnvironmentVariables(initialPath));
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            CheckFileExists = true,
            InitialDirectory = folder is not null && Directory.Exists(folder) ? folder : null,
        };
        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }
}

/// <summary>A destination that can be added to a workflow.</summary>
public sealed record DestinationChoice(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>One step in a workflow list: on or off, moved up and down, removed.</summary>
public sealed partial class WorkflowStepRow(string destination) : ObservableObject
{
    public string Destination { get; } = destination;

    [ObservableProperty]
    private string _name = destination;

    [ObservableProperty]
    private bool _enabled = true;

    /// <summary>Its destination isn't set up any more (it is skipped).</summary>
    [ObservableProperty]
    private bool _isMissing;
}

/// <summary>
/// An ordered list of steps (Settings › Output): the after-capture workflow, or what happens to a recording once it
/// is saved. Steps come from the destinations as they are being edited, so a server added on the Destinations tab can
/// be added here at once.
/// </summary>
public sealed partial class WorkflowListViewModel : ObservableObject
{
    private readonly Func<IReadOnlyList<DestinationChoice>> _available;

    public WorkflowListViewModel(Func<IReadOnlyList<DestinationChoice>> available) => _available = available;

    public event EventHandler? Changed;

    public ObservableCollection<WorkflowStepRow> Steps { get; } = [];

    /// <summary>Destinations not in the list yet.</summary>
    [ObservableProperty]
    private IReadOnlyList<DestinationChoice> _addable = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private DestinationChoice? _toAdd;

    public bool AnyEnabled => Steps.Any(s => s.Enabled && !s.IsMissing);

    public void Load(IReadOnlyList<WorkflowStep>? steps)
    {
        foreach (var row in Steps)
        {
            row.PropertyChanged -= OnRowChanged;
        }

        Steps.Clear();
        foreach (var step in steps ?? [])
        {
            Add(new WorkflowStepRow(step.Destination) { Enabled = step.Enabled });
        }

        Refresh();
    }

    public List<WorkflowStep> Build() => [.. Steps.Select(r => new WorkflowStep(r.Destination, r.Enabled))];

    /// <summary>Names the steps after the destinations as they are now and lists what can be added.</summary>
    public void Refresh()
    {
        var available = _available();
        foreach (var row in Steps)
        {
            var match = available.FirstOrDefault(d => DestinationIds.Same(d.Id, row.Destination));
            row.Name = match?.Name ?? DestinationsText.NotSetUp(row.Destination);
            row.IsMissing = match is null;
        }

        Addable = [.. available.Where(d => !Steps.Any(s => DestinationIds.Same(s.Destination, d.Id)))];
        ToAdd = Addable.FirstOrDefault();
        OnPropertyChanged(nameof(AnyEnabled));
    }

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add()
    {
        if (ToAdd is not { } choice)
        {
            return;
        }

        Add(new WorkflowStepRow(choice.Id) { Name = choice.Name });
        Refresh();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private bool CanAdd() => ToAdd is not null;

    [RelayCommand]
    private void MoveUp(WorkflowStepRow row) => Move(row, -1);

    [RelayCommand]
    private void MoveDown(WorkflowStepRow row) => Move(row, +1);

    [RelayCommand]
    private void Remove(WorkflowStepRow row)
    {
        row.PropertyChanged -= OnRowChanged;
        Steps.Remove(row);
        Refresh();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Move(WorkflowStepRow row, int by)
    {
        var from = Steps.IndexOf(row);
        var to = from + by;
        if (from < 0 || to < 0 || to >= Steps.Count)
        {
            return;
        }

        Steps.Move(from, to);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Add(WorkflowStepRow row)
    {
        row.PropertyChanged += OnRowChanged;
        Steps.Add(row);
    }

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkflowStepRow.Enabled))
        {
            OnPropertyChanged(nameof(AnyEnabled));
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>What a Test button found, for the text beside it.</summary>
public abstract partial class TestableViewModel : ObservableObject
{
    [ObservableProperty]
    private string _testStatus = string.Empty;

    [ObservableProperty]
    private bool _testFailed;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestCommand))]
    private bool _testing;

    /// <summary>Runs the destination's own test with the settings and secrets as they are in the window.</summary>
    internal Func<string, Task<OutputResult>>? Tester { get; set; }

    /// <summary>The id of the destination this tests.</summary>
    public abstract string DestinationId { get; }

    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestAsync()
    {
        if (Tester is null)
        {
            return;
        }

        Testing = true;
        TestStatus = DestinationsText.Testing;
        TestFailed = false;
        try
        {
            var result = await Tester(DestinationId);
            TestStatus = result.Message;
            TestFailed = !result.Succeeded;
            OnTested(result);
        }
        finally
        {
            Testing = false;
        }
    }

    private bool CanTest() => !Testing;

    protected virtual void OnTested(OutputResult result)
    {
    }
}

/// <summary>Settings › Destinations › the printer.</summary>
public sealed partial class PrinterViewModel : TestableViewModel
{
    public override string DestinationId => DestinationIds.Printer;

    [ObservableProperty]
    private PrintFit _fit;

    [ObservableProperty]
    private PageOrientation _orientation;

    [ObservableProperty]
    private double _marginMm;

    [ObservableProperty]
    private bool _showDialog;

    /// <summary>The printer by name; null for Windows' default.</summary>
    [ObservableProperty]
    private DeviceChoice? _printer;

    public ObservableCollection<DeviceChoice> Printers { get; } = [];

    public IReadOnlyList<PrintFit> Fits { get; } = Enum.GetValues<PrintFit>();

    public IReadOnlyList<PageOrientation> Orientations { get; } = Enum.GetValues<PageOrientation>();

    public void Load(PrintSettings settings, IReadOnlyList<string> installed)
    {
        Printers.Clear();
        Printers.Add(new DeviceChoice(null, DestinationsText.DefaultPrinter));
        foreach (var name in installed)
        {
            Printers.Add(new DeviceChoice(name, name));
        }

        Printer = Printers.FirstOrDefault(p => string.Equals(p.Id, settings.Printer, StringComparison.OrdinalIgnoreCase));
        if (Printer is null)
        {
            Printer = new DeviceChoice(settings.Printer, DestinationsText.PrinterNotInstalled(settings.Printer));
            Printers.Add(Printer);
        }

        Fit = settings.Fit;
        Orientation = settings.Orientation;
        MarginMm = settings.MarginMm;
        ShowDialog = settings.ShowDialog;
    }

    public PrintSettings Build() => new(Fit, Orientation, Math.Round(MarginMm, 1), ShowDialog, Printer?.Id);
}

/// <summary>Settings › Destinations › PDF.</summary>
public sealed partial class PdfViewModel : TestableViewModel
{
    public override string DestinationId => DestinationIds.Pdf;

    [ObservableProperty]
    private PaperSize _pageSize;

    [ObservableProperty]
    private PageOrientation _orientation;

    [ObservableProperty]
    private double _marginMm;

    [ObservableProperty]
    private PrintFit _fit;

    public IReadOnlyList<PaperSize> PageSizes { get; } = Enum.GetValues<PaperSize>();

    public IReadOnlyList<PageOrientation> Orientations { get; } = Enum.GetValues<PageOrientation>();

    public IReadOnlyList<PrintFit> Fits { get; } = Enum.GetValues<PrintFit>();

    public void Load(PdfSettings settings)
    {
        PageSize = settings.PageSize;
        Orientation = settings.Orientation;
        MarginMm = settings.MarginMm;
        Fit = settings.Fit;
    }

    public PdfSettings Build() => new(PageSize, Orientation, Math.Round(MarginMm, 1), Fit);
}

/// <summary>Settings › Destinations › Word or PowerPoint.</summary>
public sealed partial class OfficeViewModel(string destination, string app, string filter, IFilePicker? picker) : TestableViewModel
{
    public override string DestinationId => destination;

    public string App => app;

    /// <summary>On: added to <see cref="AppendPath"/>; off: a new file each time.</summary>
    [ObservableProperty]
    private bool _append;

    [ObservableProperty]
    private string _appendPath = string.Empty;

    public bool CanBrowse => picker is not null;

    public void Load(OfficeSettings settings)
    {
        Append = settings.Mode == OfficeMode.Append;
        AppendPath = settings.AppendPath ?? string.Empty;
    }

    public OfficeSettings Build() => new(Append ? OfficeMode.Append : OfficeMode.NewFile, string.IsNullOrWhiteSpace(AppendPath) ? null : AppendPath.Trim());

    [RelayCommand]
    private void Browse()
    {
        if (picker?.PickFile(DestinationsText.ChooseOfficeFile(app), filter, AppendPath.Length > 0 ? AppendPath : null) is { } file)
        {
            AppendPath = file;
            Append = true;
        }
    }
}

/// <summary>One FTP, FTPS or SFTP server in Settings › Destinations. Its password and key passphrase are kept with DPAPI.</summary>
public sealed partial class ServerViewModel : TestableViewModel
{
    private readonly PendingCredentials _secrets;
    private readonly IFilePicker? _picker;

    public ServerViewModel(ServerDestination server, PendingCredentials secrets, IFilePicker? picker)
    {
        _secrets = secrets;
        _picker = picker;
        Id = server.Id;
        _name = server.Name;
        _protocol = server.Protocol;
        _host = server.Host;
        _port = server.Port > 0 ? server.Port.ToString(CultureInfo.CurrentCulture) : string.Empty;
        _userName = server.UserName ?? string.Empty;
        _remoteFolder = server.RemoteFolder;
        _passive = server.Passive;
        _publicUrl = server.PublicUrl ?? string.Empty;
        _trustedFingerprint = server.TrustedFingerprint;
        _keyFile = server.KeyFile ?? string.Empty;
    }

    public string Id { get; }

    public override string DestinationId => DestinationIds.Server(Id);

    public IReadOnlyList<ServerProtocol> Protocols { get; } = Enum.GetValues<ServerProtocol>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    private string _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label), nameof(IsSftp), nameof(IsFtp), nameof(PortHint))]
    private ServerProtocol _protocol;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    private string _host;

    [ObservableProperty]
    private string _port;

    [ObservableProperty]
    private string _userName;

    [ObservableProperty]
    private string _remoteFolder;

    [ObservableProperty]
    private bool _passive;

    [ObservableProperty]
    private string _publicUrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTrustedFingerprint))]
    [NotifyCanExecuteChangedFor(nameof(ForgetFingerprintCommand))]
    private string? _trustedFingerprint;

    [ObservableProperty]
    private string _keyFile;

    /// <summary>A certificate or key the last Test found untrusted, which the user may trust.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TrustCommand))]
    private string? _untrustedFingerprint;

    public string Label => $"{(Name.Length > 0 ? Name : Host.Length > 0 ? Host : DestinationsText.NewServer)} ({Describe(Protocol)})";

    /// <summary>Its label: what a screen reader says for it in the list.</summary>
    public override string ToString() => Label;

    public bool IsSftp => Protocol == ServerProtocol.Sftp;

    public bool IsFtp => !IsSftp;

    public string PortHint => DestinationsText.PortEmptyFor(ServerDestination.DefaultPort(Protocol));

    public bool HasTrustedFingerprint => TrustedFingerprint is not null;

    public string PasswordState => _secrets.Get(CredentialKeys.Password(Build())) is null ? DestinationsText.NoPasswordSaved : DestinationsText.PasswordSaved;

    public string PassphraseState => _secrets.Get(CredentialKeys.Passphrase(Build())) is null ? DestinationsText.NoPassphraseSaved : DestinationsText.PassphraseSaved;

    public bool CanBrowse => _picker is not null;

    /// <summary>A password typed into the window (kept until OK or Apply); an emptied box keeps the saved one (Forget removes it).</summary>
    public void SetPassword(string password)
    {
        Type(CredentialKeys.Password(Build()), password);
        OnPropertyChanged(nameof(PasswordState));
    }

    public void SetPassphrase(string passphrase)
    {
        Type(CredentialKeys.Passphrase(Build()), passphrase);
        OnPropertyChanged(nameof(PassphraseState));
    }

    private void Type(string key, string value)
    {
        if (value.Length == 0)
        {
            _secrets.Revert(key);
        }
        else
        {
            _secrets.Set(key, value);
        }
    }

    [RelayCommand]
    private void ForgetPassword()
    {
        _secrets.Set(CredentialKeys.Password(Build()), null);
        _secrets.Set(CredentialKeys.Passphrase(Build()), null);
        OnPropertyChanged(nameof(PasswordState));
        OnPropertyChanged(nameof(PassphraseState));
    }

    [RelayCommand(CanExecute = nameof(HasTrustedFingerprint))]
    private void ForgetFingerprint() => TrustedFingerprint = null;

    /// <summary>Trusts the certificate or key the last Test showed, and tests again.</summary>
    [RelayCommand(CanExecute = nameof(CanTrust))]
    private async Task TrustAsync()
    {
        TrustedFingerprint = UntrustedFingerprint;
        UntrustedFingerprint = null;
        await TestCommand.ExecuteAsync(null);
    }

    private bool CanTrust() => UntrustedFingerprint is not null;

    [RelayCommand]
    private void BrowseKeyFile()
    {
        if (_picker?.PickFile(DestinationsText.ChoosePrivateKey, DestinationsText.PrivateKeyFilter, KeyFile.Length > 0 ? KeyFile : null) is { } file)
        {
            KeyFile = file;
        }
    }

    protected override void OnTested(OutputResult result) => UntrustedFingerprint = result.Fingerprint;

    public ServerDestination Build() => new ServerDestination(
        Id,
        Name,
        Protocol,
        Host,
        int.TryParse(Port, NumberStyles.Integer, CultureInfo.CurrentCulture, out var port) ? port : 0,
        UserName,
        RemoteFolder,
        Passive,
        PublicUrl,
        TrustedFingerprint,
        IsSftp ? KeyFile : null).Normalize() with { Id = Id };

    /// <summary>What is wrong with it, for the window's error list.</summary>
    public IEnumerable<string> Problems()
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            yield return DestinationsText.ServerNoAddress(Label);
        }

        if (Port.Trim().Length > 0 && (!int.TryParse(Port, NumberStyles.Integer, CultureInfo.CurrentCulture, out var port) || port is < 1 or > 65535))
        {
            yield return DestinationsText.ServerBadPort(Label);
        }

        if (PublicUrl.Trim().Length > 0 && !(Uri.TryCreate(PublicUrl.Replace("{file}", "x", StringComparison.OrdinalIgnoreCase), UriKind.Absolute, out var url) && url.Scheme is "http" or "https"))
        {
            yield return DestinationsText.ServerBadPublicUrl(Label);
        }
    }

    private static string Describe(ServerProtocol protocol) => protocol switch
    {
        ServerProtocol.FtpsExplicit => DestinationsText.ProtocolFtps,
        ServerProtocol.FtpsImplicit => DestinationsText.ProtocolFtpsImplicit,
        ServerProtocol.Sftp => DestinationsText.ProtocolSftp,
        _ => DestinationsText.ProtocolFtp,
    };
}

/// <summary>One HTTP upload endpoint in Settings › Destinations. The secret header's value is kept with DPAPI.</summary>
public sealed partial class UploadViewModel : TestableViewModel
{
    private readonly PendingCredentials _secrets;

    public UploadViewModel(HttpDestination upload, PendingCredentials secrets)
    {
        _secrets = secrets;
        Id = upload.Id;
        _name = upload.Name;
        _url = upload.Url;
        _fileField = upload.FileField;
        _fieldsText = Lines(upload.Fields, "=");
        _headersText = Lines(upload.Headers, ": ");
        _authHeader = upload.AuthHeader ?? string.Empty;
        _linkPath = upload.LinkPath ?? string.Empty;
    }

    public string Id { get; }

    public override string DestinationId => DestinationIds.Upload(Id);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    private string _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    private string _url;

    [ObservableProperty]
    private string _fileField;

    /// <summary>One field a line: <c>name=value</c>.</summary>
    [ObservableProperty]
    private string _fieldsText;

    /// <summary>One header a line: <c>Name: value</c>.</summary>
    [ObservableProperty]
    private string _headersText;

    [ObservableProperty]
    private string _authHeader;

    [ObservableProperty]
    private string _linkPath;

    public string Label => Name.Length > 0 ? Name : Uri.TryCreate(Url, UriKind.Absolute, out var url) ? url.Host : DestinationsText.NewUpload;

    /// <summary>Its label: what a screen reader says for it in the list.</summary>
    public override string ToString() => Label;

    public string TokenState => _secrets.Get(CredentialKeys.Token(Build())) is null ? DestinationsText.NoValueSaved : DestinationsText.ValueSaved;

    /// <summary>The secret header's value typed into the window; an emptied box keeps the saved one (Forget removes it).</summary>
    public void SetToken(string token)
    {
        if (token.Length == 0)
        {
            _secrets.Revert(CredentialKeys.Token(Build()));
        }
        else
        {
            _secrets.Set(CredentialKeys.Token(Build()), token);
        }

        OnPropertyChanged(nameof(TokenState));
    }

    [RelayCommand]
    private void ForgetToken()
    {
        _secrets.Set(CredentialKeys.Token(Build()), null);
        OnPropertyChanged(nameof(TokenState));
    }

    public HttpDestination Build() => new HttpDestination(
        Id,
        Name,
        Url,
        FileField,
        Pairs(FieldsText, '='),
        Pairs(HeadersText, ':'),
        AuthHeader,
        LinkPath).Normalize() with { Id = Id };

    public IEnumerable<string> Problems()
    {
        if (!(Uri.TryCreate(Url.Trim(), UriKind.Absolute, out var url) && url.Scheme is "http" or "https"))
        {
            yield return DestinationsText.UploadBadUrl(Label);
        }

        if (LinkPath.Trim().Length > 0 && !Outputs.Http.LinkPath.TryParse(LinkPath, out _, out var error))
        {
            yield return DestinationsText.UploadBadLinkPath(Label, error);
        }

        foreach (var line in Lines(HeadersText).Where(l => !l.Contains(':', StringComparison.Ordinal)))
        {
            yield return DestinationsText.UploadBadHeader(Label, line);
        }

        foreach (var line in Lines(FieldsText).Where(l => !l.Contains('=', StringComparison.Ordinal)))
        {
            yield return DestinationsText.UploadBadField(Label, line);
        }
    }

    private static IEnumerable<string> Lines(string text) =>
        text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static List<NameValue> Pairs(string text, char separator) =>
        [.. Lines(text).Where(l => l.Contains(separator, StringComparison.Ordinal)).Select(l => new NameValue(l[..l.IndexOf(separator, StringComparison.Ordinal)].Trim(), l[(l.IndexOf(separator, StringComparison.Ordinal) + 1)..].Trim()))];

    private static string Lines(IReadOnlyList<NameValue>? pairs, string separator) =>
        string.Join(Environment.NewLine, (pairs ?? []).Select(p => p.Name + separator + p.Value));
}

/// <summary>One program captures can be opened with, in Settings › Destinations.</summary>
public sealed partial class ExternalAppViewModel(ExternalApp app, IFilePicker? picker) : TestableViewModel
{
    public string Id { get; } = app.Id;

    public override string DestinationId => DestinationIds.App(Id);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    private string _name = app.Name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    private string _path = app.Path;

    [ObservableProperty]
    private string _arguments = app.Arguments;

    public string Label => Name.Length > 0 ? Name : Path.Length > 0 ? System.IO.Path.GetFileNameWithoutExtension(Path) : DestinationsText.NewApp;

    /// <summary>Its label: what a screen reader says for it in the list.</summary>
    public override string ToString() => Label;

    public bool CanBrowse => picker is not null;

    [RelayCommand]
    private void Browse()
    {
        if (picker?.PickFile(DestinationsText.ChooseProgram, DestinationsText.ProgramFilter, Path.Length > 0 ? Path : null) is { } file)
        {
            Path = file;
            if (Name.Length == 0)
            {
                Name = System.IO.Path.GetFileNameWithoutExtension(file);
            }
        }
    }

    public ExternalApp Build() => new ExternalApp(Id, Name, Path, Arguments).Normalize() with { Id = Id };

    public IEnumerable<string> Problems()
    {
        if (string.IsNullOrWhiteSpace(Path))
        {
            yield return DestinationsText.AppNoProgram(Label);
        }
    }
}

/// <summary>
/// A destination with a sign-in, in Settings › Destinations. Signing in and out is done at once (the browser has said so
/// to the service already), unlike what is typed in the window, which waits for OK. Nothing else about it is secret.
/// </summary>
public sealed partial class SignInViewModel(string id, string name, Func<string, Outputs.Sharing.ISignInDestination?> find) : TestableViewModel
{
    private CancellationTokenSource? _signingIn;

    public override string DestinationId => id;

    public string Name => name;

    public bool IsYouTube => DestinationIds.Same(id, DestinationIds.YouTube);

    public bool IsCloudDrive => !IsYouTube;

    /// <summary>This build (or settings.json) has the app's registration with the service.</summary>
    public bool IsAvailable => find(id) is not null;

    public bool IsSignedIn => find(id)?.IsSignedIn == true;

    public bool IsSignedOut => IsAvailable && !IsSignedIn && !IsWaiting;

    /// <summary>Who is signed in, or that nobody is.</summary>
    public string AccountText => find(id) switch
    {
        { IsSignedIn: true, Account: { Length: > 0 } account } => DestinationsText.SignedInAccount(account),
        { IsSignedIn: true } => DestinationsText.SignedIn,
        _ => IsWaiting ? DestinationsText.WaitingForBrowser : DestinationsText.NotSignedIn,
    };

    /// <summary>The browser is open, and the app waits for the service to come back.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccountText))]
    [NotifyPropertyChangedFor(nameof(IsSignedOut))]
    private bool _isWaiting;

    [ObservableProperty]
    private string _folder = string.Empty;

    [ObservableProperty]
    private bool _anyoneWithLink;

    [ObservableProperty]
    private VideoPrivacy _privacy;

    public IReadOnlyList<VideoPrivacy> Privacies { get; } = Enum.GetValues<VideoPrivacy>();

    [ObservableProperty]
    private string _description = string.Empty;

    /// <summary>Separated by commas.</summary>
    [ObservableProperty]
    private string _tags = string.Empty;

    [ObservableProperty]
    private string _playlist = string.Empty;

    [ObservableProperty]
    private bool _madeForKids;

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (find(id) is not { } destination)
        {
            return;
        }

        IsWaiting = true;
        TestFailed = false;
        TestStatus = string.Empty;
        _signingIn = new CancellationTokenSource();
        try
        {
            await destination.SignInAsync(_signingIn.Token);
        }
        catch (Outputs.Sharing.SignInRequiredException e)
        {
            TestStatus = e.Message;
            TestFailed = true;
        }
        catch (OperationCanceledException)
        {
            // the user changed their mind
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or System.Net.Sockets.SocketException or IOException or System.Text.Json.JsonException)
        {
            TestStatus = DestinationsText.SignInFailed(name, e.Message);
            TestFailed = true;
        }
        finally
        {
            _signingIn.Dispose();
            _signingIn = null;
            IsWaiting = false;
            Refresh();
        }
    }

    /// <summary>Stops waiting for the browser.</summary>
    [RelayCommand]
    private void CancelSignIn() => _signingIn?.Cancel();

    [RelayCommand]
    private void SignOut()
    {
        find(id)?.SignOut();
        TestStatus = string.Empty;
        TestFailed = false;
        Refresh();
    }

    /// <summary>The window closed: stops waiting.</summary>
    public void Stop() => _signingIn?.Cancel();

    /// <summary>The destination's state may have changed.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(IsSignedIn));
        OnPropertyChanged(nameof(IsSignedOut));
        OnPropertyChanged(nameof(AccountText));
    }

    public void Load(YouTubeSettings settings)
    {
        Privacy = settings.Privacy;
        Description = settings.Description;
        Tags = string.Join(", ", settings.Tags ?? []);
        Playlist = settings.Playlist ?? string.Empty;
        MadeForKids = settings.MadeForKids;
    }

    public void Load(CloudFolderSettings settings)
    {
        Folder = settings.Folder ?? string.Empty;
        AnyoneWithLink = settings.AnyoneWithLink;
    }

    public YouTubeSettings BuildYouTube() =>
        new YouTubeSettings(Privacy, Description, [.. Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)], Playlist, MadeForKids).Normalize();

    public CloudFolderSettings BuildFolder() => new CloudFolderSettings(Folder, AnyoneWithLink).Normalize();
}

/// <summary>
/// Settings › Destinations: the printer, Word, PowerPoint, servers, uploads and apps, each with a Test button.
/// Secrets typed here wait in <see cref="Secrets"/> until OK or Apply.
/// </summary>
public sealed partial class DestinationsViewModel : ObservableObject
{
    private readonly IFilePicker? _picker;
    private readonly IPrinter? _printer;

    public DestinationsViewModel(ICredentialStore? credentials, IFilePicker? picker, IPrinter? printer)
    {
        Secrets = new PendingCredentials(credentials ?? new NoCredentials());
        _picker = picker;
        _printer = printer;
        Word = new OfficeViewModel(DestinationIds.Word, "Word", DestinationsText.WordFilter, picker);
        PowerPoint = new OfficeViewModel(DestinationIds.PowerPoint, "PowerPoint", DestinationsText.PowerPointFilter, picker);
        Sharing =
        [
            new SignInViewModel(DestinationIds.YouTube, DestinationsText.YouTube, FindSignIn),
            new SignInViewModel(DestinationIds.GoogleDrive, DestinationsText.GoogleDrive, FindSignIn),
            new SignInViewModel(DestinationIds.OneDrive, DestinationsText.OneDrive, FindSignIn),
            new SignInViewModel(DestinationIds.Dropbox, DestinationsText.Dropbox, FindSignIn),
        ];
    }

    /// <summary>The sign-in destinations: those this build has a registration for show themselves.</summary>
    public IReadOnlyList<SignInViewModel> Sharing { get; }

    /// <summary>This build has no registration with any of the services, so there is nothing to sign in to.</summary>
    public bool NoSharing => !Sharing.Any(s => s.IsAvailable);

    /// <summary>Finds a sign-in destination as the window has it (set by the owner).</summary>
    internal Func<string, Outputs.Sharing.ISignInDestination?>? SignIns { get; set; }

    private Outputs.Sharing.ISignInDestination? FindSignIn(string id) => SignIns?.Invoke(id);

    /// <summary>Raised when a destination is added, removed or renamed (the workflow lists follow).</summary>
    public event EventHandler? ListChanged;

    /// <summary>Raised when anything of a server, upload or app is edited (the window checks it again).</summary>
    public event EventHandler? Edited;

    public PendingCredentials Secrets { get; }

    public PrinterViewModel Printer { get; } = new();

    public OfficeViewModel Word { get; }

    public OfficeViewModel PowerPoint { get; }

    public PdfViewModel Pdf { get; } = new();

    public ObservableCollection<ServerViewModel> Servers { get; } = [];

    public ObservableCollection<UploadViewModel> Uploads { get; } = [];

    public ObservableCollection<ExternalAppViewModel> Apps { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveServerCommand))]
    private ServerViewModel? _selectedServer;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveUploadCommand))]
    private UploadViewModel? _selectedUpload;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveAppCommand))]
    private ExternalAppViewModel? _selectedApp;

    /// <summary>Runs a destination's test with the settings as they are in the window.</summary>
    internal Func<string, Task<OutputResult>>? Tester
    {
        get;
        set
        {
            field = value;
            foreach (var item in Testables())
            {
                item.Tester = value;
            }
        }
    }

    public void Load(DestinationSettings settings)
    {
        Printer.Load(settings.Print ?? new PrintSettings(), _printer?.Printers() ?? []);
        Word.Load(settings.Word ?? new OfficeSettings());
        PowerPoint.Load(settings.PowerPoint ?? new OfficeSettings());
        Pdf.Load(settings.Pdf ?? new PdfSettings());
        var sharing = settings.Sharing ?? new SharingSettings();
        Sharing[0].Load(sharing.YouTube ?? new YouTubeSettings());
        Sharing[1].Load(sharing.GoogleDrive ?? new CloudFolderSettings());
        Sharing[2].Load(sharing.OneDrive ?? new CloudFolderSettings());
        Sharing[3].Load(sharing.Dropbox ?? new CloudFolderSettings());
        foreach (var row in Sharing)
        {
            if (!_trackedSharing)
            {
                Track(row);
            }

            row.Refresh();
        }

        _trackedSharing = true;
        OnPropertyChanged(nameof(NoSharing));
        Replace(Servers, (settings.Servers ?? []).Select(s => new ServerViewModel(s, Secrets, _picker)));
        Replace(Uploads, (settings.Uploads ?? []).Select(u => new UploadViewModel(u, Secrets)));
        Replace(Apps, (settings.Apps ?? []).Select(a => new ExternalAppViewModel(a, _picker)));
        SelectedServer = Servers.FirstOrDefault();
        SelectedUpload = Uploads.FirstOrDefault();
        SelectedApp = Apps.FirstOrDefault();
        Tester = Tester;
    }

    /// <summary>The destinations as edited, on top of <paramref name="current"/> (the workflows are the Output tab's).</summary>
    public DestinationSettings Build(DestinationSettings current) => current with
    {
        Print = Printer.Build(),
        Word = Word.Build(),
        PowerPoint = PowerPoint.Build(),
        Pdf = Pdf.Build(),
        Servers = [.. Servers.Select(s => s.Build())],
        Uploads = [.. Uploads.Select(u => u.Build())],
        Apps = [.. Apps.Select(a => a.Build())],
        Sharing = (current.Sharing ?? new SharingSettings()) with
        {
            YouTube = Sharing[0].BuildYouTube(),
            GoogleDrive = Sharing[1].BuildFolder(),
            OneDrive = Sharing[2].BuildFolder(),
            Dropbox = Sharing[3].BuildFolder(),
        },
    };

    private bool _trackedSharing;

    public IEnumerable<string> Problems() =>
        Servers.SelectMany(s => s.Problems()).Concat(Uploads.SelectMany(u => u.Problems())).Concat(Apps.SelectMany(a => a.Problems()));

    [RelayCommand]
    private void AddServer()
    {
        var server = new ServerViewModel(new ServerDestination(NewId("server", Servers.Select(s => s.Id))), Secrets, _picker) { Tester = Tester };
        Track(server);
        Servers.Add(server);
        SelectedServer = server;
        ListChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedServer))]
    private void RemoveServer()
    {
        if (SelectedServer is not { } server)
        {
            return;
        }

        Secrets.RemoveAll(CredentialKeys.Prefix(server.Build()));
        Servers.Remove(server);
        SelectedServer = Servers.FirstOrDefault();
        ListChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool HasSelectedServer() => SelectedServer is not null;

    [RelayCommand]
    private void AddUpload()
    {
        var upload = new UploadViewModel(new HttpDestination(NewId("upload", Uploads.Select(u => u.Id))), Secrets) { Tester = Tester };
        Track(upload);
        Uploads.Add(upload);
        SelectedUpload = upload;
        ListChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedUpload))]
    private void RemoveUpload()
    {
        if (SelectedUpload is not { } upload)
        {
            return;
        }

        Secrets.RemoveAll(CredentialKeys.Prefix(upload.Build()));
        Uploads.Remove(upload);
        SelectedUpload = Uploads.FirstOrDefault();
        ListChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool HasSelectedUpload() => SelectedUpload is not null;

    [RelayCommand]
    private void AddApp()
    {
        var app = new ExternalAppViewModel(new ExternalApp(NewId("app", Apps.Select(a => a.Id))), _picker) { Tester = Tester };
        Track(app);
        Apps.Add(app);
        SelectedApp = app;
        ListChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedApp))]
    private void RemoveApp()
    {
        if (SelectedApp is not { } app)
        {
            return;
        }

        Apps.Remove(app);
        SelectedApp = Apps.FirstOrDefault();
        ListChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool HasSelectedApp() => SelectedApp is not null;

    private IEnumerable<TestableViewModel> Testables() =>
        new TestableViewModel[] { Printer, Word, PowerPoint, Pdf }.Concat(Servers).Concat(Uploads).Concat(Apps).Concat(Sharing);

    private void Replace<T>(ObservableCollection<T> list, IEnumerable<T> items)
        where T : ObservableObject
    {
        list.Clear();
        foreach (var item in items)
        {
            Track(item);
            list.Add(item);
        }
    }

    /// <summary>An edit is checked again; a renamed destination is renamed in the workflow lists too.</summary>
    private void Track(ObservableObject item) => item.PropertyChanged += (_, e) =>
    {
        if (e.PropertyName is nameof(TestableViewModel.TestStatus) or nameof(TestableViewModel.TestFailed) or nameof(TestableViewModel.Testing)
            or nameof(SignInViewModel.IsWaiting) or nameof(SignInViewModel.IsAvailable) or nameof(SignInViewModel.IsSignedIn) or nameof(SignInViewModel.IsSignedOut) or nameof(SignInViewModel.AccountText))
        {
            return;
        }

        if (e.PropertyName is "Label")
        {
            ListChanged?.Invoke(this, EventArgs.Empty);
        }

        Edited?.Invoke(this, EventArgs.Empty);
    };

    /// <summary>"server", "server-2", … (never reused within the list, so secrets stay with their destination).</summary>
    private static string NewId(string kind, IEnumerable<string> taken)
    {
        var used = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var id = kind;
        for (var n = 2; used.Contains(id); n++)
        {
            id = $"{kind}-{n}";
        }

        return id;
    }

    private sealed class NoCredentials : ICredentialStore
    {
        public string? Get(string key) => null;

        public void Set(string key, string? value)
        {
        }

        public void RemoveAll(string prefix)
        {
        }
    }
}
