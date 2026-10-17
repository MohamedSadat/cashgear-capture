using CashGear.Capture.Interop.Native;

namespace CashGear.Capture.Audio;

/// <summary>A running process: its ID, its parent's, and its program file name (e.g. <c>chrome.exe</c>).</summary>
internal readonly record struct ProcessEntry(int Id, int ParentId, string Exe);

/// <summary>
/// The running processes as parent and children. Windows records an app's sound by process tree, and many apps run as
/// several processes of the same program (a browser plays sound from a child process), so an app is found by its
/// program file name and recorded from the process at the top of its tree.
/// </summary>
internal static class ProcessTree
{
    /// <summary>Every running process now (Toolhelp32 snapshot); empty if Windows refuses.</summary>
    public static unsafe IReadOnlyList<ProcessEntry> Snapshot()
    {
        var snapshot = Kernel32.CreateToolhelp32Snapshot(Kernel32.Th32csSnapProcess, 0);
        if (snapshot == Kernel32.InvalidHandleValue || snapshot == 0)
        {
            return [];
        }

        try
        {
            var list = new List<ProcessEntry>();
            PROCESSENTRY32W entry = default;
            entry.dwSize = (uint)sizeof(PROCESSENTRY32W);
            for (var more = Kernel32.Process32FirstW(snapshot, &entry); more != 0; more = Kernel32.Process32NextW(snapshot, &entry))
            {
                list.Add(new ProcessEntry((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, new string(entry.szExeFile)));
            }

            return list;
        }
        finally
        {
            Kernel32.CloseHandle(snapshot);
        }
    }

    /// <summary>
    /// The processes of <paramref name="exe"/> at the top of their trees: their parent is another program (or gone).
    /// The first is the one to record: the one whose tree has <paramref name="playing"/> (processes with sound open)
    /// in it if any, else the one with the most processes of its own program under it (a browser's main process).
    /// </summary>
    public static IReadOnlyList<int> Roots(IReadOnlyList<ProcessEntry> processes, string exe, IReadOnlySet<int>? playing = null)
    {
        ArgumentNullException.ThrowIfNull(processes);
        var byId = processes.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
        var roots = processes
            .Where(p => Same(p.Exe, exe) && !(byId.TryGetValue(p.ParentId, out var parent) && parent.Id != p.Id && Same(parent.Exe, exe)))
            .Select(p => p.Id)
            .ToList();
        return [.. roots
            .OrderByDescending(r => playing is not null && playing.Any(id => RootOf(byId, id, exe) == r))
            .ThenByDescending(r => processes.Count(p => p.Id != r && RootOf(byId, p.Id, exe) == r))
            .ThenBy(r => r)];
    }

    /// <summary>The top process of <paramref name="exe"/> above <paramref name="id"/> (itself if its parent is another program), or 0 if it isn't one of them.</summary>
    public static int RootOf(IReadOnlyDictionary<int, ProcessEntry> byId, int id, string exe)
    {
        ArgumentNullException.ThrowIfNull(byId);
        var root = 0;
        for (var steps = 0; steps < 64 && byId.TryGetValue(id, out var process); steps++)
        {
            if (Same(process.Exe, exe))
            {
                root = process.Id;
            }
            else if (root != 0)
            {
                break; // the tree of exe ends at another program
            }

            if (process.ParentId == process.Id)
            {
                break;
            }

            id = process.ParentId;
        }

        return root;
    }

    public static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
