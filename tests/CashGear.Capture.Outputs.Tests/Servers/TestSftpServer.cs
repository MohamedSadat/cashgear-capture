using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using FxSsh;
using FxSsh.Services;

namespace CashGear.Capture.Outputs.Tests.Servers;

/// <summary>
/// An SSH server on 127.0.0.1 (FxSsh) whose "sftp" subsystem serves a folder on disk, for the tests. One user, who
/// logs in with a password or with the RSA key given.
/// </summary>
internal sealed class TestSftpServer : IDisposable
{
    private readonly SshServer _server;

    public TestSftpServer(string root, string user = "tester", string password = "secret", RSA? clientKey = null)
    {
        Root = root;
        Port = Network.FreePort();
        _server = new SshServer(new StartingInfo(IPAddress.Loopback, Port, "SSH-2.0-CashGearTest"));
        _server.AddHostKey("ecdsa-sha2-nistp256", KeyGenerator.GenerateECDsaKeyPem("nistp256"));
        var modulus = clientKey?.ExportParameters(false).Modulus;
        _server.ConnectionAccepted += (_, session) => session.ServiceRegistered += (_, service) =>
        {
            if (service is UserAuthService auth)
            {
                auth.UserAuth += (_, e) =>
                {
                    LastAuthMethod = e.AuthMethod;
                    e.Result = e.Username == user && (e.AuthMethod == "password"
                        ? e.Password == password
                        : modulus is not null && e.Key is { } key && key.AsSpan().IndexOf(modulus) >= 0);
                };
            }
            else if (service is ConnectionService connection)
            {
                connection.CommandOpened += (_, e) =>
                {
                    e.Agreed = e.ShellType == "subsystem" && e.CommandText == "sftp";
                    if (e.Agreed)
                    {
                        var sftp = new SftpSubsystem(Root, data => e.Channel.SendData(data));
                        e.Channel.DataReceived += (_, data) => sftp.OnData(data.Span);
                        e.Channel.CloseReceived += (_, _) => e.Channel.SendClose();
                    }
                };
            }
        };
        _server.Start();
    }

    public string Root { get; }

    public int Port { get; }

    /// <summary>"password" or "publickey": how the last client logged in.</summary>
    public string? LastAuthMethod { get; private set; }

    public void Dispose() => _server.Dispose();

    /// <summary>
    /// SFTP version 3 (draft-ietf-secsh-filexfer-02): enough for SSH.NET to upload a file — the working folder,
    /// file and folder attributes, making folders, writing and removing files.
    /// </summary>
    private sealed class SftpSubsystem(string root, Action<byte[]> send)
    {
        private const byte Init = 1, Version = 2, Open = 3, Close = 4, Write = 6, Lstat = 7, Fstat = 8, SetStat = 9, FSetStat = 10, Remove = 13, MakeDirectory = 14, RealPath = 16, Stat = 17;
        private const byte Status = 101, HandleReply = 102, Name = 104, Attrs = 105;
        private const uint Ok = 0, NoSuchFile = 2, Failure = 4, Unsupported = 8;

        private readonly List<byte> _buffer = [];
        private readonly Dictionary<string, FileStream> _handles = [];
        private int _nextHandle;

        public void OnData(ReadOnlySpan<byte> data)
        {
            _buffer.AddRange(data);
            while (_buffer.Count >= 4)
            {
                var length = BinaryPrimitives.ReadInt32BigEndian(_buffer.Take(4).ToArray());
                if (_buffer.Count < 4 + length)
                {
                    return;
                }

                var packet = _buffer.Skip(4).Take(length).ToArray();
                _buffer.RemoveRange(0, 4 + length);
                Process(new Reader(packet));
            }
        }

        private void Process(Reader request)
        {
            var type = request.Byte();
            if (type == Init)
            {
                Send(Version, w => w.UInt32(3));
                return;
            }

            var id = request.UInt32();
            try
            {
                switch (type)
                {
                    case RealPath:
                    {
                        var path = Normalize(request.String());
                        if (!Exists(path))
                        {
                            StatusReply(id, NoSuchFile, "No such file");
                            return;
                        }

                        Send(Name, w => w.UInt32(id).UInt32(1).String(path).String(path).Attributes(Local(path)));
                        return;
                    }

                    case Stat or Lstat:
                    {
                        var path = Normalize(request.String());
                        if (!Exists(path))
                        {
                            StatusReply(id, NoSuchFile, "No such file");
                            return;
                        }

                        Send(Attrs, w => w.UInt32(id).Attributes(Local(path)));
                        return;
                    }

                    case Fstat:
                    {
                        var file = _handles[request.String()];
                        Send(Attrs, w => w.UInt32(id).Attributes(file.Name));
                        return;
                    }

                    case Open:
                    {
                        var path = Normalize(request.String());
                        var flags = request.UInt32();
                        var mode = (flags & 0x08) != 0 ? ((flags & 0x10) != 0 ? FileMode.Create : FileMode.OpenOrCreate) : FileMode.Open;
                        var stream = new FileStream(Local(path), mode, (flags & 0x02) != 0 ? FileAccess.ReadWrite : FileAccess.Read, FileShare.ReadWrite);
                        var handle = (++_nextHandle).ToString(System.Globalization.CultureInfo.InvariantCulture);
                        _handles[handle] = stream;
                        Send(HandleReply, w => w.UInt32(id).String(handle));
                        return;
                    }

                    case Write:
                    {
                        var file = _handles[request.String()];
                        file.Position = (long)request.UInt64();
                        file.Write(request.Bytes());
                        StatusReply(id, Ok, "OK");
                        return;
                    }

                    case Close:
                    {
                        var handle = request.String();
                        if (_handles.Remove(handle, out var file))
                        {
                            file.Dispose();
                        }

                        StatusReply(id, Ok, "OK");
                        return;
                    }

                    case SetStat or FSetStat:
                        StatusReply(id, Ok, "OK");
                        return;
                    case MakeDirectory:
                    {
                        var path = Normalize(request.String());
                        if (Exists(path) || !Directory.Exists(Path.GetDirectoryName(Local(path))))
                        {
                            StatusReply(id, Failure, "Can't create that folder");
                            return;
                        }

                        Directory.CreateDirectory(Local(path));
                        StatusReply(id, Ok, "OK");
                        return;
                    }

                    case Remove:
                    {
                        var path = Local(Normalize(request.String()));
                        if (!File.Exists(path))
                        {
                            StatusReply(id, NoSuchFile, "No such file");
                            return;
                        }

                        File.Delete(path);
                        StatusReply(id, Ok, "OK");
                        return;
                    }

                    default:
                        StatusReply(id, Unsupported, "Not supported");
                        return;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or KeyNotFoundException)
            {
                StatusReply(id, ex is FileNotFoundException or DirectoryNotFoundException ? NoSuchFile : Failure, ex.Message);
            }
        }

        private void StatusReply(uint id, uint code, string message) => Send(Status, w => w.UInt32(id).UInt32(code).String(message).String("en"));

        private void Send(byte type, Action<Writer> body)
        {
            var writer = new Writer();
            writer.Byte(type);
            body(writer);
            var payload = writer.ToArray();
            var packet = new byte[4 + payload.Length];
            BinaryPrimitives.WriteInt32BigEndian(packet, payload.Length);
            payload.CopyTo(packet, 4);
            send(packet);
        }

        private bool Exists(string path) => File.Exists(Local(path)) || Directory.Exists(Local(path));

        /// <summary>An absolute path: the user's home is the root, "." and ".." resolved.</summary>
        private static string Normalize(string path)
        {
            var parts = new List<string>();
            foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
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

        private string Local(string path) => Path.Combine(root, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
    }

    private sealed class Reader(byte[] data)
    {
        private int _at;

        public byte Byte() => data[_at++];

        public uint UInt32()
        {
            var value = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(_at));
            _at += 4;
            return value;
        }

        public ulong UInt64()
        {
            var value = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(_at));
            _at += 8;
            return value;
        }

        public byte[] Bytes()
        {
            var length = (int)UInt32();
            var value = data.AsSpan(_at, length).ToArray();
            _at += length;
            return value;
        }

        public string String() => Encoding.UTF8.GetString(Bytes());
    }

    private sealed class Writer
    {
        private readonly MemoryStream _stream = new();

        public Writer Byte(byte value)
        {
            _stream.WriteByte(value);
            return this;
        }

        public Writer UInt32(uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            _stream.Write(bytes);
            return this;
        }

        public Writer UInt64(ulong value)
        {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
            _stream.Write(bytes);
            return this;
        }

        public Writer String(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            UInt32((uint)bytes.Length);
            _stream.Write(bytes);
            return this;
        }

        /// <summary>Size, permissions and times of a local file or folder.</summary>
        public Writer Attributes(string local)
        {
            var folder = Directory.Exists(local);
            var info = folder ? (FileSystemInfo)new DirectoryInfo(local) : new FileInfo(local);
            var time = (uint)new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds();
            return UInt32(0x1 | 0x4 | 0x8)
                .UInt64(folder ? 0 : (ulong)((FileInfo)info).Length)
                .UInt32(folder ? 0x41EDu : 0x81A4u) // drwxr-xr-x / -rw-r--r--
                .UInt32(time)
                .UInt32(time);
        }

        public byte[] ToArray() => _stream.ToArray();
    }
}
