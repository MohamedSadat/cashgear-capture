using CashGear.Capture.App.Hosting;
using CashGear.Capture.App.SingleInstance;
using CashGear.Capture.Core.Instance;
using CashGear.Capture.Core.Settings;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace CashGear.Capture.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var paths = AppPaths.FromEnvironment();
        Log.Logger = LoggingSetup.CreateLogger(paths);
        var log = Log.ForContext(typeof(Program));
        var command = InstanceCommandParser.FromArgs(args);

        try
        {
            using var guard = SingleInstanceGuard.TryAcquire(InstanceNames.MutexName);
            if (!guard.IsPrimary)
            {
                log.Information("Another instance is running; forwarding {Command}", command);
                return InstancePipeClient.TrySend(InstanceNames.PipeName, command, TimeSpan.FromSeconds(3)) ? 0 : 1;
            }

            if (command == InstanceCommand.Exit)
            {
                log.Information("--exit given but no instance is running");
                return 0;
            }

            log.Information(
                "Starting CashGear Capture {Version} (PID {ProcessId}) on {Os}",
                typeof(Program).Assembly.GetName().Version,
                Environment.ProcessId,
                Environment.OSVersion.VersionString);

            // Start the host BEFORE creating the WPF Application: the Application constructor installs a
            // DispatcherSynchronizationContext, and blocking on host startup under it can deadlock.
            using var host = AppHost.Build(args, paths);
            host.Start();

            var app = new CaptureApplication(host.Services, command);
            app.InitializeComponent();
            var exitCode = app.Run();

            SynchronizationContext.SetSynchronizationContext(null);
            host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();

            log.Information("Shutdown complete (exit code {ExitCode})", exitCode);
            return exitCode;
        }
        catch (Exception ex)
        {
            log.Fatal(ex, "Unhandled exception during startup or shutdown");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
