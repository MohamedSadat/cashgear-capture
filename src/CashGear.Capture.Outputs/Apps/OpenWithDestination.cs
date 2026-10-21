using System.ComponentModel;
using System.Diagnostics;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.Outputs.Apps;

/// <summary>Starts programs (behind an interface so tests don't start any).</summary>
public interface IProcessLauncher
{
    void Start(string program, string arguments, string workingDirectory);
}

internal sealed class ProcessLauncher : IProcessLauncher
{
    public void Start(string program, string arguments, string workingDirectory) =>
        Process.Start(new ProcessStartInfo(program, arguments) { UseShellExecute = false, WorkingDirectory = workingDirectory })?.Dispose();
}

/// <summary>
/// Opens the capture (or recording) in another program, such as Paint.NET or Photoshop. A capture that wasn't saved is
/// first copied to <paramref name="keepFolder"/>, where it stays for the program to read after the workflow ends
/// (old copies are deleted at the next start).
/// </summary>
public sealed class OpenWithDestination(ExternalApp app, IProcessLauncher launcher, string keepFolder) : IOutputDestination
{
    public string Id => DestinationIds.App(app.Id);

    public string DisplayName => $"Open with {(app.Name.Length > 0 ? app.Name : Path.GetFileNameWithoutExtension(app.Path))}";

    public bool SupportsVideo => true;

    public async Task<OutputResult> SendAsync(OutputItem item, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (Resolve(app.Path) is not { } program)
        {
            return OutputResult.Failed($"{app.Path} isn't there (Settings › Destinations).");
        }

        var file = await item.GetFileAsync(ct).ConfigureAwait(false);
        if (!string.Equals(file, item.SavedPath, StringComparison.OrdinalIgnoreCase))
        {
            var folder = Path.Combine(keepFolder, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var kept = Path.Combine(folder, Path.GetFileName(file));
            File.Copy(file, kept);
            file = kept;
        }

        try
        {
            launcher.Start(program, Arguments(app.Arguments, file), Path.GetDirectoryName(file)!);
        }
        catch (Win32Exception ex)
        {
            return OutputResult.Failed($"{Path.GetFileName(program)} couldn't be started: {ex.Message}");
        }

        return OutputResult.Done($"Opened with {Path.GetFileNameWithoutExtension(program)}");
    }

    /// <summary>Checks the program is there; nothing is started.</summary>
    public Task<OutputResult> TestAsync(CancellationToken ct) => Task.FromResult(Resolve(app.Path) is { } program
        ? OutputResult.Done($"Found {program}. It will be started as: {Path.GetFileName(program)} {Arguments(app.Arguments, @"C:\…\Capture.png")}")
        : OutputResult.Failed(app.Path.Length == 0 ? "Choose the program." : $"{app.Path} isn't there."));

    /// <summary>
    /// The command line with <c>{file}</c> (and <c>{folder}</c>) put in. A path is put in as it is, so the template
    /// decides the quoting (the default is <c>"{file}"</c>); a template without <c>{file}</c> gets the file at the end,
    /// quoted.
    /// </summary>
    public static string Arguments(string template, string file)
    {
        var folder = Path.GetDirectoryName(file) ?? string.Empty;
        if (!template.Contains("{file}", StringComparison.OrdinalIgnoreCase))
        {
            template = template.Trim() + (template.Trim().Length > 0 ? " " : string.Empty) + "\"{file}\"";
        }

        return template
            .Replace("{file}", file, StringComparison.OrdinalIgnoreCase)
            .Replace("{folder}", folder, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The program's full path: as given when it exists, else found on the PATH (e.g. <c>mspaint.exe</c>).</summary>
    public static string? Resolve(string program)
    {
        if (string.IsNullOrWhiteSpace(program))
        {
            return null;
        }

        var expanded = Environment.ExpandEnvironmentVariables(program.Trim());
        if (Path.IsPathRooted(expanded))
        {
            return File.Exists(expanded) ? expanded : null;
        }

        var names = Path.HasExtension(expanded) ? [expanded] : new[] { expanded + ".exe", expanded };
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var name in names)
            {
                try
                {
                    var candidate = Path.Combine(Environment.ExpandEnvironmentVariables(folder), name);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (ArgumentException)
                {
                    // a malformed PATH entry
                }
            }
        }

        return null;
    }
}
