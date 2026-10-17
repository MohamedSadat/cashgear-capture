using System.Diagnostics;
using System.Runtime.InteropServices;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Interop.Native;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace CashGear.Capture.Audio;

/// <summary>
/// The apps whose sound can be recorded by itself: programs with a visible window, and programs with sound open on
/// an output device (a music player in the tray has no window). One entry per program file.
/// </summary>
internal sealed partial class AudioApps(ILogger<AudioApps> logger) : IAudioApps
{
    /// <summary>Programs that are part of Windows or this app, never worth recording by themselves.</summary>
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "searchapp.exe", "searchhost.exe", "shellexperiencehost.exe", "startmenuexperiencehost.exe",
        "textinputhost.exe", "applicationframehost.exe", "systemsettings.exe", "lockapp.exe", "audiodg.exe",
        "svchost.exe", "dwm.exe", "csrss.exe", "winlogon.exe", "system", "[system process]", "idle",
        "CashGear.Capture.App.exe",
    };

    public bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);

    public IReadOnlyList<AudioApp> List()
    {
        var processes = ProcessTree.Snapshot();
        var byId = processes.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
        var playing = PlayingProcesses();
        var exes = new Dictionary<string, (int Process, bool Playing)>(StringComparer.OrdinalIgnoreCase);

        foreach (var id in WindowProcesses().Concat(playing))
        {
            if (id == Environment.ProcessId || !byId.TryGetValue(id, out var process) || Ignored.Contains(process.Exe))
            {
                continue;
            }

            var isPlaying = playing.Contains(id);
            exes[process.Exe] = exes.TryGetValue(process.Exe, out var known)
                ? (known.Process, known.Playing || isPlaying)
                : (id, isPlaying);
        }

        return [.. exes
            .Select(e => new AudioApp(e.Key.ToLowerInvariant(), DisplayName(e.Value.Process, e.Key), e.Value.Playing))
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>The process at the top of <paramref name="exe"/>'s tree to record, preferring one that has sound open; null when it isn't running.</summary>
    public int? FindRoot(string exe)
    {
        var processes = ProcessTree.Snapshot();
        var roots = ProcessTree.Roots(processes, exe);
        if (roots.Count > 1)
        {
            roots = ProcessTree.Roots(processes, exe, PlayingProcesses());
        }

        return roots.Count == 0 ? null : roots[0];
    }

    /// <summary>"Google Chrome" from the program's description, or "Chrome" when it can't be read.</summary>
    private string DisplayName(int processId, string exe)
    {
        try
        {
            if (ImagePath(processId) is { } path)
            {
                var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (!string.IsNullOrWhiteSpace(description))
                {
                    return description.Trim();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            LogNoDescription(exe, ex.Message);
        }

        var name = Path.GetFileNameWithoutExtension(exe);
        return name.Length == 0 ? exe : char.ToUpperInvariant(name[0]) + name[1..];
    }

    /// <summary>The program file of a process, or null when it can't be opened (elevated or protected).</summary>
    private static unsafe string? ImagePath(int processId)
    {
        var process = Kernel32.OpenProcess(Kernel32.ProcessQueryLimitedInformation, 0, (uint)processId);
        if (process == 0)
        {
            return null;
        }

        try
        {
            var buffer = stackalloc char[1024];
            var size = 1024u;
            return Kernel32.QueryFullProcessImageNameW(process, 0, buffer, &size) == 0 ? null : new string(buffer, 0, (int)size);
        }
        finally
        {
            Kernel32.CloseHandle(process);
        }
    }

    /// <summary>Processes with a visible, titled top-level window that isn't a tool window.</summary>
    private static unsafe HashSet<int> WindowProcesses()
    {
        var ids = new HashSet<int>();
        foreach (var window in NativeEnumerators.EnumWindows())
        {
            if (!User32.IsWindowVisible(window) || User32.GetWindowTextLengthW(window) == 0
                || (User32.GetWindowLongPtrW(window, User32.GwlExStyle) & User32.WsExToolWindow) != 0)
            {
                continue;
            }

            uint id;
            User32.GetWindowThreadProcessId(window, &id);
            ids.Add((int)id);
        }

        return ids;
    }

    /// <summary>Processes with an active sound session on any output device.</summary>
    private HashSet<int> PlayingProcesses()
    {
        var ids = new HashSet<int>();
        try
        {
            using var devices = new MMDeviceEnumerator();
            foreach (var device in devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    using var sessions = device.AudioSessionManager.Sessions;
                    for (var i = 0; i < sessions.Count; i++)
                    {
                        using var session = sessions[i];
                        if (session.State == AudioSessionState.AudioSessionStateActive && !session.IsSystemSoundsSession)
                        {
                            ids.Add((int)session.GetProcessID);
                        }
                    }
                }
            }
        }
        catch (COMException ex)
        {
            LogSessionsFailed(ex);
        }

        return ids;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "No description for {Exe}: {Reason}")]
    private partial void LogNoDescription(string exe, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not list the apps playing sound")]
    private partial void LogSessionsFailed(Exception ex);
}
