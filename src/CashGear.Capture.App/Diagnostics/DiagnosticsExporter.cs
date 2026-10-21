using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using CashGear.Capture.App.Hosting;
using CashGear.Capture.App.Platform;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Engine.Diagnostics;
using CashGear.Capture.Recording;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace CashGear.Capture.App.Diagnostics;

/// <summary>What an export wrote.</summary>
public sealed record DiagnosticsResult(string Path, IReadOnlyList<string> Entries, long Bytes);

/// <summary>
/// Writes the "Export diagnostics" zip for a bug report: the last week's logs, settings.json and state.json without
/// addresses, names or paths (<see cref="DiagnosticsRedaction"/>), the encoder detection, facts about this PC
/// (<c>system.json</c>) and the recordings waiting to be saved. The user's profile folder is shown as
/// <c>%USERPROFILE%</c> in every file. The passwords' file (<c>secrets.json</c>) is never read. Nothing is sent
/// anywhere: the user decides what to do with the zip.
/// </summary>
public sealed partial class DiagnosticsExporter(
    AppPaths paths,
    IMonitorService monitors,
    ILogger<DiagnosticsExporter> logger,
    IRecordingEncoders? encoders = null,
    IRecordingRecovery? recovery = null,
    IAudioDevices? audio = null)
{
    /// <summary>How far back logs go into the zip.</summary>
    public static readonly TimeSpan LogAge = TimeSpan.FromDays(7);

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The user's profile folder, shown as <c>%USERPROFILE%</c> (tests use another).</summary>
    internal string Profile { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>A name for the zip: <c>CashGear Capture diagnostics 2026-10-21 2015.zip</c>.</summary>
    public static string DefaultFileName(DateTimeOffset now) =>
        string.Create(CultureInfo.InvariantCulture, $"CashGear Capture diagnostics {now:yyyy-MM-dd HHmm}.zip");

    public Task<DiagnosticsResult> ExportAsync(string zipPath, CancellationToken ct = default) =>
        Task.Run(() => Export(zipPath, ct), ct);

    private DiagnosticsResult Export(string zipPath, CancellationToken ct)
    {
        var full = Path.GetFullPath(zipPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var partial = full + ".partial";
        var entries = new List<string>();
        try
        {
            using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                Add(zip, entries, "README.txt", Readme());
                Add(zip, entries, "system.json", SystemReport().ToJsonString(Indented));
                AddRedacted(zip, entries, "settings.json", paths.SettingsFile, DiagnosticsRedaction.Settings);
                AddRedacted(zip, entries, "state.json", Path.Combine(paths.DataDirectory, "state.json"), DiagnosticsRedaction.State);
                AddRedacted(zip, entries, "encoders.json", Path.Combine(paths.DataDirectory, "encoders.json"), json => json);
                Add(zip, entries, "interrupted-recordings.txt", Interrupted());
                ct.ThrowIfCancellationRequested();
                foreach (var log in Logs())
                {
                    ct.ThrowIfCancellationRequested();
                    AddLog(zip, entries, log);
                }
            }

            File.Move(partial, full, overwrite: true);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }

        var bytes = new FileInfo(full).Length;
        LogExported(full, entries.Count, bytes);
        return new DiagnosticsResult(full, entries, bytes);
    }

    /// <summary>This week's log files, newest first (also the older plain-text logs of earlier versions).</summary>
    private IEnumerable<FileInfo> Logs()
    {
        var folder = new DirectoryInfo(paths.LogDirectory);
        if (!folder.Exists)
        {
            return [];
        }

        var since = DateTime.UtcNow - LogAge;
        return folder.EnumerateFiles("capture-*.*")
            .Where(f => f.Extension is ".jsonl" or ".log" && f.LastWriteTimeUtc >= since)
            .OrderByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Copies a log line by line, also today's, which the app (and maybe another copy) is writing to.</summary>
    private void AddLog(ZipArchive zip, List<string> entries, FileInfo log)
    {
        var name = "logs/" + log.Name;
        using var source = new FileStream(log.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(source, Encoding.UTF8);
        using var target = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        while (reader.ReadLine() is { } line)
        {
            target.Write(DiagnosticsRedaction.WithoutProfile(line, Profile));
            target.Write('\n');
        }

        entries.Add(name);
    }

    private void AddRedacted(ZipArchive zip, List<string> entries, string name, string path, Func<string, string> redact)
    {
        string text;
        try
        {
            text = File.Exists(path) ? redact(File.ReadAllText(path)) : "(none)";
        }
        catch (IOException e)
        {
            text = $"(could not be read: {e.Message})";
        }

        Add(zip, entries, name, text);
    }

    private void Add(ZipArchive zip, List<string> entries, string name, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(DiagnosticsRedaction.WithoutProfile(text, Profile));
        entries.Add(name);
    }

    private static string Readme() => string.Create(CultureInfo.InvariantCulture, $"""
        CashGear Capture {BuildInfo.Version}: diagnostics exported {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}, run {LoggingSetup.RunId}.

        logs/                       The app's logs of the last {LogAge.TotalDays:0} days, one JSON object a line (newest file first).
                                    Each line has the run's id ("Run"); a recording's lines have its id ("Recording").
        settings.json               The settings. Server and upload addresses, user names, URLs, header values,
                                    other apps' paths, printers and the update address are left out ("{DiagnosticsRedaction.Removed}").
        state.json                  What the app remembers, without window titles and recent files.
        encoders.json               Which video encoders worked when they were last checked.
        system.json                 Windows, the processor, memory, graphics cards and drivers, monitors, sound devices.
        interrupted-recordings.txt  Recordings that were never saved (names and sizes only).

        Your user folder is shown as %USERPROFILE%. Passwords and tokens (secrets.json) are never included.
        Nothing has been sent anywhere.
        """);

    private string Interrupted()
    {
        if (recovery is null)
        {
            return "(recording isn't available in this copy)";
        }

        var found = recovery.Find();
        if (found.Count == 0)
        {
            return "None.";
        }

        var text = new StringBuilder();
        foreach (var recording in found)
        {
            var manifest = recording.Manifest;
            var size = FolderSize(recording.Directory);
            text.AppendLine(CultureInfo.InvariantCulture,
                $"{Path.GetFileName(recording.Directory)}: started {manifest.StartedAt:yyyy-MM-dd HH:mm:ss}, {manifest.Width}x{manifest.Height} at {manifest.Fps} fps, {manifest.Segments.Count} part(s), {size.Bytes / 1048576.0:0.0} MB, to be saved as \"{Path.GetFileName(manifest.OutputPath)}\"");
        }

        return text.ToString();
    }

    private JsonObject SystemReport()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var gc = GC.GetGCMemoryInfo();
        return new JsonObject
        {
            ["exported"] = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
            ["app"] = new JsonObject
            {
                ["version"] = BuildInfo.Version,
                ["run"] = LoggingSetup.RunId,
                ["processId"] = Environment.ProcessId,
                ["path"] = AppContext.BaseDirectory,
                ["administrator"] = Elevation.IsElevated,
                ["dotnet"] = RuntimeInformation.FrameworkDescription,
                ["startedAt"] = process.StartTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
                ["workingSetMb"] = Megabytes(process.WorkingSet64),
                ["privateMb"] = Megabytes(process.PrivateMemorySize64),
                ["gcHeapMb"] = Megabytes(gc.HeapSizeBytes),
                ["threads"] = process.Threads.Count,
                ["handles"] = process.HandleCount,
            },
            ["windows"] = Windows(),
            ["processor"] = new JsonObject
            {
                ["name"] = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) is string cpu ? cpu.Trim() : null,
                ["logicalProcessors"] = Environment.ProcessorCount,
                ["architecture"] = RuntimeInformation.OSArchitecture.ToString(),
            },
            ["memoryMb"] = Megabytes(gc.TotalAvailableMemoryBytes),
            ["graphics"] = new JsonArray([.. GraphicsAdapters.List().Select(a => (JsonNode)new JsonObject
            {
                ["name"] = a.Name,
                ["vendorId"] = a.VendorId.ToString("X4", CultureInfo.InvariantCulture),
                ["deviceId"] = a.DeviceId.ToString("X4", CultureInfo.InvariantCulture),
                ["memoryMb"] = Megabytes(a.DedicatedMemory),
                ["driver"] = a.DriverVersion,
                ["software"] = a.IsSoftware,
            })]),
            ["monitors"] = Monitors(),
            ["sound"] = Sound(),
            ["recording"] = new JsonObject
            {
                ["ffmpeg"] = encoders is null ? "(not available)" : encoders.Problem ?? encoders.FfmpegVersion,
            },
            ["dataFolders"] = DataFolders(),
        };
    }

    private static JsonObject Windows()
    {
        const string Key = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        return new JsonObject
        {
            ["product"] = Registry.GetValue(Key, "ProductName", null) as string,
            ["release"] = Registry.GetValue(Key, "DisplayVersion", null) as string,
            ["build"] = string.Create(CultureInfo.InvariantCulture, $"{Environment.OSVersion.Version.Build}.{Registry.GetValue(Key, "UBR", 0)}"),
            ["version"] = Environment.OSVersion.VersionString,
            ["remoteSession"] = Interop.Native.User32.GetSystemMetrics(0x1000) != 0, // SM_REMOTESESSION
            ["uiLanguage"] = CultureInfo.CurrentUICulture.Name,
            ["culture"] = CultureInfo.CurrentCulture.Name,
        };
    }

    private JsonArray Monitors()
    {
        var list = new JsonArray();
        foreach (var monitor in monitors.GetMonitors())
        {
            list.Add(new JsonObject
            {
                ["device"] = monitor.DeviceName,
                ["primary"] = monitor.IsPrimary,
                ["bounds"] = monitor.Bounds.ToString(),
                ["workArea"] = monitor.WorkArea.ToString(),
                ["dpi"] = monitor.DpiX,
                ["scale"] = Math.Round(monitor.Scale.ScaleX * 100) + "%",
            });
        }

        return list;
    }

    private JsonObject Sound()
    {
        if (audio is null)
        {
            return new JsonObject { ["available"] = false };
        }

        try
        {
            return new JsonObject
            {
                ["defaultOutput"] = audio.DefaultOutput()?.Name,
                ["outputs"] = new JsonArray([.. audio.Outputs().Select(d => (JsonNode)JsonValue.Create(d.Name))]),
                ["microphones"] = new JsonArray([.. audio.Microphones().Select(d => (JsonNode)JsonValue.Create(d.Name))]),
            };
        }
        catch (Exception e) when (e is COMException or InvalidOperationException)
        {
            return new JsonObject { ["error"] = e.Message };
        }
    }

    /// <summary>How many files and megabytes each of the app's folders holds (history, caches, models, temporary recordings).</summary>
    private JsonObject DataFolders()
    {
        var folders = new JsonObject();
        if (!Directory.Exists(paths.DataDirectory))
        {
            return folders;
        }

        foreach (var folder in Directory.EnumerateDirectories(paths.DataDirectory))
        {
            var size = FolderSize(folder);
            folders[Path.GetFileName(folder)] = string.Create(CultureInfo.InvariantCulture, $"{size.Files} files, {size.Bytes / 1048576.0:0.0} MB");
        }

        return folders;
    }

    private static (int Files, long Bytes) FolderSize(string folder)
    {
        try
        {
            var files = new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
            return (files.Count, files.Sum(f => f.Length));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (0, 0);
        }
    }

    private static double Megabytes(long bytes) => Math.Round(bytes / 1048576.0, 1);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left behind; harmless.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Diagnostics exported to {Path}: {Entries} files, {Bytes} bytes")]
    private partial void LogExported(string path, int entries, long bytes);
}
