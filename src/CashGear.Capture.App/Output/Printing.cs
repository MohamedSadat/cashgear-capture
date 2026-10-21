using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CashGear.Capture.App.Hosting;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Outputs;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.App.Output;

/// <summary>Prints captures (behind an interface so tests never print).</summary>
public interface IPrinter
{
    /// <summary>The installed printers, by name (for Settings).</summary>
    IReadOnlyList<string> Printers();

    Task<OutputResult> PrintAsync(CapturedImage image, string title, PrintSettings settings, CancellationToken ct);

    /// <summary>Checks the printer is there and says how captures will be printed; prints nothing.</summary>
    Task<OutputResult> CheckAsync(PrintSettings settings, CancellationToken ct);
}

/// <summary>
/// Prints with WPF: the print dialog (or straight to the chosen or default printer), one page per capture, laid
/// out by <see cref="PrintLayout"/> on the page the printer reports.
/// </summary>
internal sealed partial class WpfPrinter(IUiDispatcher ui, ILogger<WpfPrinter> logger) : IPrinter
{
    public IReadOnlyList<string> Printers()
    {
        try
        {
            using var server = new LocalPrintServer();
            return [.. server.GetPrintQueues([EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections]).Select(q => q.FullName).Order(StringComparer.CurrentCultureIgnoreCase)];
        }
        catch (Exception ex) when (ex is PrintSystemException or InvalidOperationException)
        {
            LogNoPrinters(ex);
            return [];
        }
    }

    public async Task<OutputResult> PrintAsync(CapturedImage image, string title, PrintSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(settings);
        var picture = await Task.Run(image.ToBitmapSource, ct).ConfigureAwait(false);
        var size = new PixelSize(image.Width, image.Height);
        var scale = image.Metadata.Dpi.ScaleX;
        return await ui.InvokeAsync(() =>
        {
            try
            {
                var dialog = new PrintDialog { UserPageRangeEnabled = false };
                if (settings.Printer is { } name)
                {
                    if (Find(name) is { } queue)
                    {
                        dialog.PrintQueue = queue;
                    }
                    else if (!settings.ShowDialog)
                    {
                        return OutputResult.Failed($"The printer \"{name}\" isn't installed (Settings › Destinations).");
                    }
                }

                dialog.PrintTicket.PageOrientation = PrintLayout.IsLandscape(settings.Orientation, size) ? System.Printing.PageOrientation.Landscape : System.Printing.PageOrientation.Portrait;
                if (settings.ShowDialog && dialog.ShowDialog() != true)
                {
                    return OutputResult.Skip("Printing was cancelled.");
                }

                var page = new Size(dialog.PrintableAreaWidth, dialog.PrintableAreaHeight);
                var placement = PrintLayout.Place(page.Width, page.Height, settings.MarginMm, size, scale, settings.Fit);
                dialog.PrintDocument(PrintPage.Build(picture, page, placement).DocumentPaginator, "CashGear Capture – " + title);
                LogPrinted(dialog.PrintQueue.FullName);
                return OutputResult.Done($"Printed on {dialog.PrintQueue.FullName}");
            }
            catch (Exception ex) when (ex is PrintSystemException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                LogPrintFailed(ex);
                return OutputResult.Failed("Couldn't print: " + ex.Message);
            }
        }).ConfigureAwait(false);
    }

    public Task<OutputResult> CheckAsync(PrintSettings settings, CancellationToken ct) => ui.InvokeAsync(() =>
    {
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            using var server = new LocalPrintServer();
            var queue = settings.Printer is { } name ? Find(name) : server.DefaultPrintQueue;
            if (queue is null)
            {
                return OutputResult.Failed(settings.Printer is null ? "Windows has no default printer." : $"The printer \"{settings.Printer}\" isn't installed.");
            }

            var how = settings.Fit switch
            {
                PrintFit.FillPage => "filling the page",
                PrintFit.ActualSize => "at the size it was on the screen",
                _ => "as large as fits",
            };
            var dialog = settings.ShowDialog ? "; the print dialog lets you change it each time" : string.Empty;
            return OutputResult.Done($"Prints on {queue.FullName}, one capture per page {how}, {settings.MarginMm:0.#} mm margins{dialog}.");
        }
        catch (Exception ex) when (ex is PrintSystemException or InvalidOperationException)
        {
            return OutputResult.Failed("The printers can't be read: " + ex.Message);
        }
    });

    private static PrintQueue? Find(string name)
    {
        using var server = new LocalPrintServer();
        return server.GetPrintQueues([EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections])
            .FirstOrDefault(q => string.Equals(q.FullName, name, StringComparison.OrdinalIgnoreCase));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Printed a capture on {Printer}")]
    private partial void LogPrinted(string printer);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Printing failed")]
    private partial void LogPrintFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The printers can't be listed")]
    private partial void LogNoPrinters(Exception ex);
}

/// <summary>The printed page: white, with the picture where <see cref="PrintLayout"/> put it, cut off at the margins.</summary>
public static class PrintPage
{
    public static FixedDocument Build(BitmapSource picture, Size page, PrintPlacement placement)
    {
        var area = new Canvas { Width = placement.Area.Width, Height = placement.Area.Height, ClipToBounds = true };
        var image = new Image { Source = picture, Width = placement.Picture.Width, Height = placement.Picture.Height, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        Canvas.SetLeft(image, placement.Picture.X - placement.Area.X);
        Canvas.SetTop(image, placement.Picture.Y - placement.Area.Y);
        area.Children.Add(image);

        var fixedPage = new FixedPage { Width = page.Width, Height = page.Height, Background = Brushes.White };
        FixedPage.SetLeft(area, placement.Area.X);
        FixedPage.SetTop(area, placement.Area.Y);
        fixedPage.Children.Add(area);

        var content = new PageContent();
        ((IAddChild)content).AddChild(fixedPage);
        var document = new FixedDocument();
        document.DocumentPaginator.PageSize = page;
        document.Pages.Add(content);
        return document;
    }
}
