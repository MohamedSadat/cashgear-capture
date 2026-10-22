using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;

// Checks that an update keeps a user's settings and recordings, with the real packages and Velopack's real updater,
// on portable copies in a folder of their own (nothing is installed, no shortcuts or registry entries are made, and
// an installed CashGear Capture keeps running: a portable copy is an instance of its own).
//
//   UpdateCheck [--work <folder>] [--old 1.1.0-beta.1] [--new 1.1.0] [--previous <1.0.1 portable zip>] [--skip-packaging]
//
// 1. Packages --old on the beta channel and --new on the stable channel (tools\package.ps1 -Fast -NoRnnoise) into
//    <work>\out, which becomes the update feed.
// 2. Beta to stable: extracts the old portable zip, gives it settings (no hotkeys, so nothing clashes with a running
//    copy; a theme, a history size, the stable channel) and a recording, starts it, checks its version, exits it,
//    updates it from the feed on the stable channel and starts the new version: the version is new, the settings,
//    the recording and the rest of Data are as they were, and the copy now follows the stable channel.
// 3. With --previous (the 1.0.1 portable zip from artifacts\releases): the 1.0.x feed (releases.win.json) offers the
//    new stable release to a 1.0.1 copy, which updates to it and then follows the stable channel. 1.0.1 isn't started
//    (it would use the user's own AppData).
//
// Exit code 0 when every check passes; the report is written to <work>\update-check.report.txt.

var options = Args.Parse(args);
var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
while (!File.Exists(Path.Combine(root, "CashGear.Capture.sln")))
{
    root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Run from the repository.");
}

var work = Path.GetFullPath(options.Get("work") ?? Path.Combine(root, "artifacts", "update-check"));
var oldVersion = options.Get("old") ?? "1.1.0-beta.1";
var newVersion = options.Get("new") ?? "1.1.0";
var report = new Report(Path.Combine(work, "update-check.report.txt"));
var output = Path.Combine(work, "out");
var feed = Path.Combine(output, "releases");

try
{
    if (!options.Has("skip-packaging"))
    {
        if (Directory.Exists(output))
        {
            Directory.Delete(output, recursive: true);
        }

        Package(oldVersion, "beta");
        Package(newVersion, "stable");
    }

    report.Line($"Feed {feed}: " + string.Join(", ", Directory.GetFiles(feed).Select(Path.GetFileName)));
    BetaToStable();
    if (options.Get("previous") is { } previous)
    {
        From101(previous);
    }
}
catch (Exception ex)
{
    report.Fail("stopped: " + ex);
}

report.Line(report.Failures == 0 ? "ALL CHECKS PASSED" : $"{report.Failures} CHECK(S) FAILED");
report.Save();
return report.Failures == 0 ? 0 : 1;

void Package(string version, string channel)
{
    report.Line($"Packaging {version} ({channel})…");
    var package = Run("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(root, "tools", "package.ps1"),
        "-Version", version, "-Channel", channel, "-Fast", "-NoRnnoise", "-OutputDir", output], root, TimeSpan.FromMinutes(20));
    report.Check(package == 0, $"tools\\package.ps1 {version} {channel} exit {package}");
}

void BetaToStable()
{
    var app = Path.Combine(work, "beta-to-stable");
    var captures = Path.Combine(work, "captures");
    Fresh(app);
    Directory.CreateDirectory(captures);
    ZipFile.ExtractToDirectory(Path.Combine(feed, "CashGear.Capture-beta-Portable.zip"), app);
    report.Check(File.Exists(Path.Combine(app, ".portable")), "the portable zip has its .portable marker");

    var data = Path.Combine(app, "Data");
    Directory.CreateDirectory(data);
    var settings = new JsonObject
    {
        ["appearance"] = new JsonObject { ["theme"] = "dark" },
        ["history"] = new JsonObject { ["maxItems"] = 42 },
        ["output"] = new JsonObject { ["folder"] = captures },
        ["updates"] = new JsonObject { ["checkAutomatically"] = false, ["channel"] = "stable" },
        ["hotkeys"] = NoHotkeys(),
        ["crashReports"] = new JsonObject { ["send"] = false },
    };
    File.WriteAllText(Path.Combine(data, "settings.json"), settings.ToJsonString());
    var recording = Path.Combine(captures, "My recording.mp4");
    File.WriteAllBytes(recording, RandomNumberGenerator.GetBytes(256 * 1024));
    var recordingHash = Hash(recording);

    var exe = Path.Combine(app, "current", "CashGear.Capture.App.exe");
    StartAndExit(exe, data, oldVersion, "the old (beta) copy");
    var keptSettings = File.ReadAllText(Path.Combine(data, "settings.json"));
    report.Check(keptSettings.Contains("\"maxItems\": 42", StringComparison.Ordinal), "the old copy kept its settings (in Data, next to it)");
    var dataBefore = Snapshot(data, except: "logs");

    var update = Update(app, "stable");
    report.Check(update == newVersion, $"the stable channel offered {update} (expected {newVersion})");
    report.Check(Manifest(app).Contains($"<version>{newVersion}</version>", StringComparison.Ordinal), $"current\\sq.version is {newVersion}");
    report.Check(Manifest(app).Contains("<channel>stable</channel>", StringComparison.Ordinal), "the copy follows the stable channel now");

    StartAndExit(exe, data, newVersion, "the updated copy");
    var dataAfter = Snapshot(data, except: "logs");
    foreach (var file in dataBefore.Keys)
    {
        report.Check(dataAfter.ContainsKey(file), $"Data\\{file} kept");
    }

    var settingsAfter = JsonNode.Parse(File.ReadAllText(Path.Combine(data, "settings.json")))!;
    report.Check(settingsAfter["appearance"]?["theme"]?.GetValue<string>() is "dark" or "Dark", "the theme survived the update");
    report.Check(settingsAfter["history"]?["maxItems"]?.GetValue<int>() == 42, "the history size survived the update");
    report.Check(string.Equals(settingsAfter["output"]?["folder"]?.GetValue<string>(), captures, StringComparison.OrdinalIgnoreCase), "the capture folder survived the update");
    report.Check(File.Exists(recording) && Hash(recording) == recordingHash, "the recording is untouched");
}

void From101(string previousZip)
{
    var app = Path.Combine(work, "from-1.0.1");
    Fresh(app);
    ZipFile.ExtractToDirectory(previousZip, app);
    report.Check(Manifest(app).Contains("<version>1.0.1</version>", StringComparison.Ordinal), "the previous copy is 1.0.1");

    var update = Update(app, channel: null);
    report.Check(update == newVersion, $"1.0.1 (the \"win\" channel) was offered {update} (expected {newVersion})");
    report.Check(Manifest(app).Contains($"<version>{newVersion}</version>", StringComparison.Ordinal), $"1.0.1 updated to {newVersion}");
    report.Check(Manifest(app).Contains("<channel>stable</channel>", StringComparison.Ordinal), "after the update it follows the stable channel");
}

// Checks the feed for the copy in <app> and installs what it offers with Velopack's Update.exe; returns the version.
string? Update(string app, string? channel)
{
    var process = new PortableCopy(Path.Combine(app, "current", "CashGear.Capture.App.exe"));
    var locator = new WindowsVelopackLocator(process, null);
    var manager = new UpdateManager(feed, new UpdateOptions { ExplicitChannel = channel, AllowVersionDowngrade = false }, locator);
    var found = manager.CheckForUpdatesAsync().GetAwaiter().GetResult();
    if (found is null)
    {
        return null;
    }

    report.Line($"  {app}: found {found.TargetFullRelease.Version} (downgrade: {found.IsDowngrade}), downloading");
    manager.DownloadUpdatesAsync(found).GetAwaiter().GetResult();
    UpdateExe.Apply(locator, found.TargetFullRelease, silent: true, waitPid: 0, restart: false);
    var updater = process.Started.LastOrDefault() ?? throw new InvalidOperationException("Update.exe wasn't started.");
    report.Check(updater.WaitForExit(TimeSpan.FromMinutes(5)) && updater.ExitCode == 0, $"Update.exe applied the update (exit {(updater.HasExited ? updater.ExitCode : -1)})");
    return found.TargetFullRelease.Version.ToString();
}

// Starts the copy (its own instance), waits until it is ready, checks the version it logs, and exits it.
void StartAndExit(string exe, string data, string version, string what)
{
    var ready = "CashGearUpdateCheck-" + Guid.NewGuid().ToString("N")[..8];
    using var signal = new EventWaitHandle(false, EventResetMode.ManualReset, ready);
    var start = new ProcessStartInfo(exe) { UseShellExecute = false };
    start.Environment["CASHGEAR_READY_EVENT"] = ready;
    using var app = Process.Start(start)!;
    report.Check(signal.WaitOne(TimeSpan.FromSeconds(60)), $"{what} started and became ready");

    var log = Directory.GetFiles(Path.Combine(data, "logs"), "capture-*.jsonl").OrderDescending().FirstOrDefault();
    var text = log is null ? string.Empty : ReadShared(log);
    report.Check(text.Contains($"Starting CashGear Capture {{Version}}", StringComparison.Ordinal) && text.Contains($"\"Version\":\"{version}\"", StringComparison.Ordinal),
        $"{what} logged version {version} in its own Data\\logs");

    var exit = Run(exe, ["--exit"], Path.GetDirectoryName(exe)!, TimeSpan.FromSeconds(30));
    report.Check(app.WaitForExit(TimeSpan.FromSeconds(30)), $"{what} exited on --exit (hand-off exit {exit})");
}

int Run(string file, string[] arguments, string folder, TimeSpan timeout)
{
    var start = new ProcessStartInfo(file) { UseShellExecute = false, WorkingDirectory = folder };
    foreach (var argument in arguments)
    {
        start.ArgumentList.Add(argument);
    }

    using var process = Process.Start(start)!;
    if (!process.WaitForExit(timeout))
    {
        process.Kill(entireProcessTree: true);
        return -1;
    }

    return process.ExitCode;
}

static JsonObject NoHotkeys()
{
    var hotkeys = new JsonObject();
    foreach (var name in new[]
    {
        "captureRegion", "captureActiveWindow", "captureFullScreen", "captureScrolling", "record", "captureText", "toggleDrawing", "repeatLastCapture",
        "captureWindow", "captureFixedRegion", "captureFreehand", "capturePolygon", "captureRoundedRegion", "captureObject", "captureMenu", "toggleToolbar",
        "magnifier", "colorPicker", "ruler", "crosshair", "useToolbar",
    })
    {
        hotkeys[name] = string.Empty;
    }

    return hotkeys;
}

static void Fresh(string folder)
{
    if (Directory.Exists(folder))
    {
        Directory.Delete(folder, recursive: true);
    }

    Directory.CreateDirectory(folder);
}

static string Manifest(string app) => File.ReadAllText(Path.Combine(app, "current", "sq.version"));

static string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));

static Dictionary<string, string> Snapshot(string folder, string except) =>
    Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
        .Select(f => Path.GetRelativePath(folder, f))
        .Where(f => !f.StartsWith(except + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        .ToDictionary(f => f, f => Hash(Path.Combine(folder, f)), StringComparer.OrdinalIgnoreCase);

static string ReadShared(string file)
{
    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    return new StreamReader(stream).ReadToEnd();
}

/// <summary>What Velopack sees as "this process": the portable copy's app, so it updates that folder.</summary>
internal sealed class PortableCopy(string exe) : IProcessImpl
{
    public List<Process> Started { get; } = [];

    public string GetCurrentProcessPath() => exe;

    public uint GetCurrentProcessId() => (uint)Environment.ProcessId;

    public void StartProcess(string exePath, IEnumerable<string> args, string workDir, bool showWindow)
    {
        var start = new ProcessStartInfo(exePath) { UseShellExecute = false, WorkingDirectory = workDir, CreateNoWindow = !showWindow };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        Started.Add(Process.Start(start)!);
    }

    public void Exit(int exitCode)
    {
        // Velopack asks the app to exit after starting the updater; this check keeps running and waits for it instead.
    }
}

internal sealed class Report(string path)
{
    private readonly List<string> _lines = [];

    public int Failures { get; private set; }

    public void Line(string text)
    {
        Console.WriteLine(text);
        _lines.Add(text);
    }

    public void Check(bool ok, string what)
    {
        Line((ok ? "  pass  " : "  FAIL  ") + what);
        if (!ok)
        {
            Failures++;
        }
    }

    public void Fail(string what) => Check(false, what);

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, _lines);
    }
}

internal sealed class Args
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

    public static Args Parse(string[] args)
    {
        var parsed = new Args();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                var name = args[i][2..];
                parsed._values[name] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : null;
            }
        }

        return parsed;
    }

    public string? Get(string name) => _values.TryGetValue(name, out var value) ? value : null;

    public bool Has(string name) => _values.ContainsKey(name);
}
