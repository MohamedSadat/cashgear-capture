using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Outputs.Resources;

namespace CashGear.Capture.Outputs;

/// <summary>Copies the links the earlier steps made (uploads) to the clipboard, one per line.</summary>
public sealed class CopyLinkDestination(IOutputService output) : IOutputDestination
{
    public string Id => DestinationIds.CopyLink;

    public string DisplayName => OutputsStrings.CopyLink;

    public bool SupportsVideo => true;

    public async Task<OutputResult> SendAsync(OutputItem item, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Links.Count == 0)
        {
            return OutputResult.Skip(OutputsStrings.NoLinkToCopy);
        }

        await output.CopyTextToClipboardAsync(string.Join(Environment.NewLine, item.Links), ct).ConfigureAwait(false);
        return OutputResult.Done(OutputsStrings.LinksCopied(item.Links.Count));
    }

    public Task<OutputResult> TestAsync(CancellationToken ct) => Task.FromResult(OutputResult.Done(OutputsStrings.NothingToSetUp));
}
