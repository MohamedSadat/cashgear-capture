using System.Windows;
using CashGear.Capture.App.Hosting;
using CashGear.Capture.App.Output;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Editor.Document;
using CashGear.Capture.Editor.Imaging;
using CashGear.Capture.Editor.Interaction;
using CashGear.Capture.Editor.Presets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace CashGear.Capture.App.Editor;

/// <summary>Opens captures in the editor.</summary>
public interface IEditorLauncher
{
    /// <summary>
    /// Opens <paramref name="image"/> in a new editor window. The pixels are copied, so the caller may dispose the
    /// image once this completes. <paramref name="savedPath"/> is the file it was already saved to, if any (Ctrl+S
    /// then updates that file). <paramref name="pending"/> are the workflow's steps after the editor, which its
    /// Continue button runs.
    /// </summary>
    Task OpenAsync(CapturedImage image, string? savedPath, IReadOnlyList<WorkflowStep>? pending = null, CancellationToken ct = default);
}

/// <summary>Creates editor windows (one per capture) and closes them all on exit, asking about unsaved changes.</summary>
public sealed partial class EditorWindowService(
    IServiceProvider services,
    IUiDispatcher ui,
    ISettingsService settings,
    ILogger<EditorWindowService> logger) : IEditorLauncher
{
    private readonly List<EditorWindow> _windows = [];

    public int OpenCount => _windows.Count;

    public async Task OpenAsync(CapturedImage image, string? savedPath, IReadOnlyList<WorkflowStep>? pending = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ct.ThrowIfCancellationRequested();

        // Copy the pixels here, off the UI thread; from then on the document owns them.
        var picture = SkiaImaging.ToSKImage(image);
        var metadata = image.Metadata;
        try
        {
            await ui.InvokeAsync(() => Show(picture, metadata, savedPath, pending)).ConfigureAwait(false);
        }
        catch
        {
            picture.Dispose();
            throw;
        }
    }

    /// <summary>Closes every editor. False if the user kept one open (so the app should not exit).</summary>
    public async Task<bool> CloseAllAsync()
    {
        foreach (var window in _windows.ToList())
        {
            if (!await window.RequestCloseAsync())
            {
                return false;
            }
        }

        return true;
    }

    private void Show(SKImage picture, CaptureMetadata metadata, string? savedPath, IReadOnlyList<WorkflowStep>? pending)
    {
        var theme = settings.Current.Appearance.Theme;
        var document = new EditorDocument(picture);
        EditorWindow? window = null;
        var dialogs = new DeferredDialogs(() => window!, theme);
        var viewModel = new EditorViewModel(
            document,
            metadata,
            savedPath,
            services.GetRequiredService<ToolStyleMemory>(),
            services.GetRequiredService<IOutputService>(),
            settings,
            services.GetRequiredService<AppPaths>(),
            services.GetRequiredService<ICaptureCounter>(),
            dialogs,
            services.GetRequiredService<ILogger<EditorViewModel>>(),
            services.GetService<IEditorOutputs>(),
            pending,
            services.GetService<IPresetStore>());

        var created = new EditorWindow(viewModel, theme);
        window = created;
        created.Closed += (_, _) => _windows.Remove(created);
        _windows.Add(created);
        created.Show();
        created.Activate();
        LogOpened(picture.Width, picture.Height, savedPath);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Opened a {Width}x{Height} capture in the editor (file: {Path})")]
    private partial void LogOpened(int width, int height, string? path);

    /// <summary>The dialogs need the window as their owner, and the window needs the view model that needs the dialogs.</summary>
    private sealed class DeferredDialogs(Func<Window> owner, AppTheme theme) : IEditorDialogs
    {
        private WpfEditorDialogs Dialogs => new(owner(), theme);

        public string? AskSavePath(string folder, string fileName, ImageFileFormat format) => Dialogs.AskSavePath(folder, fileName, format);

        public SKSizeI? AskImageSize(SKSizeI current) => Dialogs.AskImageSize(current);

        public bool ShowEffects(EffectsViewModel viewModel) => Dialogs.ShowEffects(viewModel);

        public bool ShowImageDialog(ParameterDialogViewModel viewModel) => Dialogs.ShowImageDialog(viewModel);

        public string? PickImageFile() => Dialogs.PickImageFile();

        public string? AskName(string title, string suggested) => Dialogs.AskName(title, suggested);

        public SaveChangesChoice AskSaveChanges(string documentName) => Dialogs.AskSaveChanges(documentName);

        public void ShowError(string message) => Dialogs.ShowError(message);
    }
}
