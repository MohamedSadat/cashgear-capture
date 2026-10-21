using System.IO;
using System.IO.Compression;
using System.Text.Json;
using CashGear.Capture.App.Diagnostics;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.App.Tests.Diagnostics;

public sealed class DiagnosticsExporterTests : IDisposable
{
    // Values that must never reach the zip.
    private const string Host = "files.secret-host.example";
    private const string User = "alice-the-user";
    private const string UploadUrl = "https://upload.secret-host.example/api?key=abc123";
    private const string HeaderValue = "Bearer tok-0123456789";
    private const string FieldValue = "field-secret-42";
    private const string Fingerprint = "SHA256:fingerprint-of-the-server";
    private const string Password = "P@ssw0rd-in-secrets";
    private const string WindowTitle = "Quarterly salaries - Excel";
    private const string Project = "My secret project.cgcap";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cashgear-tests", Guid.NewGuid().ToString("N"));
    private readonly string _profile;
    private readonly AppPaths _paths;

    public DiagnosticsExporterTests()
    {
        _profile = Path.Combine(_root, "Users", "tester");
        _paths = new AppPaths(Path.Combine(_root, "Roaming"), Path.Combine(_root, "Local", "logs"), Path.Combine(_profile, "Pictures"));
        Directory.CreateDirectory(_paths.SettingsDirectory);
        Directory.CreateDirectory(_paths.LogDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task The_zip_holds_the_logs_settings_and_facts_without_secrets_or_the_user_folder()
    {
        var escapedProfile = _profile.Replace("\\", "\\\\", StringComparison.Ordinal);
        File.WriteAllText(_paths.SettingsFile, $$"""
            {
              // comments are allowed in the settings file
              "schemaVersion": 5,
              "output": { "folder": "{{escapedProfile}}\\Pictures\\Shots" },
              "destinations": {
                "servers": [ { "id": "s1", "name": "Office NAS", "protocol": "sftp", "host": "{{Host}}", "port": 2222, "userName": "{{User}}",
                               "remoteFolder": "/home/{{User}}/shots", "publicUrl": "https://{{Host}}/{file}", "trustedFingerprint": "{{Fingerprint}}",
                               "keyFile": "{{escapedProfile}}\\.ssh\\id_rsa" } ],
                "uploads": [ { "id": "u1", "name": "My host", "url": "{{UploadUrl}}", "fileField": "file",
                               "fields": [ { "name": "album", "value": "{{FieldValue}}" } ],
                               "headers": [ { "name": "Authorization", "value": "{{HeaderValue}}" } ], "authHeader": "X-Api-Key", "linkPath": "data.link" } ],
                "apps": [ { "id": "a1", "name": "Paint.NET", "path": "{{escapedProfile}}\\Apps\\paint.exe", "arguments": "\"{file}\"" } ],
                "word": { "mode": "appendToFile", "appendPath": "{{escapedProfile}}\\Documents\\Report.docx" },
                "print": { "printer": "Accounting printer" }
              },
              "updates": { "feedUrl": "https://updates.secret-host.example/feed" },
              "crashReports": { "send": true, "dsn": "https://abc@crash-reports.secret-host.example/7" },
              "recording": { "fps": 30 },
            }
            """);
        File.WriteAllText(Path.Combine(_paths.DataDirectory, "secrets.json"), $$"""{ "server:s1:password": "{{Password}}" }""");
        File.WriteAllText(Path.Combine(_paths.DataDirectory, "state.json"), $$"""
            { "lastCapture": { "mode": "window", "bounds": { "x": 1, "y": 2, "width": 3, "height": 4 }, "windowTitle": "{{WindowTitle}}" },
              "videoEditor": { "recentProjects": [ "{{escapedProfile}}\\Videos\\{{Project}}" ], "lastRecording": "{{escapedProfile}}\\Videos\\a.mp4", "exportPreset": "mp4-1080p" } }
            """);
        File.WriteAllText(Path.Combine(_paths.DataDirectory, "encoders.json"), """{ "key": "ffmpeg 9.0", "encoders": [ { "id": "h264_mf", "works": true } ] }""");

        // Today's log is held open for writing, as the running app holds it; an old one is left out.
        var today = Path.Combine(_paths.LogDirectory, "capture-20261021.jsonl");
        using var writing = new FileStream(today, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var line = $$"""{"@t":"2026-10-21T20:00:00.000+03:00","@l":"Information","@m":"Saved {{escapedProfile}}\\Pictures\\a.png","Run":"0a1b2c3d"}""" + "\n";
        writing.Write(System.Text.Encoding.UTF8.GetBytes(line));
        writing.Flush();
        var older = Path.Combine(_paths.LogDirectory, "capture-20261018.log");
        File.WriteAllText(older, $"2026-10-18 10:00:00.000 +03:00 [INF] Saved {_profile}\\Pictures\\b.png\n");
        var old = Path.Combine(_paths.LogDirectory, "capture-20261001.jsonl");
        File.WriteAllText(old, "{}\n");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-20));

        var exporter = new DiagnosticsExporter(_paths, new OneMonitor(), NullLogger<DiagnosticsExporter>.Instance) { Profile = _profile };
        var zipPath = Path.Combine(_root, "out", DiagnosticsExporter.DefaultFileName(new DateTimeOffset(2026, 10, 21, 20, 15, 0, TimeSpan.FromHours(3))));
        var result = await exporter.ExportAsync(zipPath, TestContext.Current.CancellationToken);

        Path.GetFileName(result.Path).Should().Be("CashGear Capture diagnostics 2026-10-21 2015.zip");
        File.Exists(zipPath + ".partial").Should().BeFalse();
        using var zip = ZipFile.OpenRead(result.Path);
        zip.Entries.Select(e => e.FullName).Should().BeEquivalentTo(
            "README.txt", "system.json", "settings.json", "state.json", "encoders.json", "interrupted-recordings.txt",
            "logs/capture-20261021.jsonl", "logs/capture-20261018.log");
        result.Entries.Should().HaveCount(8);

        var texts = zip.Entries.ToDictionary(e => e.FullName, e => new StreamReader(e.Open()).ReadToEnd());
        foreach (var (name, text) in texts)
        {
            foreach (var secret in new[] { Host, User, UploadUrl, HeaderValue, FieldValue, Fingerprint, Password, WindowTitle, Project, "Accounting printer", "updates.secret-host", "crash-reports.secret-host" })
            {
                text.Should().NotContain(secret, $"{name} must not carry it");
            }

            text.Should().NotContain(_profile, $"{name} shows the user folder as %USERPROFILE%")
                .And.NotContain(escapedProfile).And.NotContainEquivalentOf(_profile.Replace('\\', '/'));
        }

        // The settings stay readable JSON, with what was set still showing.
        using var settings = JsonDocument.Parse(texts["settings.json"]);
        var server = settings.RootElement.GetProperty("destinations").GetProperty("servers")[0];
        server.GetProperty("host").GetString().Should().Be(DiagnosticsRedaction.Removed);
        server.GetProperty("port").GetInt32().Should().Be(2222);
        server.GetProperty("protocol").GetString().Should().Be("sftp");
        var upload = settings.RootElement.GetProperty("destinations").GetProperty("uploads")[0];
        upload.GetProperty("headers")[0].GetProperty("name").GetString().Should().Be("Authorization");
        upload.GetProperty("headers")[0].GetProperty("value").GetString().Should().Be(DiagnosticsRedaction.Removed);
        upload.GetProperty("linkPath").GetString().Should().Be("data.link");
        settings.RootElement.GetProperty("output").GetProperty("folder").GetString().Should().Be("%USERPROFILE%\\Pictures\\Shots");
        settings.RootElement.GetProperty("recording").GetProperty("fps").GetInt32().Should().Be(30);

        using var state = JsonDocument.Parse(texts["state.json"]);
        state.RootElement.GetProperty("videoEditor").GetProperty("exportPreset").GetString().Should().Be("mp4-1080p");
        state.RootElement.GetProperty("lastCapture").GetProperty("mode").GetString().Should().Be("window");

        texts["logs/capture-20261021.jsonl"].Should().Be(line.Replace(escapedProfile, "%USERPROFILE%", StringComparison.Ordinal));
        texts["logs/capture-20261018.log"].Should().Contain("Saved %USERPROFILE%\\Pictures\\b.png");
        texts["encoders.json"].Should().Contain("h264_mf");
        texts["interrupted-recordings.txt"].Should().Contain("isn't available");

        using var system = JsonDocument.Parse(texts["system.json"]);
        system.RootElement.GetProperty("monitors")[0].GetProperty("device").GetString().Should().Be(@"\\.\DISPLAY9");
        system.RootElement.GetProperty("processor").GetProperty("logicalProcessors").GetInt32().Should().Be(Environment.ProcessorCount);
        system.RootElement.GetProperty("windows").GetProperty("build").GetString().Should().StartWith(Environment.OSVersion.Version.Build + ".");
        system.RootElement.GetProperty("graphics").GetArrayLength().Should().BeGreaterThan(0, "DXGI lists at least the Basic Render Driver");
        texts["README.txt"].Should().Contain("never included");
    }

    [Fact]
    public void A_settings_file_that_is_not_json_is_left_out_rather_than_copied()
    {
        DiagnosticsRedaction.Settings("{ \"host\": \"" + Host + "\", ").Should().StartWith("(not readable as JSON").And.NotContain(Host);
    }

    [Fact]
    public void Empty_values_stay_empty_so_unused_settings_show()
    {
        var json = DiagnosticsRedaction.Settings("""{ "destinations": { "servers": [ { "host": "", "userName": null } ] } }""");

        using var document = JsonDocument.Parse(json);
        var server = document.RootElement.GetProperty("destinations").GetProperty("servers")[0];
        server.GetProperty("host").GetString().Should().BeEmpty();
        server.GetProperty("userName").ValueKind.Should().Be(JsonValueKind.Null);
    }

    private sealed class OneMonitor : IMonitorService
    {
        public IReadOnlyList<MonitorInfo> GetMonitors() =>
            [new MonitorInfo(1, @"\\.\DISPLAY9", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 144, 144, IsPrimary: true)];
    }
}
