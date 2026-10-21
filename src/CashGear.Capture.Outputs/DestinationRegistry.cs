using CashGear.Capture.Core.Settings;
using CashGear.Capture.Outputs.Credentials;

namespace CashGear.Capture.Outputs;

/// <summary>Makes destinations from the settings: one each for the built-in ones, one per configured server, upload or app.</summary>
public interface IDestinationSource
{
    IEnumerable<IOutputDestination> Create(AppSettings settings, ICredentialStore credentials);
}

/// <summary>
/// Every destination there is (built-in only; no plugins), made from the current settings each time, so a server
/// added in Settings is there at once. Settings' Test buttons pass the settings being edited and the secrets typed
/// but not saved yet.
/// </summary>
public sealed class DestinationRegistry(IEnumerable<IDestinationSource> sources, ISettingsService settings, ICredentialStore credentials)
{
    private static readonly string[] BuiltInOrder =
    [
        DestinationIds.Editor, DestinationIds.VideoEditor, DestinationIds.File, DestinationIds.Clipboard, DestinationIds.Printer,
        DestinationIds.Word, DestinationIds.PowerPoint, DestinationIds.Pdf, DestinationIds.CopyLink,
    ];

    private static readonly string[] ConfiguredOrder = [DestinationIds.ServerPrefix, DestinationIds.UploadPrefix, DestinationIds.AppPrefix];

    /// <summary>All destinations, built-in ones first, then servers, uploads and apps in the order they were added.</summary>
    public IReadOnlyList<IOutputDestination> All(AppSettings? current = null, ICredentialStore? secrets = null)
    {
        var made = sources.SelectMany(s => s.Create(current ?? settings.Current, secrets ?? credentials)).ToList();
        return [.. made.Select((d, i) => (d, i)).OrderBy(x => Rank(x.d.Id)).ThenBy(x => x.i).Select(x => x.d)];
    }

    /// <summary>The destination with this id, or null when it isn't set up (any more).</summary>
    public IOutputDestination? Find(string id, AppSettings? current = null, ICredentialStore? secrets = null) =>
        All(current, secrets).FirstOrDefault(d => DestinationIds.Same(d.Id, id));

    /// <summary>The destination's name, or what to show for a step whose destination is gone.</summary>
    public string NameOf(string id, AppSettings? current = null) => Find(id, current)?.DisplayName ?? $"{id} (not set up)";

    private static int Rank(string id)
    {
        var builtIn = Array.FindIndex(BuiltInOrder, b => DestinationIds.Same(b, id));
        if (builtIn >= 0)
        {
            return builtIn;
        }

        var configured = Array.FindIndex(ConfiguredOrder, p => id.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        return BuiltInOrder.Length + (configured >= 0 ? configured : ConfiguredOrder.Length);
    }
}
