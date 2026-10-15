using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.App.Tests.Recording;

/// <summary>
/// Logging for the recorder tests: to the file named by <c>CASHGEAR_TEST_LOG</c> when it is set (to follow what a
/// recording did, step by step), otherwise nowhere.
/// </summary>
internal static class TestLog
{
    public static ILoggerFactory Create() =>
        Environment.GetEnvironmentVariable("CASHGEAR_TEST_LOG") is { Length: > 0 } path
            ? LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(new FileProvider(path)))
            : NullLoggerFactory.Instance;

    private sealed class FileProvider(string path) : ILoggerProvider
    {
        private readonly object _gate = new();

        public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName.Split('.')[^1]);

        public void Dispose()
        {
        }

        public void Write(string line)
        {
            lock (_gate)
            {
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
    }

    private sealed class FileLogger(FileProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            provider.Write($"{DateTime.Now:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId,3}] {logLevel,-11} {category}: {formatter(state, exception)}{(exception is null ? string.Empty : " " + exception)}");
    }
}
