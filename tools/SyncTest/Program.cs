using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using CashGear.Capture.Core;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Engine;
using CashGear.Capture.Media;
using CashGear.Capture.Recording;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.SyncTest;

/// <summary>
/// Records the primary monitor with the real recorder while a square in its corner flashes in step with beeps
/// through the speakers, then measures how far apart they are in the file and whether that changes over time.
/// <code>
/// SyncTest run [--minutes M | --seconds S] [--fps 30] [--encoder auto] [--pauses N] [--pause-seconds 5] [--out DIR]
///              [--busy] [--microphone default|ID] [--tracks Mixed|Separate|Both]
/// SyncTest analyze FILE
/// SyncTest recover [--out DIR]     (saves recordings a killed run left behind, then analyzes them)
/// </code>
/// Everything (settings, logs, temporary files, results) goes under --out (default artifacts\synctest).
/// </summary>
internal static class Program
{
    /// <summary>The flashing square, in physical pixels from the monitor's top-left corner.</summary>
    private static readonly PixelRect Square = new(40, 40, 240, 240);

    /// <summary>The part of the square the analysis looks at (its edges may blend into the frame around it).</summary>
    private static readonly PixelRect Inside = new(60, 60, 200, 200);

    private const double DriftLimitMs = 40;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("SyncTest run|analyze|recover … (see Program.cs)");
            return 1;
        }

        var options = Options.Parse(args[1..]);
        try
        {
            return args[0] switch
            {
                "run" => RunOnDispatcher(() => RunAsync(options)),
                "analyze" => Analyze(options.Positional ?? throw new ArgumentException("analyze needs a file")),
                "recover" => RunOnDispatcher(() => RecoverAsync(options)),
                _ => throw new ArgumentException($"Unknown command {args[0]}"),
            };
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            return 3;
        }
    }

    /// <summary>Runs <paramref name="body"/> on a WPF dispatcher (the square is a window) and returns its exit code.</summary>
    private static int RunOnDispatcher(Func<Task<int>> body)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var code = 0;
        app.Startup += async (_, _) =>
        {
            try
            {
                code = await body();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                code = 3;
            }
            finally
            {
                app.Shutdown(code);
            }
        };
        app.Run();
        return code;
    }

    private static ServiceProvider Services(string root)
    {
        var paths = new AppPaths(Path.Combine(root, "settings"), Path.Combine(root, "logs"), root, Path.Combine(root, "data"));
        var services = new ServiceCollection()
            .AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information).AddFilter("Microsoft", LogLevel.Warning))
            .AddCaptureCore(paths)
            .AddCaptureEngine()
            .AddScreenRecording();
        return services.BuildServiceProvider();
    }

    private static async Task<int> RunAsync(Options options)
    {
        Directory.CreateDirectory(options.Out);
        using var services = Services(options.Out);
        var encoders = services.GetRequiredService<IRecordingEncoders>();
        if (encoders.Problem is { } problem)
        {
            Console.Error.WriteLine(problem);
            return 3;
        }

        var detected = await encoders.DetectAsync();
        Console.WriteLine("Encoders: " + string.Join(", ", detected.Where(p => p.Works).Select(p => $"{p.Id} ({p.Detail ?? "works"})")));
        var monitor = services.GetRequiredService<IMonitorService>().GetMonitors().First(m => m.IsPrimary);
        var (width, height) = EncoderPresets.EvenSize(monitor.Bounds.Width, monitor.Bounds.Height);
        var area = monitor.Bounds with { Width = width, Height = height };
        var recorder = services.GetRequiredService<IScreenRecorder>();
        recorder.Warning += (_, message) => Console.WriteLine("WARNING " + message);
        recorder.Failed += (_, message) => Console.WriteLine("FAILED " + message);

        using var stimulus = new Stimulus(
            new PixelRect(monitor.Bounds.X + Square.X, monitor.Bounds.Y + Square.Y, Square.Width, Square.Height),
            monitor.Scale.ScaleX,
            TimeSpan.FromSeconds(options.Interval));
        stimulus.Start();
        var busy = options.Busy ? Busy(new PixelRect(monitor.Bounds.X + 320, monitor.Bounds.Y + 40, 240, 240), monitor.Scale.ScaleX) : null;
        Console.WriteLine($"Beeps on {stimulus.DeviceName} every {options.Interval} s; recording {options.Duration} of {monitor.ShortName} {area} at {options.Fps} fps with '{options.Encoder}'");
        await Task.Delay(500);

        var output = Path.Combine(options.Out, $"synctest-{DateTime.Now:yyyyMMdd-HHmmss}.mp4");
        var recording = new RecordingOptions(
            monitor, area, options.Fps, options.Quality, IncludeCursor: false, options.Encoder, output, SystemAudio: true,
            Microphone: options.Microphone, Tracks: options.Tracks);
        var process = Process.GetCurrentProcess();
        await recorder.StartAsync(recording);
        var cpuStart = process.TotalProcessorTime;
        var wall = Stopwatch.StartNew();
        var pauses = Enumerable.Range(1, options.Pauses).Select(i => options.Duration * i / (options.Pauses + 1)).ToList();
        var nextReport = TimeSpan.Zero;
        while (recorder.Elapsed < options.Duration && recorder.IsRecording)
        {
            if (pauses.Count > 0 && recorder.Elapsed >= pauses[0])
            {
                pauses.RemoveAt(0);
                recorder.Pause();
                Console.WriteLine($"paused at {recorder.Elapsed:hh\\:mm\\:ss\\.fff} for {options.PauseSeconds} s");
                await Task.Delay(TimeSpan.FromSeconds(options.PauseSeconds));
                recorder.Resume();
            }

            if (recorder.Elapsed >= nextReport)
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"elapsed {recorder.Elapsed.TotalSeconds:0.0} s, {stimulus.Flashes} flashes"));
                nextReport += options.Duration > TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(1) : TimeSpan.FromSeconds(1);
            }

            await Task.Delay(20);
        }

        process.Refresh();
        var cpu = process.TotalProcessorTime - cpuStart;
        var recordedWall = wall.Elapsed;
        var result = await recorder.StopAsync();
        busy?.Close();
        var cores = cpu.TotalSeconds / recordedWall.TotalSeconds;
        var cpuText = string.Create(CultureInfo.InvariantCulture,
            $"CPU         {cores / Environment.ProcessorCount:P1} of {Environment.ProcessorCount} logical CPUs ({cores:0.00} cores) while recording, this whole process");
        var framesText = $"Recording   {result.FramesWritten} frames, {result.FramesDropped} dropped, {result.FramesRepeated} repeated, {result.EncoderName}, {result.FileBytes / 1048576.0:0.0} MB";
        Console.WriteLine(cpuText);
        Console.WriteLine(framesText);
        return Report(result.Path, cpuText + Environment.NewLine + framesText);
    }

    /// <summary>
    /// A square that changes colour on every screen refresh, so Windows sends a new picture of the monitor every
    /// frame and the recorder reads every one back (the worst case for the CPU). Its own drawing counts against the
    /// measured CPU too, so the figure is on the high side.
    /// </summary>
    private static Window Busy(PixelRect where, double scale)
    {
        var greys = new[] { System.Windows.Media.Brushes.DimGray, System.Windows.Media.Brushes.Gray };
        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Left = where.X / scale,
            Top = where.Y / scale,
            Width = where.Width / scale,
            Height = where.Height / scale,
        };
        var n = 0;
        EventHandler paint = (_, _) => window.Background = greys[n++ & 1];
        System.Windows.Media.CompositionTarget.Rendering += paint;
        window.Closed += (_, _) => System.Windows.Media.CompositionTarget.Rendering -= paint;
        window.Show();
        return window;
    }

    private static async Task<int> RecoverAsync(Options options)
    {
        using var services = Services(options.Out);
        var recovery = services.GetRequiredService<IRecordingRecovery>();
        var found = recovery.Find();
        Console.WriteLine($"{found.Count} interrupted recording(s) in {options.Out}");
        var code = 0;
        foreach (var recording in found)
        {
            var saved = await recovery.RecoverAsync(recording);
            Console.WriteLine($"Recovered {string.Join(", ", saved.Files)}: {saved.Duration.TotalSeconds:0.000} s, cut short {saved.Truncated}");
            foreach (var file in saved.Files)
            {
                code = Math.Max(code, Report(file, $"Recovered   from {recording.Directory}"));
            }
        }

        return code;
    }

    private static int Analyze(string file)
    {
        new FfmpegRuntime(Microsoft.Extensions.Logging.Abstractions.NullLogger<FfmpegRuntime>.Instance).EnsureAvailable();
        return Report(file, null);
    }

    private static int Report(string file, string? extra)
    {
        var report = Analysis.Analyze(file, Inside.X, Inside.Y, Inside.Width, Inside.Height);
        var summary = report.Summary(extra);
        Console.WriteLine(summary);
        File.WriteAllText(Path.ChangeExtension(file, ".report.txt"), summary);
        report.WriteCsv(Path.ChangeExtension(file, ".offsets.csv"));
        var pass = report.Claps.Count >= Math.Min(3, report.Flashes) && Math.Abs(report.FittedDriftMs) < DriftLimitMs && Math.Abs(report.FirstToLastMs) < DriftLimitMs;
        Console.WriteLine(pass ? $"PASS: drift under {DriftLimitMs} ms" : $"FAIL: drift of {DriftLimitMs} ms or more, or too few claps");
        return pass ? 0 : 2;
    }

    private sealed record Options(
        TimeSpan Duration,
        int Fps,
        string Encoder,
        QualityPreset Quality,
        int Pauses,
        double PauseSeconds,
        double Interval,
        string Out,
        string? Positional,
        bool Busy,
        string? Microphone,
        AudioTrackLayout Tracks)
    {
        public static Options Parse(string[] args)
        {
            var duration = TimeSpan.FromSeconds(30);
            var fps = 30;
            var encoder = EncoderPresets.Auto;
            var quality = QualityPreset.Balanced;
            var pauses = 0;
            var pauseSeconds = 5.0;
            var interval = 2.0;
            var output = Path.GetFullPath(Path.Combine(FindRepository(), "artifacts", "synctest"));
            string? positional = null;
            var busy = false;
            string? microphone = null;
            var tracks = AudioTrackLayout.Mixed;
            for (var i = 0; i < args.Length; i++)
            {
                string Next() => args[++i];
                switch (args[i])
                {
                    case "--minutes": duration = TimeSpan.FromMinutes(double.Parse(Next(), CultureInfo.InvariantCulture)); break;
                    case "--seconds": duration = TimeSpan.FromSeconds(double.Parse(Next(), CultureInfo.InvariantCulture)); break;
                    case "--fps": fps = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--encoder": encoder = Next(); break;
                    case "--quality": quality = Enum.Parse<QualityPreset>(Next(), ignoreCase: true); break;
                    case "--pauses": pauses = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--pause-seconds": pauseSeconds = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--interval": interval = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--out": output = Path.GetFullPath(Next()); break;
                    case "--busy": busy = true; break;
                    case "--microphone": microphone = Next(); break;
                    case "--tracks": tracks = Enum.Parse<AudioTrackLayout>(Next(), ignoreCase: true); break;
                    default: positional = args[i]; break;
                }
            }

            return new Options(duration, fps, encoder, quality, pauses, pauseSeconds, interval, output, positional, busy, microphone, tracks);
        }

        private static string FindRepository()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "CashGear.Capture.sln")))
                {
                    return dir.FullName;
                }
            }

            return Path.GetTempPath();
        }
    }
}
