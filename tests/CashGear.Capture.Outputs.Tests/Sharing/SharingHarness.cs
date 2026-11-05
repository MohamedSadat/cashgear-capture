using System.Security.Cryptography;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Outputs.Sharing;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.Outputs.Tests.Sharing;

/// <summary>Follows the sign-in page as a browser does: the service's page, then its redirect to the loopback address.</summary>
internal sealed class FollowingBrowser : ISignInBrowser
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler { AllowAutoRedirect = false });

    public Uri? Opened { get; private set; }

    /// <summary>Runs after the service answered and before the loopback address is visited, as a page poking at ports might.</summary>
    public Func<Uri, Task>? Before { get; set; }

    public void Open(Uri address)
    {
        Opened = address;
        _ = Task.Run(async () =>
        {
            using var page = await Client.GetAsync(address);
            if (page.Headers.Location is { } back)
            {
                if (Before is { } before)
                {
                    await before(back);
                }

                using var done = await Client.GetAsync(back);
            }
        });
    }
}

/// <summary>A browser the user never finishes signing in with.</summary>
internal sealed class NeverBrowser : ISignInBrowser
{
    public void Open(Uri address)
    {
    }
}

/// <summary>The sign-in destinations against a fake server: tokens in memory, shortened waits.</summary>
internal sealed class SharingHarness : IAsyncDisposable
{
    public const string GoogleId = "google-client";
    public const string MicrosoftId = "microsoft-client";
    public const string DropboxId = "dropbox-key";

    private SharingHarness(FakeSharingServer server, ISignInBrowser browser)
    {
        Server = server;
        Browser = browser;
        Http = new SharingHttp();
        Wake = new UploadWake();
        Credentials = new MemoryCredentials();
        OAuth = new OAuthClient(Credentials, Http, browser, NullLogger<OAuthClient>.Instance);
        var timing = new UploadTiming(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5), TimeSpan.FromMilliseconds(50));
        var uploader = new ResumableUploader(Wake, NullLogger<ResumableUploader>.Instance, timing);
        Context = new SharingContext(OAuth, Http, uploader, SharingEndpoints.At(server.Address));
    }

    public FakeSharingServer Server { get; }

    public ISignInBrowser Browser { get; }

    public SharingHttp Http { get; }

    public UploadWake Wake { get; }

    public MemoryCredentials Credentials { get; }

    public OAuthClient OAuth { get; }

    public SharingContext Context { get; }

    public static async Task<SharingHarness> StartAsync(ISignInBrowser? browser = null) =>
        new(await FakeSharingServer.StartAsync(), browser ?? new FollowingBrowser());

    public async ValueTask DisposeAsync()
    {
        await Server.DisposeAsync();
        Http.Dispose();
        Wake.Dispose();
    }

    public SignInDestination YouTube(YouTubeSettings? settings = null) => new YouTubeDestination(Context, settings ?? new YouTubeSettings(), GoogleId, Server.GoogleSecret);

    public SignInDestination Drive(CloudFolderSettings? settings = null) => new GoogleDriveDestination(Context, settings ?? new CloudFolderSettings(), GoogleId, Server.GoogleSecret);

    public SignInDestination OneDrive(CloudFolderSettings? settings = null) => new OneDriveDestination(Context, settings ?? new CloudFolderSettings(), MicrosoftId);

    public SignInDestination Dropbox(CloudFolderSettings? settings = null) => new DropboxDestination(Context, settings ?? new CloudFolderSettings(), DropboxId);

    public SignInDestination Named(string id) => id switch
    {
        DestinationIds.YouTube => YouTube(),
        DestinationIds.GoogleDrive => Drive(),
        DestinationIds.OneDrive => OneDrive(),
        _ => Dropbox(),
    };

    /// <summary>A file of random bytes, with its hash.</summary>
    public static (string Path, byte[] Hash) RandomFile(string folder, string name, int length)
    {
        var bytes = RandomNumberGenerator.GetBytes(length);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, bytes);
        return (path, SHA256.HashData(bytes));
    }
}
