using System.Collections;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.Core.Diagnostics;

/// <summary>
/// Ids that tie a run of work together in the logs: every line logged inside a scope (on that flow, and on the
/// threads and timers started inside it) carries the id as a property, e.g. <c>"Recording": "3fa9c2d1"</c>.
/// </summary>
public static class LogScopes
{
    /// <summary>One screen recording, from its start to its saved file.</summary>
    public const string Recording = "Recording";

    /// <summary>One capture, from the hotkey or menu to its workflow.</summary>
    public const string Capture = "Capture";

    /// <summary>One export from the video editor.</summary>
    public const string Export = "Export";

    /// <summary>A new id: 8 lower-case hex digits.</summary>
    public static string NewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));

    /// <summary>Starts a scope in which every line logged carries <paramref name="name"/> = <paramref name="id"/>.</summary>
    public static IDisposable? Begin(ILogger logger, string name, string id)
    {
        ArgumentNullException.ThrowIfNull(logger);
        return logger.BeginScope(new Scope(name, id));
    }

    /// <summary>A scope's state: one property (Serilog adds every pair of an enumerable state to the events).</summary>
    private sealed class Scope(string name, string id) : IReadOnlyList<KeyValuePair<string, object>>
    {
        public int Count => 1;

        public KeyValuePair<string, object> this[int index] =>
            index == 0 ? new(name, id) : throw new ArgumentOutOfRangeException(nameof(index));

        public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
        {
            yield return this[0];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() => $"{name} {id}";
    }
}
