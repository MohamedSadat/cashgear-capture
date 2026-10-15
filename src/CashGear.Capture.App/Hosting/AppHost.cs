using CashGear.Capture.App.Capture;
using CashGear.Capture.App.Editor;
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
using CashGear.Capture.Core.Hotkeys;
using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Editor.Interaction;
using CashGear.Capture.Engine;
using CashGear.Capture.Recording;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace CashGear.Capture.App.Hosting;

internal static class AppHost
{
    public static IHost Build(string[] args, AppPaths paths)
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
        builder.Services.AddSingleton<IOwnWindows, WpfOwnWindows>();
        builder.Services.AddSingleton<IStartupRegistration, RegistryStartupRegistration>();
        builder.Services.AddSingleton<IFolderPicker, WpfFolderPicker>();
        builder.Services.AddSingleton<SettingsWindowService>();
        builder.Services.AddSingleton<RegionSelector>();
        builder.Services.AddSingleton<IScrollPlatform, Win32ScrollPlatform>();
        builder.Services.AddSingleton<ScrollTargetFinder>();
        builder.Services.AddSingleton<ScrollingCaptureService>();
        builder.Services.AddSingleton<RecordingController>();
        builder.Services.AddSingleton<ToolStyleMemory>();
        builder.Services.AddSingleton<EditorWindowService>();
        builder.Services.AddSingleton<IEditorLauncher>(sp => sp.GetRequiredService<EditorWindowService>());
        builder.Services.AddSingleton<IHotkeyService, Win32HotkeyService>();
        builder.Services.AddSingleton<HotkeyRegistrar>();
        builder.Services.AddSingleton<HotkeyRouter>();
        builder.Services.AddSingleton<CaptureCoordinator>();
        builder.Services.AddSingleton<UiReadySignal>();
        builder.Services.AddSingleton<TrayIconHost>();
        builder.Services.AddSingleton<ITrayNotifier>(sp => sp.GetRequiredService<TrayIconHost>());
        builder.Services.AddSingleton<TrayViewModel>();
        builder.Services.AddSingleton<InstanceCommandDispatcher>();
        builder.Services.AddHostedService<InstancePipeServer>();

        return builder.Build();
    }
}
