using CashGear.Capture.App.Capture;
using CashGear.Capture.App.Output;
using CashGear.Capture.App.Region;
using CashGear.Capture.App.SingleInstance;
using CashGear.Capture.App.Tray;
using CashGear.Capture.Core;
using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Engine;
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

        builder.Services.AddSingleton<IUiDispatcher, WpfUiDispatcher>();
        builder.Services.AddSingleton<IAppLifetime, WpfAppLifetime>();
        builder.Services.AddSingleton<IOutputService, WpfOutputService>();
        builder.Services.AddSingleton<RegionSelector>();
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
