using System.IO;
using System.Windows;
using System.Windows.Input;
using CashGear.Capture.App.Platform;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Editor.Batch;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace CashGear.Capture.App.ImageTools;

/// <summary>The list template of the image tools (a resource dictionary with its own class, so it loads anywhere).</summary>
public partial class ImageListTemplate : ResourceDictionary
{
    public ImageListTemplate() => InitializeComponent();
}

/// <summary>
/// What the image tool windows share: theming, pictures dropped on them, Esc to close them, and stopping their work
/// when closed.
/// </summary>
public class ImageToolWindow : Window, IThemedWindow
{
    public void ApplyTheme(AppTheme theme) => Theming.Apply(this, theme);

    /// <summary>Esc closes the window (its Cancel button stops a run, it doesn't close it); an open drop-down takes Esc first.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    /// <summary>Wires the window to its view model (after <c>InitializeComponent</c>).</summary>
    protected void Attach(object viewModel, AppTheme theme)
    {
        DataContext = viewModel;
        ApplyTheme(theme);
        AllowDrop = true;
        Drop += OnDrop;
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
    }

    protected virtual ImageListViewModel? List => null;

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            List?.Add(paths);
            e.Handled = true;
        }
    }
}

public partial class BatchWindow : ImageToolWindow
{
    public BatchWindow(BatchViewModel viewModel, AppTheme theme)
    {
        InitializeComponent();
        Attach(viewModel, theme);
        Closed += (_, _) => (DataContext as BatchViewModel)?.Dispose();
    }

    protected override ImageListViewModel? List => (DataContext as BatchViewModel)?.List;
}

public partial class CombineWindow : ImageToolWindow
{
    public CombineWindow(CombineViewModel viewModel, AppTheme theme)
    {
        InitializeComponent();
        Attach(viewModel, theme);
    }

    protected override ImageListViewModel? List => (DataContext as CombineViewModel)?.List;
}

public partial class PdfWindow : ImageToolWindow
{
    public PdfWindow(PdfToolViewModel viewModel, AppTheme theme)
    {
        InitializeComponent();
        Attach(viewModel, theme);
        Closed += (_, _) => (DataContext as PdfToolViewModel)?.Dispose();
    }

    protected override ImageListViewModel? List => (DataContext as PdfToolViewModel)?.List;
}

/// <summary>Opens Batch Process, Combine Images and Images to PDF (one window each), or brings the open one to the front. UI thread only.</summary>
public sealed class ImageToolWindows(IServiceProvider services, ISettingsService settings)
{
    private readonly Dictionary<Type, Window> _open = [];

    public void ShowBatch() => Show(() => new BatchWindow(ActivatorUtilities.CreateInstance<BatchViewModel>(services), settings.Current.Appearance.Theme));

    public void ShowCombine() => Show(() => new CombineWindow(ActivatorUtilities.CreateInstance<CombineViewModel>(services), settings.Current.Appearance.Theme));

    public void ShowPdf() => Show(() => new PdfWindow(ActivatorUtilities.CreateInstance<PdfToolViewModel>(services), settings.Current.Appearance.Theme));

    private void Show<T>(Func<T> create)
        where T : Window
    {
        if (_open.TryGetValue(typeof(T), out var open))
        {
            if (open.WindowState == WindowState.Minimized)
            {
                open.WindowState = WindowState.Normal;
            }

            open.Activate();
            return;
        }

        var window = create();
        window.Closed += (_, _) => _open.Remove(typeof(T));
        _open[typeof(T)] = window;
        window.Show();
        window.Activate();
    }
}

/// <summary>Open, folder and save dialogs for the image tools, owned by the window in front.</summary>
internal sealed class WpfImageFilePicker : IImageFilePicker
{
    private static Window? Owner => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);

    public IReadOnlyList<string> PickImages(string title)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = ImageFiles.DialogFilter, Multiselect = true, CheckFileExists = true };
        return (Owner is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog()) == true ? dialog.FileNames : [];
    }

    public string? PickFolder(string title, string? initialFolder)
    {
        var dialog = new OpenFolderDialog { Title = title, InitialDirectory = initialFolder is { } folder && Directory.Exists(folder) ? folder : null };
        return (Owner is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog()) == true ? dialog.FolderName : null;
    }

    public string? PickSavePath(string title, string filter, string fileName, string? folder)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            Filter = filter,
            FileName = fileName,
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = folder is { } f && Directory.Exists(f) ? f : null,
        };
        return (Owner is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog()) == true ? dialog.FileName : null;
    }
}
