using System.Globalization;
using CashGear.Capture.Core.Settings;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.Core.Output;

/// <summary>The running number behind the <c>{counter}</c> file-name token.</summary>
public interface ICaptureCounter
{
    /// <summary>Returns the next number (1, 2, 3, …) and remembers it across restarts.</summary>
    int Next();
}

/// <summary>Keeps the counter in a small file under <see cref="AppPaths.DataDirectory"/>, not in the user's settings.</summary>
public sealed partial class FileCaptureCounter(AppPaths paths, ILogger<FileCaptureCounter> logger) : ICaptureCounter
{
    private readonly Lock _lock = new();

    private string FilePath => Path.Combine(paths.DataDirectory, "counter.txt");

    public int Next()
    {
        lock (_lock)
        {
            var next = Read() + 1;
            try
            {
                Directory.CreateDirectory(paths.DataDirectory);
                File.WriteAllText(FilePath, next.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogWriteFailed(ex, FilePath); // keep going: a repeated number only costs a " (2)" suffix
            }

            return next;
        }
    }

    private int Read()
    {
        try
        {
            return File.Exists(FilePath)
                && int.TryParse(File.ReadAllText(FilePath).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                && value >= 0
                ? value
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogWriteFailed(ex, FilePath);
            return 0;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read or write the capture counter at {Path}")]
    private partial void LogWriteFailed(Exception ex, string path);
}
