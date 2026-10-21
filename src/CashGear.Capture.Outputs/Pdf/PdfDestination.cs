using CashGear.Capture.Core.Settings;
using CashGear.Capture.Editor.Imaging;
using CashGear.Capture.Editor.Pdf;
using CashGear.Capture.Outputs.Office;
using CashGear.Capture.Outputs.Resources;

namespace CashGear.Capture.Outputs.Pdf;

/// <summary>
/// Saves the capture as a one-page PDF in the capture folder, named like the capture (" (2)" when the name is
/// taken), on the paper and with the margins of Settings › Destinations.
/// </summary>
public sealed class PdfDestination(PdfSettings settings, string captureFolder) : IOutputDestination
{
    public string Id => DestinationIds.Pdf;

    public string DisplayName => OutputsStrings.Pdf;

    public bool SupportsVideo => false;

    public async Task<OutputResult> SendAsync(OutputItem item, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Image is not { } capture)
        {
            return OutputResult.Skip(OutputsStrings.PdfPicturesOnly);
        }

        string? target = null;
        try
        {
            target = OfficeFiles.NewPath(captureFolder, item.Name, ".pdf");
            using var image = SkiaImaging.ToSKImage(capture);
            var options = new PdfOptions(settings.PageSize, settings.Orientation, settings.MarginMm, settings.Fit);
            await PdfWriter.WriteAsync(target, [PdfPicture.FromImage(image, capture.Metadata.Dpi.ScaleX)], options, item.Title ?? item.Name, ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return OutputResult.Failed(target is null ? OutputsStrings.PdfNotSaved(ex.Message) : OutputsStrings.PdfNotSavedNamed(Path.GetFileName(target), ex.Message));
        }

        return new OutputResult(true, OutputsStrings.SavedFile(Path.GetFileName(target))) { Path = target };
    }

    public Task<OutputResult> TestAsync(CancellationToken ct) => Task.FromResult(OfficeFiles.CheckFolder(captureFolder));
}
