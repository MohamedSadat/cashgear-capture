using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using CashGear.Capture.App.Tray;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Engine.Tests;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.App.Tests.Tray;

/// <summary>The warning shown when Windows never lets the app add its tray icon.</summary>
[Collection(DesktopCollection.Name)]
public sealed class TrayIconRefusedWindowTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "cashgear-tray-tests");
    private static readonly AppPaths Paths = new(Path.Combine(Root, "settings"), Path.Combine(Root, "logs"), Path.Combine(Root, "captures"));

    [Fact]
    [Trait("Category", InteractiveDesktop.Category)] // puts the real window on screen
    public void The_warning_does_not_keep_the_app_from_exiting()
    {
        // It was a message box, whose modal loop kept the UI thread from finishing: "--exit" logged "UI exiting" but
        // the process lived on until the user clicked OK, and so would a restart to update.
        InteractiveDesktop.Require();
        TrayIconRefusedWindow? warning = null;
        var visibleAtExit = false;
        var closed = false;
        ExceptionDispatchInfo? failure = null;

        var ui = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                var host = new TrayIconHost(new InlineUiDispatcher(), Paths, new MutableSettings(), NullLogger<TrayIconHost>.Instance);

                // Shown from a dispatcher callback, as the retry timer does after the last refusal.
                dispatcher.InvokeAsync(() =>
                {
                    warning = host.ShowRefusedWarning();
                    warning.Closed += (_, _) => closed = true;
                });

                // Then what Application.Shutdown does: OnExit disposes the host, and the dispatcher shuts down.
                dispatcher.InvokeAsync(
                    () =>
                    {
                        visibleAtExit = warning?.IsVisible == true;
                        host.Dispose();
                        dispatcher.InvokeShutdown();
                    },
                    DispatcherPriority.Background);

                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        })
        {
            IsBackground = true, // a stuck UI thread must not keep the test run alive
            Name = "WPF test STA",
        };
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();

        ui.Join(TimeSpan.FromSeconds(10)).Should().BeTrue("the UI thread must finish while the warning is up");
        failure?.Throw();
        visibleAtExit.Should().BeTrue();
        closed.Should().BeTrue("exiting closes the warning");
    }

    [Fact]
    public void The_warning_names_the_log_folder()
    {
        TrayIconRefusedWindow.Message(Paths.LogDirectory).Should().EndWith($"Details are in the log: {Paths.LogDirectory}");
    }
}
