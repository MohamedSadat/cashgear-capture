using CashGear.Capture.App.Capture;
using CashGear.Capture.App.Editor;
using CashGear.Capture.App.History;
using CashGear.Capture.App.Hotkeys;
using CashGear.Capture.App.Output;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Recording;
using CashGear.Capture.App.Region;
using CashGear.Capture.App.Scrolling;
using CashGear.Capture.App.Settings;
using CashGear.Capture.App.SingleInstance;
using CashGear.Capture.App.Tray;
using CashGear.Capture.Core;
using CashGear.Capture.Core.History;
using CashGear.Capture.Core.Hotkeys;
using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Editor.Interaction;
using CashGear.Capture.Engine;
using CashGear.Capture.Outputs;
using CashGear.Capture.Recording;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace CashGear.Capture.App.Hosting;

internal static class AppHost
{
    public static IHost Build(string[] args, AppPaths paths, InstallEvents install)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ApplicationName = "CashGear.Capture",
            ContentRootPath = AppContext.BaseDirectory,
            DisableDefaults = true, // no appsettings.json / env config / console logging
        });

        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        }));

        builder.Services.AddSerilog();
        builder.Services.AddSingleton<IHostLifetime, WpfHostLifetime>();
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));

        builder.Services.AddCaptureCore(paths);
        builder.Services.AddCaptureEngine();
        builder.Services.AddScreenRecording();

        builder.Services.AddSingleton<IUiDispatcher, WpfUiDispatcher>();
        builder.Services.AddSingleton<IAppLifetime, WpfAppLifetime>();
        builder.Services.AddSingleton<IOutputService, WpfOutputService>();
        builder.Services.AddOutputDestinations();
        builder.Services.AddSingleton<IPrinter, WpfPrinter>();
        builder.Services.AddSingleton<IDestinationSource, AppDestinations>();
        builder.Services.AddSingleton<OutputWorkflow>();
        builder.Services.AddSingleton<IEditorOutputs>(sp => sp.GetRequiredService<OutputWorkflow>());
        builder.Services.AddSingleton<IOwnWindows, WpfOwnWindows>();
        builder.Services.AddSingleton<IStartupRegistration, RegistryStartupRegistration>();
        builder.Services.AddSingleton(install);
        builder.Services.AddSingleton<IShell, ExplorerShell>();
        builder.Services.AddSingleton<IUpdateBackend, VelopackUpdateBackend>();
        builder.Services.AddSingleton<UpdateService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<UpdateService>());
        builder.Services.AddSingleton<IFolderPicker, WpfFolderPicker>();
        builder.Services.AddSingleton<IFilePicker, WpfFilePicker>();
        builder.Services.AddSingleton<SettingsWindowService>();
        builder.Services.AddSingleton<SettingsFiles>();
        builder.Services.AddSingleton<ReleaseNotes>();
        builder.Services.AddSingleton<ISettingsFileDialogs, WpfSettingsFileDialogs>();
        builder.Services.AddSingleton<RegionSelector>();
        builder.Services.AddSingleton<IScrollPlatform, Win32ScrollPlatform>();
        builder.Services.AddSingleton<ScrollTargetFinder>();
        builder.Services.AddSingleton<ScrollingCaptureService>();
        builder.Services.AddSingleton<IKeyboardState, Win32KeyboardState>();
        builder.Services.AddSingleton<CashGear.Capture.Core.Recording.IPowerEvents, WindowsPowerEvents>();
        builder.Services.AddSingleton<RecordingController>();
        builder.Services.AddSingleton<IRecordingPicker, WpfRecordingPicker>();
        builder.Services.AddSingleton<AddOverlaysController>();
        builder.Services.AddSingleton<CashGear.Capture.VideoEditor.Export.ProjectExporter>();
        builder.Services.AddSingleton(sp => new CashGear.Capture.VideoEditor.Playback.MediaPreviews(
            System.IO.Path.Combine(sp.GetRequiredService<AppPaths>().DataDirectory, "VideoEditor", "cache"),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CashGear.Capture.VideoEditor.Playback.MediaPreviews>>()));
        builder.Services.AddSingleton<CashGear.Capture.Transcription.ITranscriber, CashGear.Capture.Transcription.WhisperTranscriber>();
        builder.Services.AddSingleton(sp => new CashGear.Capture.Transcription.WhisperModelStore(
            System.IO.Path.Combine(sp.GetRequiredService<AppPaths>().DataDirectory, "Models", "Whisper"),
            new System.Net.Http.HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan },
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CashGear.Capture.Transcription.WhisperModelStore>>(),
            alsoIn: [System.IO.Path.Combine(AppContext.BaseDirectory, "models")]));
        builder.Services.AddSingleton<VideoEditor.VideoEditorWindows>();
        builder.Services.AddSingleton<VideoEditor.IVideoEditorLauncher>(sp => sp.GetRequiredService<VideoEditor.VideoEditorWindows>());
        builder.Services.AddSingleton<InterruptedRecordings>();
        builder.Services.AddSingleton<Diagnostics.DiagnosticsExporter>();
        builder.Services.AddSingleton<Diagnostics.DiagnosticsService>();
        builder.Services.AddSingleton<ToolStyleMemory>();
        builder.Services.AddSingleton<CashGear.Capture.Editor.Presets.IPresetStore, CashGear.Capture.Editor.Presets.JsonPresetStore>();
        builder.Services.AddSingleton<EditorWindowService>();
        builder.Services.AddSingleton<IEditorLauncher>(sp => sp.GetRequiredService<EditorWindowService>());
        builder.Services.AddSingleton<IHotkeyService, Win32HotkeyService>();
        builder.Services.AddSingleton<HotkeyRegistrar>();
        builder.Services.AddSingleton<HotkeyRouter>();
        builder.Services.AddSingleton<ICaptureCountdown, WpfCaptureCountdown>();
        builder.Services.AddSingleton<ICaptureHistory, FileCaptureHistory>();
        builder.Services.AddSingleton<HistoryActions>();
        builder.Services.AddSingleton<HistoryWindowService>();
        builder.Services.AddSingleton<ImageTools.IImageFilePicker, ImageTools.WpfImageFilePicker>();
        builder.Services.AddSingleton<ImageTools.ImageToolWindows>();
        builder.Services.AddSingleton<ThemeService>();
        builder.Services.AddSingleton<CaptureCoordinator>();
        builder.Services.AddSingleton<Toolbar.CaptureToolbarService>();
        builder.Services.AddSingleton<Tools.ScreenTools>();
        builder.Services.AddSingleton<UiReadySignal>();
        builder.Services.AddSingleton<TrayIconHost>();
        builder.Services.AddSingleton<ITrayNotifier>(sp => sp.GetRequiredService<TrayIconHost>());
        builder.Services.AddSingleton<TrayViewModel>();
        builder.Services.AddSingleton<InstanceCommandDispatcher>();
        builder.Services.AddHostedService<InstancePipeServer>();

        return builder.Build();
    }
}
