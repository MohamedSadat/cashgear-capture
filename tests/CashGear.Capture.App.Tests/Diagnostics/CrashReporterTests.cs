using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using CashGear.Capture.App.Diagnostics;
using CashGear.Capture.App.Hosting;
using CashGear.Capture.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Serilog.Context;

namespace CashGear.Capture.App.Tests.Diagnostics;

/// <summary>
/// Crash reports go to a fake Sentry on this PC (an HTTP listener on 127.0.0.1 as the DSN's host): nothing leaves
/// the PC. Sentry is one per process, so these tests run one at a time.
/// </summary>
[Collection(CrashReportCollection.Name)]
public sealed class CrashReporterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cashgear-tests", "crash-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeSentry _sentry = new();
    private readonly MutableSettings _settings = new();
    private readonly AppPaths _paths;

    public CrashReporterTests() => _paths = new AppPaths(_root, Path.Combine(_root, "logs"), _root);

    public void Dispose()
    {
        _sentry.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Sentry's cache may still be closing
        }
    }

    private CrashReporter Create(string? dsn) =>
        new(_settings, _paths, NullLogger<CrashReporter>.Instance, dsn, "test");

    [Fact]
    public async Task Nothing_is_sent_until_the_user_agrees()
    {
        using var reporter = Create(_sentry.Dsn);
        reporter.Start();

        reporter.IsAvailable.Should().BeTrue();
        reporter.ShouldAsk.Should().BeTrue("there is an address and no answer yet");
        reporter.IsSending.Should().BeFalse();
        reporter.Capture(new InvalidOperationException("boom"), "ui", fatal: false);

        await reporter.AnswerAsync(false);
        reporter.ShouldAsk.Should().BeFalse();
        reporter.IsSending.Should().BeFalse();
        CrashReporter.Active.Should().BeNull();

        await Task.Delay(500, TestContext.Current.CancellationToken);
        _sentry.Requests.Should().BeEmpty();
    }

    [Fact]
    public void Without_an_address_there_is_nothing_to_ask()
    {
        using var reporter = Create(dsn: null);
        reporter.Start();

        reporter.IsAvailable.Should().BeFalse();
        reporter.ShouldAsk.Should().BeFalse();
        _settings.Current = new AppSettings { CrashReports = new CrashReportSettings(Send: true) };
        reporter.Start();
        reporter.IsSending.Should().BeFalse();
    }

    [Fact]
    public async Task A_crash_is_reported_with_its_ids_and_breadcrumbs_but_no_names_or_typing()
    {
        using var reporter = Create(_sentry.Dsn);
        reporter.Start();
        await reporter.AnswerAsync(true);
        reporter.IsSending.Should().BeTrue("the setting changed");
        CrashReporter.Active.Should().BeSameAs(reporter);

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        using (var logger = LoggingSetup.Configure(new LoggerConfiguration()).WriteTo.Sink(CrashBreadcrumbs.Instance).CreateLogger())
        using (LogContext.PushProperty("Recording", "3fa9c2d1"))
        {
            logger.Information("Captured window {Title}", "Secret Window Title");
            logger.Information("Typed {Keys} into {File}", "hunter2", Path.Combine(profile, "Documents", "plan.txt"));
        }

        reporter.Capture(
            new InvalidOperationException($"Could not open {Path.Combine(profile, "Videos", "secret.mp4")} on {Environment.MachineName}"), "ui", fatal: false);
        reporter.Flush(TimeSpan.FromSeconds(10));

        var body = await _sentry.WaitForAsync(r => r.Contains("\"type\":\"event\"", StringComparison.Ordinal), TimeSpan.FromSeconds(15));
        body.Should().Contain("InvalidOperationException")
            .And.Contain("%USERPROFILE%")
            .And.Contain("\"run_id\":\"" + LoggingSetup.RunId + "\"")
            .And.Contain("\"recording\":\"3fa9c2d1\"")
            .And.Contain("\"where\":\"ui\"")
            .And.Contain("Captured window {Title}")
            .And.Contain("Typed {Keys} into {File}")
            .And.Contain("cashgear-capture@")
            .And.NotContain("timezone");
        body.Should().NotContain("Secret Window Title")
            .And.NotContain("hunter2")
            .And.NotContain("plan.txt")
            .And.NotContain(profile.Replace("\\", "\\\\", StringComparison.Ordinal))
            .And.NotContainEquivalentOf("\"" + Environment.MachineName + "\"")
            .And.NotContainEquivalentOf("\"" + Environment.UserName + "\"");

        _settings.Current = _settings.Current with { CrashReports = _settings.Current.CrashReports with { Send = false } };
        reporter.Start();
        reporter.IsSending.Should().BeFalse("turned off in Settings");
        CrashReporter.Active.Should().BeNull();
    }

    [Fact]
    public void An_address_in_settings_wins_and_a_bad_one_turns_reports_off()
    {
        _settings.Current = new AppSettings { CrashReports = new CrashReportSettings(Send: true, Dsn: "not a dsn") };
        using var reporter = Create(_sentry.Dsn);

        reporter.Dsn.Should().Be("not a dsn");
        reporter.Start();
        reporter.IsSending.Should().BeFalse();
    }

    [Fact]
    public void A_reset_or_an_import_keeps_this_PCs_answer()
    {
        var mine = new AppSettings { CrashReports = new CrashReportSettings(Send: true) };

        SettingsTransfer.KeepLocal(new AppSettings(), mine, keepLanguage: true).CrashReports.Send.Should().BeTrue();
        SettingsTransfer.KeepLocal(new AppSettings { CrashReports = new CrashReportSettings(Send: false) }, mine, keepLanguage: false)
            .CrashReports.Send.Should().BeTrue();
    }

    /// <summary>Takes Sentry's envelopes on 127.0.0.1 and keeps their text (unzipped).</summary>
    private sealed class FakeSentry : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _serving;

        public FakeSentry()
        {
            using var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            var port = ((IPEndPoint)socket.LocalEndpoint).Port;
            socket.Stop();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            Dsn = $"http://0123456789abcdef0123456789abcdef@127.0.0.1:{port}/1";
            _serving = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync();
                    }
                    catch (Exception e) when (e is HttpListenerException or ObjectDisposedException)
                    {
                        return;
                    }

                    using var buffer = new MemoryStream();
                    var input = context.Request.Headers["Content-Encoding"] is { } encoding && encoding.Contains("gzip", StringComparison.OrdinalIgnoreCase)
                        ? new GZipStream(context.Request.InputStream, CompressionMode.Decompress)
                        : context.Request.InputStream;
                    await input.CopyToAsync(buffer);
                    Requests.Add(Encoding.UTF8.GetString(buffer.ToArray()));
                    context.Response.StatusCode = 200;
                    context.Response.Close();
                }
            });
        }

        public string Dsn { get; }

        public ConcurrentBag<string> Requests { get; } = [];

        public async Task<string> WaitForAsync(Func<string, bool> match, TimeSpan timeout)
        {
            var until = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < until)
            {
                if (Requests.FirstOrDefault(match) is { } found)
                {
                    return found;
                }

                await Task.Delay(100);
            }

            throw new TimeoutException("No crash report arrived; got " + Requests.Count + " other requests.");
        }

        public void Dispose()
        {
            _listener.Close();
            _serving.Wait(TimeSpan.FromSeconds(5));
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CrashReportCollection
{
    public const string Name = "Crash reports";
}
