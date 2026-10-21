using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CashGear.Capture.Core;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Engine;
using CashGear.Capture.Interop.Native;
using CashGear.Capture.Recording;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.Bench;

/// <summary>
/// Measures the performance budgets on this PC, against a Release self-contained copy of the app (as installed) run
/// as a separate instance (<c>CASHGEAR_INSTANCE=bench</c>: its own settings, data and single-instance names), so an
/// installed copy keeps running.
/// <code>
/// Bench publish                                   builds the copy in artifacts\bench\app
/// Bench startup [--runs 10]                       process start to the tray icon (budget 1.5 s)
/// Bench idle [--settle 30] [--seconds 120]        CPU and memory while idle (budget ≈ 0% CPU, under 80 MB)
/// Bench overlay [--runs 20]                       the hotkey to the region overlay on screen (budget 150 ms)
/// Bench all                                       publish (if needed), startup, idle, overlay
/// Bench soak [--hours 4] [--monitor N]            by hand: memory while recording a monitor for hours
/// Bench alloc [--seconds 60]                      by hand: the recorder's allocations per frame, 1080p30 of the primary monitor
/// </code>
/// Reports go to artifacts\bench\{command}-{time}.report.txt (and the console).
/// </summary>
internal static partial class Program
{
    /// <summary>The bench copy's only hotkey: Capture Region. Not Alt+Shift or Ctrl+Shift, which switch keyboard layouts.</summary>
    private const string OverlayHotkey = "Ctrl+Alt+F11";

    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Bench publish|startup|idle|overlay|all|soak|alloc … (see Program.cs)");
            return 1;
        }

        var options = Options.Parse(args[1..]);
        try
        {
            return args[0] switch
            {
                "publish" => Publish(options),
                "startup" => Report("startup", Startup(options)),
                "idle" => Report("idle", Idle(options)),
                "overlay" => Report("overlay", Overlay(options)),
                "all" => All(options),
                "soak" => Report("soak", Soak(options).GetAwaiter().GetResult()),
                "alloc" => Report("alloc", Alloc(options).GetAwaiter().GetResult()),
                _ => throw new ArgumentException($"Unknown command {args[0]}"),
            };
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            return 3;
        }
    }

    private static int All(Options options)
    {
        if (!File.Exists(options.App) && Publish(options) != 0)
        {
            return 3;
        }

        var text = new StringBuilder();
        text.AppendLine(Startup(options)).AppendLine().AppendLine(Idle(options)).AppendLine().AppendLine(Overlay(options));
        return Report("all", text.ToString());
    }

    /// <summary>The app as the installer ships it: Release, self-contained, single file (as tools\package.ps1 publishes it).</summary>
    private static int Publish(Options options)
    {
        var output = Path.GetDirectoryName(options.App)!;
        var project = Path.Combine(Repository(), "src", "CashGear.Capture.App", "CashGear.Capture.App.csproj");
        Console.WriteLine($"Publishing {project} to {output}");
        using var dotnet = Process.Start(new ProcessStartInfo("dotnet",
            ["publish", project, "-c", "Release", "-r", "win-x64", "--self-contained", "true", "--nologo", "-v", "q",
             "-p:PublishSingleFile=true", "-p:PublishReadyToRun=true", "-p:DebugType=None", "-p:DebugSymbols=false", "-o", output])
        { UseShellExecute = false })!;
        dotnet.WaitForExit();
        return dotnet.ExitCode;
    }

    /// <summary>Starts and exits the copy <c>--runs</c> times: from the process starting to its tray icon and hotkeys.</summary>
    private static string Startup(Options options)
    {
        using var app = new AppCopy(options.App);
        app.EnsureStopped();
        app.SeedSettings();
        var runs = new List<(double External, double Tray, double Ready)>();
        for (var i = 0; i < options.Runs; i++)
        {
            var external = app.Start();
            var ready = app.LastReady() ?? throw new InvalidOperationException("The app logged no Ready line.");
            runs.Add((external.TotalMilliseconds, ready.TrayMs, ready.ReadyMs));
            Console.WriteLine($"run {i + 1}: tray {ready.TrayMs:0} ms, hotkeys {ready.ReadyMs:0} ms after the process started; {external.TotalMilliseconds:0} ms from Process.Start to the ready signal");
            app.Stop();
            Thread.Sleep(1000);
        }

        var rest = runs.Skip(1).ToList();
        return Lines(
            $"Cold start to tray (budget 1500 ms), {options.Runs} runs of {options.App}",
            $"  first run (just published): tray {runs[0].Tray:0} ms, hotkeys {runs[0].Ready:0} ms, ready signal {runs[0].External:0} ms",
            $"  the others: tray median {Median(rest.Select(r => r.Tray)):0} ms, p95 {Percentile(rest.Select(r => r.Tray), 95):0} ms, max {rest.Max(r => r.Tray):0} ms",
            $"              hotkeys median {Median(rest.Select(r => r.Ready)):0} ms; ready signal median {Median(rest.Select(r => r.External)):0} ms",
            $"  {Verdict(Percentile(rest.Select(r => r.Tray), 95) < 1500 && runs[0].Tray < 1500)}");
    }

    /// <summary>Starts the copy, lets it settle (FFmpeg and the encoders are loaded 3 s in), then watches it do nothing.</summary>
    private static string Idle(Options options)
    {
        using var app = new AppCopy(options.App);
        app.EnsureStopped();
        app.SeedSettings();
        app.Start();
        try
        {
            Console.WriteLine($"Settling for {options.Settle} s");
            Thread.Sleep(TimeSpan.FromSeconds(options.Settle));
            var process = app.Process!;
            process.Refresh();
            var cpuStart = process.TotalProcessorTime;
            var wall = Stopwatch.StartNew();
            var samples = new List<Memory>();
            while (wall.Elapsed.TotalSeconds < options.Seconds)
            {
                samples.Add(Memory.Of(process));
                Thread.Sleep(5000);
            }

            process.Refresh();
            var cpu = process.TotalProcessorTime - cpuStart;
            var oneCore = cpu.TotalMilliseconds / wall.Elapsed.TotalMilliseconds * 100;
            var last = samples[^1];
            var threads = process.Threads.Count;
            return Lines(
                $"Idle for {wall.Elapsed.TotalSeconds:0} s after {options.Settle} s settling (budgets: CPU about 0%, memory under 80 MB)",
                $"  CPU: {cpu.TotalMilliseconds:0} ms in all = {oneCore:0.000}% of one core, {oneCore / Environment.ProcessorCount:0.0000}% of the {Environment.ProcessorCount} logical processors",
                $"  memory at the end: {last.PrivateWorkingSetMb:0.0} MB private working set (Task Manager's \"Memory\"), {last.WorkingSetMb:0.0} MB working set, {last.PrivateBytesMb:0.0} MB private bytes",
                $"  private working set over the window: min {samples.Min(s => s.PrivateWorkingSetMb):0.0}, max {samples.Max(s => s.PrivateWorkingSetMb):0.0} MB; {threads} threads",
                $"  {Verdict(oneCore < 0.5 && last.PrivateWorkingSetMb < 80)} (CPU under 0.5% of one core, private working set under 80 MB)");
        }
        finally
        {
            app.Stop();
        }
    }

    /// <summary>Presses the copy's Capture Region hotkey and times the overlay's first frame against the key press (QPC in both processes).</summary>
    private static string Overlay(Options options)
    {
        using var app = new AppCopy(options.App);
        app.EnsureStopped();
        app.SeedSettings();
        app.Start();
        try
        {
            Thread.Sleep(TimeSpan.FromSeconds(8)); // past the warm-up at 3 s
            var runs = new List<(double FromKey, double FromHotkey)>();
            for (var i = 0; i < options.Runs; i++)
            {
                var pressed = Stopwatch.GetTimestamp();
                Keys.Press(Keys.Control, Keys.Menu, Keys.F11);
                var shown = app.WaitForOverlay(pressed, TimeSpan.FromSeconds(5))
                    ?? throw new InvalidOperationException("The overlay didn't show within 5 s.");
                var fromKey = Stopwatch.GetElapsedTime(pressed, shown.ShownQpc).TotalMilliseconds;
                runs.Add((fromKey, shown.SinceHotkeyMs));
                Console.WriteLine($"run {i + 1}: {fromKey:0.0} ms from the key press ({shown.SinceHotkeyMs:0.0} ms from WM_HOTKEY)");
                app.CloseOverlay();
                Thread.Sleep(800);
            }

            var rest = runs.Skip(1).ToList();
            return Lines(
                $"Hotkey to region overlay (budget 150 ms), {options.Runs} runs; from SendInput to the overlay's first rendered frame",
                $"  first: {runs[0].FromKey:0.0} ms ({runs[0].FromHotkey:0.0} ms from WM_HOTKEY)",
                $"  the others: median {Median(rest.Select(r => r.FromKey)):0.0} ms, p95 {Percentile(rest.Select(r => r.FromKey), 95):0.0} ms, max {rest.Max(r => r.FromKey):0.0} ms (from WM_HOTKEY: median {Median(rest.Select(r => r.FromHotkey)):0.0} ms)",
                $"  {Verdict(Percentile(rest.Select(r => r.FromKey), 95) < 150)} (p95 after the first)");
        }
        finally
        {
            app.Stop();
        }
    }

    /// <summary>By hand: records a whole monitor for hours, sampling memory every minute; the last hour should hold what the first did.</summary>
    private static async Task<string> Soak(Options options)
    {
        using var services = Services(options.Out);
        var monitors = services.GetRequiredService<IMonitorService>().GetMonitors();
        var monitor = options.Monitor is { } n ? monitors[n] : monitors[0];
        var recorder = services.GetRequiredService<IScreenRecorder>();
        recorder.Warning += (_, message) => Console.WriteLine("WARNING " + message);
        var (width, height) = EncoderPresets.EvenSize(monitor.Bounds.Width, monitor.Bounds.Height);
        var output = Path.Combine(options.Out, $"soak-{DateTime.Now:yyyyMMdd-HHmmss}.mp4");
        await recorder.StartAsync(new RecordingOptions(monitor, monitor.Bounds with { Width = width, Height = height }, 30, QualityPreset.Balanced, IncludeCursor: true, "auto", output, SystemAudio: true));
        var samples = new List<(double Minutes, Memory Memory, double HeapMb)>();
        using var process = Process.GetCurrentProcess();
        for (var minute = 1; minute <= options.Hours * 60; minute++)
        {
            while (recorder.Elapsed.TotalMinutes < minute)
            {
                await Task.Delay(500);
            }

            var heap = GC.GetTotalMemory(forceFullCollection: false) / 1048576.0;
            samples.Add((minute, Memory.Of(process), heap));
            Console.WriteLine($"{minute} min: {samples[^1].Memory.PrivateBytesMb:0.0} MB private, {heap:0.0} MB GC heap");
        }

        var result = await recorder.StopAsync();
        var first = samples.Where(s => s.Minutes is > 10 and <= 60).ToList();
        var last = samples.Where(s => s.Minutes > (options.Hours * 60) - 60).ToList();
        var growth = last.Max(s => s.Memory.PrivateBytesMb) - first.Max(s => s.Memory.PrivateBytesMb);
        return Lines(
            $"Recorded {monitor.ShortName} for {result.Duration} ({result.FramesWritten} frames, {result.FramesDropped} dropped) to {result.Path}",
            $"  private bytes: first hour max {first.Max(s => s.Memory.PrivateBytesMb):0.0} MB, last hour max {last.Max(s => s.Memory.PrivateBytesMb):0.0} MB ({growth:+0.0;-0.0} MB)",
            $"  {Verdict(growth < 20)} (the last hour within 20 MB of the first)",
            string.Join(Environment.NewLine, samples.Where(s => s.Minutes % 10 == 0).Select(s => $"  {s.Minutes,4:0} min: {s.Memory.PrivateBytesMb:0.0} MB private, {s.HeapMb:0.0} MB GC heap")));
    }

    /// <summary>By hand: a real 1080p30 recording of the primary monitor, and what its threads allocate per frame once running.</summary>
    private static async Task<string> Alloc(Options options)
    {
        using var services = Services(options.Out);
        var monitor = services.GetRequiredService<IMonitorService>().GetMonitors().First(m => m.IsPrimary);
        var recorder = (ScreenRecorder)services.GetRequiredService<IScreenRecorder>();
        var (width, height) = EncoderPresets.EvenSize(monitor.Bounds.Width, monitor.Bounds.Height);
        var output = Path.Combine(options.Out, $"alloc-{DateTime.Now:yyyyMMdd-HHmmss}.mp4");
        await recorder.StartAsync(new RecordingOptions(monitor, monitor.Bounds with { Width = width, Height = height }, 30, QualityPreset.Balanced, IncludeCursor: true, "auto", output, SystemAudio: true));
        while (recorder.Elapsed.TotalSeconds < 10)
        {
            await Task.Delay(100);
        }

        var start = recorder.Allocations!.Value;
        var total = GC.GetTotalAllocatedBytes(precise: true);
        while (recorder.Elapsed.TotalSeconds < options.Seconds)
        {
            await Task.Delay(100);
        }

        var end = recorder.Allocations!.Value;
        var allBytes = GC.GetTotalAllocatedBytes(precise: true) - total;
        var result = await recorder.StopAsync();
        var frames = end.Frames - start.Frames;
        return Lines(
            $"Recorded {monitor.ShortName} {width}x{height} at 30 fps: {result.FramesWritten} frames ({result.EncoderName})",
            $"  per frame from 10 s to {options.Seconds} s ({frames} frames): video thread {(end.Video - start.Video) / (double)frames:0.0} B, sound thread {(end.Sound - start.Sound) / (double)frames:0.0} B, file thread {(end.Writer - start.Writer) / (double)frames:0.0} B",
            $"  the whole process meanwhile: {allBytes / (double)frames:0.0} B per frame (capture callbacks, this bench's own polling and logging included)");
    }

    private static ServiceProvider Services(string root)
    {
        Directory.CreateDirectory(root);
        var paths = new AppPaths(Path.Combine(root, "settings"), Path.Combine(root, "logs"), root, Path.Combine(root, "data"));
        return new ServiceCollection()
            .AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning))
            .AddCaptureCore(paths)
            .AddCaptureEngine()
            .AddScreenRecording()
            .BuildServiceProvider();
    }

    private static int Report(string command, string text)
    {
        var folder = Path.Combine(Repository(), "artifacts", "bench");
        Directory.CreateDirectory(folder);
        var header = $"CashGear Capture bench, {DateTime.Now:yyyy-MM-dd HH:mm:ss}, {Machine()}";
        var path = Path.Combine(folder, $"{command}-{DateTime.Now:yyyyMMdd-HHmmss}.report.txt");
        File.WriteAllText(path, header + Environment.NewLine + text + Environment.NewLine);
        Console.WriteLine(header);
        Console.WriteLine(text);
        Console.WriteLine("Report: " + path);
        return 0;
    }

    private static string Machine()
    {
        var cpu = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", "?") as string;
        var gpus = string.Join(", ", Engine.Diagnostics.GraphicsAdapters.List().Where(a => !a.IsSoftware).Select(a => a.ToString()));
        return $"{cpu?.Trim()} ({Environment.ProcessorCount} logical), {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1073741824.0:0} GB, {gpus}, Windows {Environment.OSVersion.Version}";
    }

    private static string Verdict(bool met) => met ? "MET" : "NOT MET";

    private static string Lines(params string[] lines) => string.Join(Environment.NewLine, lines);

    private static double Median(IEnumerable<double> values) => Percentile(values, 50);

    private static double Percentile(IEnumerable<double> values, double percent)
    {
        var sorted = values.Order().ToList();
        if (sorted.Count == 0)
        {
            return double.NaN;
        }

        var rank = percent / 100 * (sorted.Count - 1);
        var low = (int)Math.Floor(rank);
        var high = (int)Math.Ceiling(rank);
        return sorted[low] + ((sorted[high] - sorted[low]) * (rank - low));
    }

    internal static string Repository()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "CashGear.Capture.sln")))
        {
            folder = folder.Parent;
        }

        return folder?.FullName ?? Directory.GetCurrentDirectory();
    }

    /// <summary>Memory as Task Manager shows it, from GetProcessMemoryInfo.</summary>
    private readonly record struct Memory(double PrivateWorkingSetMb, double WorkingSetMb, double PrivateBytesMb)
    {
        public static unsafe Memory Of(Process process)
        {
            var counters = new ProcessMemoryCountersEx2 { Cb = (uint)sizeof(ProcessMemoryCountersEx2) };
            if (!Native.K32GetProcessMemoryInfo(process.Handle, &counters, counters.Cb))
            {
                throw new InvalidOperationException("GetProcessMemoryInfo failed.");
            }

            return new Memory(counters.PrivateWorkingSetSize / 1048576.0, counters.WorkingSetSize / 1048576.0, counters.PrivateUsage / 1048576.0);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCountersEx2
    {
        public uint Cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
        public nuint PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }

    private static unsafe partial class Native
    {
        [LibraryImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool K32GetProcessMemoryInfo(nint process, ProcessMemoryCountersEx2* counters, uint size);
    }

    /// <summary>Keys pressed with SendInput (virtual keys: the keyboard layout doesn't matter).</summary>
    private static unsafe class Keys
    {
        public const ushort Control = 0x11;
        public const ushort Menu = 0x12; // Alt
        public const ushort Escape = 0x1B;
        public const ushort F11 = 0x7A;

        /// <summary>The keys down in order, then up in reverse order.</summary>
        public static void Press(params ushort[] keys)
        {
            var inputs = new INPUT[keys.Length * 2];
            for (var i = 0; i < keys.Length; i++)
            {
                inputs[i] = INPUT.Key(keys[i], up: false);
                inputs[inputs.Length - 1 - i] = INPUT.Key(keys[i], up: true);
            }

            fixed (INPUT* p = inputs)
            {
                if (User32.SendInput((uint)inputs.Length, p, sizeof(INPUT)) != inputs.Length)
                {
                    throw new InvalidOperationException("SendInput was refused.");
                }
            }
        }
    }

    /// <summary>The published copy, started as the bench instance with a signal for when it is ready.</summary>
    private sealed class AppCopy(string exe) : IDisposable
    {
        public const string Instance = "bench";

        private readonly string _readyName = $@"Local\CashGear.Bench.Ready.{Environment.ProcessId}";
        private readonly string _settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CashGear", "Capture-" + Instance);
        private readonly string _logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CashGear", "Capture-" + Instance, "logs");

        public Process? Process { get; private set; }

        /// <summary>Settings for the bench: no hotkeys but Capture Region's, no toolbar, the overlay's own captures not kept.</summary>
        public void SeedSettings()
        {
            Directory.CreateDirectory(_settings);
            var hotkeys = new Dictionary<string, string?>
            {
                ["captureRegion"] = OverlayHotkey,
                ["captureActiveWindow"] = null,
                ["captureFullScreen"] = null,
                ["captureScrolling"] = null,
                ["record"] = null,
                ["captureText"] = null,
                ["toggleDrawing"] = null,
                ["repeatLastCapture"] = null,
            };
            var settings = new Dictionary<string, object> { ["schemaVersion"] = AppSettings.CurrentSchemaVersion, ["hotkeys"] = hotkeys };
            File.WriteAllText(Path.Combine(_settings, "settings.json"), JsonSerializer.Serialize(settings));
        }

        /// <summary>Starts the copy and waits for its ready signal; the time from Process.Start to it.</summary>
        public TimeSpan Start()
        {
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, _readyName);
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
            start.Environment["CASHGEAR_INSTANCE"] = Instance;
            start.Environment["CASHGEAR_READY_EVENT"] = _readyName;
            var clock = Stopwatch.StartNew();
            Process = Process.Start(start)!;
            if (!ready.WaitOne(TimeSpan.FromSeconds(60)))
            {
                throw new InvalidOperationException("The app wasn't ready within 60 s.");
            }

            return clock.Elapsed;
        }

        /// <summary>Asks the copy to exit (<c>--exit</c> to the bench instance) and waits for it.</summary>
        public void Stop()
        {
            if (Process is null)
            {
                return;
            }

            var exit = new ProcessStartInfo(exe, "--exit") { UseShellExecute = false };
            exit.Environment["CASHGEAR_INSTANCE"] = Instance;
            using (var sender = Process.Start(exit)!)
            {
                sender.WaitForExit(10_000);
            }

            if (!Process.WaitForExit(15_000))
            {
                Process.Kill();
            }

            Process.Dispose();
            Process = null;
        }

        /// <summary>A bench copy left running by an earlier run is asked to exit first.</summary>
        public void EnsureStopped()
        {
            var exit = new ProcessStartInfo(exe, "--exit") { UseShellExecute = false };
            exit.Environment["CASHGEAR_INSTANCE"] = Instance;
            using var sender = Process.Start(exit)!;
            sender.WaitForExit(10_000);
            Thread.Sleep(500);
        }

        /// <summary>The newest "Ready" line this process logged.</summary>
        public (double TrayMs, double ReadyMs)? LastReady()
        {
            foreach (var line in Enumerable.Reverse(Lines()))
            {
                if (line.TryGetProperty("TrayMs", out var tray) && line.GetProperty("ProcessId").GetInt32() == Process!.Id)
                {
                    return (tray.GetDouble(), line.GetProperty("ReadyMs").GetDouble());
                }
            }

            return null;
        }

        /// <summary>Waits for an "Overlay shown" line after <paramref name="since"/> (QPC).</summary>
        public (long ShownQpc, double SinceHotkeyMs)? WaitForOverlay(long since, TimeSpan timeout)
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < timeout)
            {
                foreach (var line in Enumerable.Reverse(Lines()))
                {
                    if (line.TryGetProperty("ShownQpc", out var shown) && shown.GetInt64() > since)
                    {
                        return (shown.GetInt64(), line.GetProperty("SinceHotkeyMs").GetDouble());
                    }
                }

                Thread.Sleep(5);
            }

            return null;
        }

        /// <summary>Esc to the overlay, but only when the copy's window is the one in front (never into another app).</summary>
        public void CloseOverlay()
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                if (ForegroundProcess() == Process!.Id)
                {
                    Keys.Press(Keys.Escape);
                    return;
                }

                Thread.Sleep(100);
            }

            throw new InvalidOperationException("The overlay never came to the front, so Esc wasn't sent.");
        }

        public void Dispose()
        {
            if (Process is not null)
            {
                Stop();
            }
        }

        private static unsafe int ForegroundProcess()
        {
            uint id;
            User32.GetWindowThreadProcessId(User32.GetForegroundWindow(), &id);
            return (int)id;
        }

        /// <summary>Today's log, parsed (read while the app writes to it).</summary>
        private List<JsonElement> Lines()
        {
            var path = Path.Combine(_logs, $"capture-{DateTime.Now:yyyyMMdd}.jsonl");
            var lines = new List<JsonElement>();
            if (!File.Exists(path))
            {
                return lines;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { Length: > 0 } line)
            {
                try
                {
                    lines.Add(JsonDocument.Parse(line).RootElement.Clone());
                }
                catch (JsonException)
                {
                    // A line still being written.
                }
            }

            return lines;
        }
    }

    private sealed record Options(int Runs, int Seconds, int Settle, double Hours, int? Monitor, string App, string Out)
    {
        public static Options Parse(string[] args)
        {
            var bench = Path.Combine(Repository(), "artifacts", "bench");
            var options = new Options(10, 120, 30, 4, null, Path.Combine(bench, "app", "CashGear.Capture.App.exe"), Path.Combine(bench, "work"));
            for (var i = 0; i < args.Length; i++)
            {
                var value = i + 1 < args.Length ? args[i + 1] : null;
                options = args[i] switch
                {
                    "--runs" => options with { Runs = int.Parse(value!, CultureInfo.InvariantCulture) },
                    "--seconds" => options with { Seconds = int.Parse(value!, CultureInfo.InvariantCulture) },
                    "--settle" => options with { Settle = int.Parse(value!, CultureInfo.InvariantCulture) },
                    "--hours" => options with { Hours = double.Parse(value!, CultureInfo.InvariantCulture) },
                    "--monitor" => options with { Monitor = int.Parse(value!, CultureInfo.InvariantCulture) },
                    "--app" => options with { App = Path.GetFullPath(value!) },
                    "--out" => options with { Out = Path.GetFullPath(value!) },
                    _ => throw new ArgumentException($"Unknown option {args[i]}"),
                };
                i++;
            }

            return options;
        }
    }
}
