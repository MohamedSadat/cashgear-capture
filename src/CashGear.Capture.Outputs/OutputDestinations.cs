using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Outputs.Apps;
using CashGear.Capture.Outputs.Credentials;
using CashGear.Capture.Outputs.Http;
using CashGear.Capture.Outputs.Office;
using CashGear.Capture.Outputs.Pdf;
using CashGear.Capture.Outputs.Servers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.Outputs;

/// <summary>Word, PowerPoint, PDF, Copy the link, and every configured server, upload and app.</summary>
public sealed class ConfiguredDestinations(AppPaths paths, IOutputService output, UploadHttpClient http, IProcessLauncher launcher) : IDestinationSource
{
    public IEnumerable<IOutputDestination> Create(AppSettings settings, ICredentialStore credentials)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var destinations = settings.Destinations;
        var folder = paths.ResolveCaptureDirectory(settings.Output);
        yield return new WordDestination(destinations.Word ?? new OfficeSettings(), folder);
        yield return new PowerPointDestination(destinations.PowerPoint ?? new OfficeSettings(), folder);
        yield return new PdfDestination(destinations.Pdf ?? new PdfSettings(), folder);
        yield return new CopyLinkDestination(output);

        foreach (var server in destinations.Servers ?? [])
        {
            yield return server.Protocol == ServerProtocol.Sftp
                ? new SftpDestination(server, credentials)
                : new FtpDestination(server, credentials);
        }

        foreach (var upload in destinations.Uploads ?? [])
        {
            yield return new HttpUploadDestination(upload, credentials, http);
        }

        foreach (var app in destinations.Apps ?? [])
        {
            yield return new OpenWithDestination(app, launcher, OutgoingFiles.OpenedFolder(paths));
        }
    }
}

/// <summary>Where captures wait while they are sent: temporary files, and copies opened in other apps.</summary>
public static class OutgoingFiles
{
    /// <summary>Temporary files for uploads and documents (deleted after each workflow).</summary>
    public static string Folder(AppPaths paths) => Path.Combine(paths.DataDirectory, "outgoing");

    /// <summary>Copies of unsaved captures opened in other apps (kept a day).</summary>
    public static string OpenedFolder(AppPaths paths) => Path.Combine(Folder(paths), "opened");

    /// <summary>Deletes what is left from before <paramref name="olderThan"/> (a crash, or apps' copies). Never throws.</summary>
    public static void Clean(AppPaths paths, TimeSpan olderThan, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var root = Folder(paths);
        if (!Directory.Exists(root))
        {
            return;
        }

        var before = time.GetUtcNow().UtcDateTime - olderThan;
        foreach (var folder in Directory.EnumerateDirectories(root).Concat(Directory.Exists(OpenedFolder(paths)) ? Directory.EnumerateDirectories(OpenedFolder(paths)) : []).ToList())
        {
            if (string.Equals(folder, OpenedFolder(paths), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (Directory.GetLastWriteTimeUtc(folder) < before)
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // still open somewhere; next time
            }
        }
    }
}

public static class OutputsServiceCollectionExtensions
{
    /// <summary>
    /// The registry, the workflow runner, DPAPI credentials and the destinations that need no WPF. The app adds
    /// its own <see cref="IDestinationSource"/> (editor, file, clipboard, printer) and an <see cref="IOutputService"/>.
    /// </summary>
    public static IServiceCollection AddOutputDestinations(this IServiceCollection services)
    {
        services.TryAddSingleton<ICredentialStore>(sp => new DpapiCredentialStore(sp.GetRequiredService<AppPaths>(), sp.GetRequiredService<ILogger<DpapiCredentialStore>>()));
        services.TryAddSingleton<UploadHttpClient>();
        services.TryAddSingleton<IProcessLauncher, ProcessLauncher>();
        services.AddSingleton<IDestinationSource, ConfiguredDestinations>();
        services.TryAddSingleton<DestinationRegistry>();
        services.TryAddSingleton<WorkflowRunner>();
        return services;
    }
}
