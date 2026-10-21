using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.Outputs;

/// <summary>A place captures (and, where <see cref="SupportsVideo"/>, recordings) can be sent.</summary>
public interface IOutputDestination
{
    /// <summary>The id the workflow uses (see <see cref="DestinationIds"/>).</summary>
    string Id { get; }

    /// <summary>Shown in menus, the workflow and messages.</summary>
    string DisplayName { get; }

    bool SupportsVideo { get; }

    /// <summary>
    /// Sends the item. Problems the user can do something about (a wrong password, a locked file) come back as a
    /// failed result with a message; only cancellation throws.
    /// </summary>
    Task<OutputResult> SendAsync(OutputItem item, CancellationToken ct);

    /// <summary>
    /// Checks the set-up without sending a capture (for Settings' Test button): connects and logs in, checks the
    /// file or program exists, and so on. Never prints, opens another app or leaves anything behind on a server.
    /// </summary>
    Task<OutputResult> TestAsync(CancellationToken ct);
}

public enum OutputKind
{
    Image,
    Video,
}

/// <summary>What a destination made of an item.</summary>
/// <param name="Succeeded">False when it failed (see <paramref name="Message"/>).</param>
/// <param name="Message">One line for the summary: "Uploaded to Office", "Wrong password", …</param>
public sealed record OutputResult(bool Succeeded, string Message)
{
    /// <summary>A web address the item can be seen at (an upload).</summary>
    public string? Link { get; init; }

    /// <summary>A file it was written to (a saved capture, a Word document).</summary>
    public string? Path { get; init; }

    /// <summary>
    /// A certificate or host key that isn't trusted yet (its SHA-256 fingerprint): Test offers to trust it.
    /// </summary>
    public string? Fingerprint { get; init; }

    /// <summary>The step didn't apply (nothing to do), which is neither a success nor a failure worth a warning.</summary>
    public bool Skipped { get; init; }

    public static OutputResult Done(string message) => new(true, message);

    public static OutputResult Failed(string message) => new(false, message);

    public static OutputResult Skip(string message) => new(true, message) { Skipped = true };
}

/// <summary>
/// A capture or a recording on its way through the workflow. For an image, the file the destinations upload or
/// attach is the saved one once the File step has run, else a temporary one in the configured format, made the
/// first time a destination asks and deleted with the item.
/// </summary>
public sealed class OutputItem : IDisposable
{
    private readonly Func<string, CancellationToken, Task<string>>? _makeFile;
    private readonly Func<string>? _makeName;
    private readonly Lock _gate = new();
    private string? _name;
    private Task<string>? _made;

    private OutputItem(OutputKind kind, Func<string>? makeName, string? name, DateTimeOffset capturedAt, string? title, Func<string, CancellationToken, Task<string>>? makeFile)
    {
        Kind = kind;
        _makeName = makeName;
        _name = name;
        CapturedAt = capturedAt;
        Title = title;
        _makeFile = makeFile;
    }

    /// <summary>A capture.</summary>
    /// <param name="image">The capture (it stays the caller's; it must live as long as the item).</param>
    /// <param name="makeName">Makes its file name without extension (from the file-name template), the first time a step needs it, so a <c>{counter}</c> is only used up by captures that get a file.</param>
    /// <param name="makeFile">Writes the capture to a temporary file with the name given and returns its path.</param>
    /// <param name="images">Everything the File step saves (full screen: also each monitor); the capture alone by default.</param>
    public static OutputItem ForImage(CapturedImage image, Func<string> makeName, Func<string, CancellationToken, Task<string>> makeFile, IReadOnlyList<CapturedImage>? images = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(makeName);
        ArgumentNullException.ThrowIfNull(makeFile);
        return new OutputItem(OutputKind.Image, makeName, null, image.Metadata.CapturedAt, image.Metadata.WindowTitle, makeFile)
        {
            Image = image,
            Images = images ?? [image],
        };
    }

    /// <summary>A recording, already saved at <paramref name="path"/>.</summary>
    public static OutputItem ForVideo(string path, DateTimeOffset recordedAt, string? title = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new OutputItem(OutputKind.Video, null, System.IO.Path.GetFileNameWithoutExtension(path), recordedAt, title, null) { SavedPath = path };
    }

    public OutputKind Kind { get; }

    /// <summary>The capture (null for a recording).</summary>
    public CapturedImage? Image { get; private init; }

    /// <summary>What the File step saves.</summary>
    public IReadOnlyList<CapturedImage> Images { get; private init; } = [];

    /// <summary>The file name without extension (made on first use; the File step sets it to the saved file's).</summary>
    public string Name
    {
        get
        {
            lock (_gate)
            {
                return _name ??= _makeName?.Invoke() is { Length: > 0 } made ? made : "Capture";
            }
        }

        set
        {
            lock (_gate)
            {
                _name = value;
            }
        }
    }

    public DateTimeOffset CapturedAt { get; }

    /// <summary>The captured window's title, if any.</summary>
    public string? Title { get; }

    /// <summary>Where the item was saved (by the File step, or the recording itself).</summary>
    public string? SavedPath { get; set; }

    /// <summary>Every file the File step saved (several for one file per monitor).</summary>
    public List<string> SavedFiles { get; } = [];

    /// <summary>The links made so far (uploads), for the Copy link step.</summary>
    public List<string> Links { get; } = [];

    /// <summary>The steps after the editor, which wait for its Continue button (set when the editor step runs).</summary>
    public IReadOnlyList<WorkflowStep> Pending { get; set; } = [];

    /// <summary>The file to send: the saved one when there is one, else a temporary one (made once).</summary>
    public Task<string> GetFileAsync(CancellationToken ct)
    {
        if (SavedPath is { } saved && File.Exists(saved))
        {
            return Task.FromResult(saved);
        }

        if (_makeFile is null)
        {
            throw new FileNotFoundException("The recording isn't there any more.", SavedPath);
        }

        var name = Name;
        lock (_gate)
        {
            return _made ??= _makeFile(name, ct);
        }
    }

    /// <summary>Deletes the temporary file (never a saved one).</summary>
    public void Dispose()
    {
        Task<string>? made;
        lock (_gate)
        {
            made = _made;
            _made = null;
        }

        if (made is not { IsCompletedSuccessfully: true } || made.Result is not { } path || string.Equals(path, SavedPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            File.Delete(path);
            if (System.IO.Path.GetDirectoryName(path) is { } folder && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A destination still has it open (an app it was opened with); the temp folder is cleaned at the next start.
        }
    }
}
