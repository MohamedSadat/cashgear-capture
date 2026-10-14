using System.Windows;
using System.Windows.Threading;
using CashGear.Capture.App.Hosting;
using CashGear.Capture.App.SingleInstance;
using CashGear.Capture.App.Tray;
using CashGear.Capture.Core.Instance;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Engine.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.App;

/// <summary>The WPF application. Created by <see cref="Program.Main"/> after the host has started.</summary>
public partial class CaptureApplication : Application
{
    private readonly IServiceProvider _services;
    private readonly InstanceCommand _startupCommand;
    private readonly ILogger<CaptureApplication> _logger;
    private TrayIconHost? _tray;

    public CaptureApplication(IServiceProvider services, InstanceCommand startupCommand)
    {
        _services = services;
        _startupCommand = startupCommand;
        _logger = services.GetRequiredService<ILogger<CaptureApplication>>();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        var diagnostics = _services.GetRequiredService<IStartupDiagnostics>();
        diagnostics.LogDpiAwareness(); // per thread, so it must run here on the UI thread
        _ = _services.GetRequiredService<ISettingsService>(); // load (or create) settings up front

        _tray = _services.GetRequiredService<TrayIconHost>();
        _tray.Show(_services.GetRequiredService<TrayViewModel>());
        _services.GetRequiredService<UiReadySignal>().SetReady();
        _ = Task.Run(diagnostics.LogDisplaysAndBackends); // first WinRT probe takes ~1 s; keep it off the UI thread

        // If the host stops on its own (e.g. a background service fails), take the UI down with it.
        _services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(ShutdownFromHost);

        LogStarted(_startupCommand);

        if (_startupCommand != InstanceCommand.Activate)
        {
            _ = _services.GetRequiredService<InstanceCommandDispatcher>().DispatchAsync(_startupCommand);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        LogExiting(e.ApplicationExitCode);
        _tray?.Dispose();
        base.OnExit(e);
    }

    private void ShutdownFromHost()
    {
        if (!Dispatcher.HasShutdownStarted)
        {
            Dispatcher.InvokeAsync(Shutdown);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogUnhandledUi(e.Exception);
        e.Handled = true;
        _tray?.Notify("CashGear Capture", $"Something went wrong: {e.Exception.Message}", NotificationKind.Error);
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e) =>
        LogUnhandledDomain(e.ExceptionObject as Exception, e.IsTerminating);

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogUnobservedTask(e.Exception);
        e.SetObserved();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "UI started; startup command {Command}")]
    private partial void LogStarted(InstanceCommand command);

    [LoggerMessage(Level = LogLevel.Information, Message = "UI exiting (code {ExitCode})")]
    private partial void LogExiting(int exitCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception on the UI thread")]
    private partial void LogUnhandledUi(Exception ex);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Unhandled exception (terminating: {IsTerminating})")]
    private partial void LogUnhandledDomain(Exception? ex, bool isTerminating);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unobserved task exception")]
    private partial void LogUnobservedTask(Exception ex);
}
