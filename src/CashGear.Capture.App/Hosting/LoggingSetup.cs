using System.IO;
using CashGear.Capture.Core.Settings;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace CashGear.Capture.App.Hosting;

internal static class LoggingSetup
{
    private const string Template =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{ProcessId}:{ThreadId}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    public static Logger CreateLogger(AppPaths paths) =>
        new LoggerConfiguration()
#if DEBUG
            .MinimumLevel.Debug()
#else
            .MinimumLevel.Information()
#endif
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.WithProperty("ProcessId", Environment.ProcessId)
            .Enrich.With<ThreadIdEnricher>()
            .WriteTo.File(
                Path.Combine(paths.LogDirectory, "capture-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                shared: true, // a second launch logs its hand-off to the same file
                outputTemplate: Template)
            .CreateLogger();

    private sealed class ThreadIdEnricher : ILogEventEnricher
    {
        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory) =>
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("ThreadId", Environment.CurrentManagedThreadId));
    }
}
