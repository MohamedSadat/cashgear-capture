using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace CashGear.Capture.Outputs.Tests.Servers;

/// <summary>
/// A small FTP server on 127.0.0.1 for the tests: one user, a folder on disk as its root, passive (PASV, EPSV) and
/// active (PORT, EPRT) data connections, and TLS — explicit (AUTH TLS) or implicit — on the control and data
/// connections with a self-signed certificate. Only what an upload needs: no listings, no downloads.
/// </summary>
internal sealed class TestFtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly string _user;
    private readonly string _password;
    private readonly List<Task> _sessions = [];
    private readonly Task _accepting;

    /// <param name="root">The folder the user sees as /.</param>
    /// <param name="certificate">Offer TLS with this certificate (null: plain FTP only).</param>
    /// <param name="implicitTls">TLS from the first byte (FTPS on port 990 style).</param>
    public TestFtpServer(string root, string user = "tester", string password = "secret", X509Certificate2? certificate = null, bool implicitTls = false)
    {
        Root = root;
        _user = user;
        _password = password;
        Certificate = certificate;
        ImplicitTls = implicitTls;
        _listener.Start();
        _accepting = AcceptAsync();
    }

    public string Root { get; }

    public X509Certificate2? Certificate { get; }

    public bool ImplicitTls { get; }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Every command received (passwords hidden), for the tests to check what the client did.</summary>
    public List<string> Commands { get; } = [];

    /// <summary>Whether the last file stored came over an encrypted data connection.</summary>
    public bool LastUploadEncrypted { get; private set; }

    /// <summary>Whether the last data connection was made by the server (active mode).</summary>
    public bool LastUploadActive { get; private set; }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _accepting;
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }

        Task[] sessions;
        lock (_sessions)
        {
            sessions = [.. _sessions];
        }

        await Task.WhenAll(sessions.Select(s => s.ContinueWith(_ => { }, TaskScheduler.Default)));
        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var client = await _listener.AcceptTcpClientAsync(_stop.Token);
            lock (_sessions)
            {
                _sessions.Add(Task.Run(() => new Session(this, client).RunAsync(_stop.Token)));
            }
        }
    }

    private void Log(string command)
    {
        lock (Commands)
        {
            Commands.Add(command.StartsWith("PASS ", StringComparison.OrdinalIgnoreCase) ? "PASS ***" : command);
        }
    }

    private sealed class Session(TestFtpServer server, TcpClient client)
    {
        private Stream _control = client.GetStream();
        private StreamReader? _reader;
        private string _cwd = "/";
        private bool _loggedIn;
        private string? _user;
        private bool _protectData;
        private TcpListener? _passive;
        private IPEndPoint? _active;

        public async Task RunAsync(CancellationToken ct)
        {
            using (client)
            {
                try
                {
                    if (server.ImplicitTls)
                    {
                        await StartTlsAsync(ct);
                        _protectData = true;
                    }

                    _reader = new StreamReader(_control, Encoding.UTF8, false, 1024, leaveOpen: true);
                    await ReplyAsync("220 CashGear test FTP server ready", ct);
                    while (!ct.IsCancellationRequested)
                    {
                        var line = await _reader.ReadLineAsync(ct);
                        if (line is null)
                        {
                            return;
                        }

                        server.Log(line);
                        if (!await HandleAsync(line, ct))
                        {
                            return;
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or System.Security.Authentication.AuthenticationException)
                {
                    // the client went away
                }
                finally
                {
                    _passive?.Stop();
                    await _control.DisposeAsync();
                }
            }
        }

        private async Task<bool> HandleAsync(string line, CancellationToken ct)
        {
            var space = line.IndexOf(' ', StringComparison.Ordinal);
            var verb = (space < 0 ? line : line[..space]).ToUpperInvariant();
            var argument = space < 0 ? string.Empty : line[(space + 1)..];
            if (!_loggedIn && verb is not ("USER" or "PASS" or "AUTH" or "PBSZ" or "PROT" or "FEAT" or "SYST" or "QUIT" or "OPTS" or "NOOP" or "CLNT"))
            {
                await ReplyAsync("530 Please log in", ct);
                return true;
            }

            switch (verb)
            {
                case "AUTH" when server.Certificate is not null && argument.ToUpperInvariant() is "TLS" or "SSL" && !server.ImplicitTls:
                    await ReplyAsync("234 Proceed with TLS", ct);
                    await StartTlsAsync(ct);
                    _reader = new StreamReader(_control, Encoding.UTF8, false, 1024, leaveOpen: true);
                    return true;
                case "AUTH":
                    await ReplyAsync("502 TLS isn't offered here", ct);
                    return true;
                case "PBSZ":
                    await ReplyAsync("200 PBSZ=0", ct);
                    return true;
                case "PROT":
                    _protectData = argument.Trim().Equals("P", StringComparison.OrdinalIgnoreCase);
                    await ReplyAsync("200 Protection level set", ct);
                    return true;
                case "USER":
                    _user = argument;
                    await ReplyAsync("331 Password please", ct);
                    return true;
                case "PASS":
                    _loggedIn = _user == server._user && argument == server._password;
                    await ReplyAsync(_loggedIn ? "230 Logged in" : "530 Login incorrect", ct);
                    return true;
                case "FEAT":
                    await ReplyAsync("211-Features:\r\n EPSV\r\n EPRT\r\n PASV\r\n SIZE\r\n UTF8\r\n" + (server.Certificate is null || server.ImplicitTls ? string.Empty : " AUTH TLS\r\n PBSZ\r\n PROT\r\n") + "211 End", ct);
                    return true;
                case "SYST":
                    await ReplyAsync("215 UNIX Type: L8", ct);
                    return true;
                case "OPTS" or "CLNT" or "NOOP":
                    await ReplyAsync("200 OK", ct);
                    return true;
                case "TYPE":
                    await ReplyAsync("200 Type set", ct);
                    return true;
                case "PWD" or "XPWD":
                    await ReplyAsync($"257 \"{_cwd}\" is the current folder", ct);
                    return true;
                case "CWD":
                {
                    var folder = Resolve(argument);
                    if (Directory.Exists(Local(folder)))
                    {
                        _cwd = folder;
                        await ReplyAsync("250 OK", ct);
                    }
                    else
                    {
                        await ReplyAsync("550 No such folder", ct);
                    }

                    return true;
                }

                case "CDUP":
                    _cwd = Resolve("..");
                    await ReplyAsync("250 OK", ct);
                    return true;
                case "MKD" or "XMKD":
                {
                    var folder = Resolve(argument);
                    if (Directory.Exists(Local(folder)) || !Directory.Exists(Path.GetDirectoryName(Local(folder))))
                    {
                        await ReplyAsync("550 Can't create that folder", ct);
                    }
                    else
                    {
                        Directory.CreateDirectory(Local(folder));
                        await ReplyAsync($"257 \"{folder}\" created", ct);
                    }

                    return true;
                }

                case "SIZE":
                {
                    var file = Local(Resolve(argument));
                    await ReplyAsync(File.Exists(file) ? $"213 {new FileInfo(file).Length}" : "550 No such file", ct);
                    return true;
                }

                case "MDTM":
                {
                    var file = Local(Resolve(argument));
                    await ReplyAsync(File.Exists(file) ? $"213 {File.GetLastWriteTimeUtc(file):yyyyMMddHHmmss}" : "550 No such file", ct);
                    return true;
                }

                case "DELE":
                {
                    var file = Local(Resolve(argument));
                    if (File.Exists(file))
                    {
                        File.Delete(file);
                        await ReplyAsync("250 Deleted", ct);
                    }
                    else
                    {
                        await ReplyAsync("550 No such file", ct);
                    }

                    return true;
                }

                case "PASV":
                {
                    var port = OpenPassive();
                    await ReplyAsync($"227 Entering Passive Mode (127,0,0,1,{port / 256},{port % 256})", ct);
                    return true;
                }

                case "EPSV":
                    await ReplyAsync($"229 Entering Extended Passive Mode (|||{OpenPassive()}|)", ct);
                    return true;
                case "PORT":
                {
                    var n = argument.Split(',').Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
                    _active = new IPEndPoint(new IPAddress([(byte)n[0], (byte)n[1], (byte)n[2], (byte)n[3]]), (n[4] * 256) + n[5]);
                    await ReplyAsync("200 PORT OK", ct);
                    return true;
                }

                case "EPRT":
                {
                    var parts = argument.Split(argument[0]);
                    _active = new IPEndPoint(IPAddress.Parse(parts[2]), int.Parse(parts[3], CultureInfo.InvariantCulture));
                    await ReplyAsync("200 EPRT OK", ct);
                    return true;
                }

                case "STOR":
                    await StoreAsync(Resolve(argument), ct);
                    return true;
                case "QUIT":
                    await ReplyAsync("221 Bye", ct);
                    return false;
                default:
                    await ReplyAsync("502 Not implemented", ct);
                    return true;
            }
        }

        private async Task StoreAsync(string path, CancellationToken ct)
        {
            if (!Directory.Exists(Path.GetDirectoryName(Local(path))))
            {
                await ReplyAsync("553 No such folder", ct);
                return;
            }

            if (_passive is null && _active is null)
            {
                await ReplyAsync("425 Use PASV or PORT first", ct);
                return;
            }

            await ReplyAsync("150 Opening data connection", ct);
            TcpClient data;
            if (_passive is { } passive)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                data = await passive.AcceptTcpClientAsync(timeout.Token);
                passive.Stop();
                _passive = null;
                server.LastUploadActive = false;
            }
            else
            {
                data = new TcpClient(AddressFamily.InterNetwork);
                await data.ConnectAsync(_active!, ct);
                _active = null;
                server.LastUploadActive = true;
            }

            using (data)
            {
                Stream stream = data.GetStream();
                if (_protectData)
                {
                    var tls = new SslStream(stream, leaveInnerStreamOpen: false);
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = server.Certificate }, ct);
                    stream = tls;
                }

                await using (stream)
                await using (var file = new FileStream(Local(path), FileMode.Create, FileAccess.Write))
                {
                    try
                    {
                        await stream.CopyToAsync(file, ct);
                    }
                    catch (IOException) when (_protectData)
                    {
                        // a TLS client that closes without close_notify; what arrived is the file
                    }
                }
            }

            server.LastUploadEncrypted = _protectData;
            await ReplyAsync("226 Transfer complete", ct);
        }

        private int OpenPassive()
        {
            _passive?.Stop();
            _passive = new TcpListener(IPAddress.Loopback, 0);
            _passive.Start();
            _active = null;
            return ((IPEndPoint)_passive.LocalEndpoint).Port;
        }

        private async Task StartTlsAsync(CancellationToken ct)
        {
            var tls = new SslStream(_control, leaveInnerStreamOpen: false);
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = server.Certificate }, ct);
            _control = tls;
        }

        private async Task ReplyAsync(string reply, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(reply + "\r\n");
            await _control.WriteAsync(bytes, ct);
            await _control.FlushAsync(ct);
        }

        /// <summary>An absolute server path for what the client sent (relative to the current folder).</summary>
        private string Resolve(string path)
        {
            var combined = path.StartsWith('/') ? path : _cwd.TrimEnd('/') + "/" + path;
            var parts = new List<string>();
            foreach (var part in combined.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (part == "..")
                {
                    if (parts.Count > 0)
                    {
                        parts.RemoveAt(parts.Count - 1);
                    }
                }
                else if (part != ".")
                {
                    parts.Add(part);
                }
            }

            return "/" + string.Join('/', parts);
        }

        private string Local(string serverPath) => Path.Combine(server.Root, serverPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
    }
}
