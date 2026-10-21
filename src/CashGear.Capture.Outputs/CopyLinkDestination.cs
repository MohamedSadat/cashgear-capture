using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.Outputs;

/// <summary>Copies the links the earlier steps made (uploads) to the clipboard, one per line.</summary>
public sealed class CopyLinkDestination(IOutputService output) : IOutputDestination
{
    public string Id => DestinationIds.CopyLink;

    public string DisplayName => "Copy the link";

    public bool SupportsVideo => true;

    public async Task<OutputResult> SendAsync(OutputItem item, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Links.Count == 0)
        {
            return OutputResult.Skip("No link to copy (put an upload before Copy the link).");
        }

        await output.CopyTextToClipboardAsync(string.Join(Environment.NewLine, item.Links), ct).ConfigureAwait(false);
        return OutputResult.Done(item.Links.Count == 1 ? "Link copied to the clipboard" : $"{item.Links.Count} links copied to the clipboard");
    }

    public Task<OutputResult> TestAsync(CancellationToken ct) => Task.FromResult(OutputResult.Done("Nothing to set up."));
}
