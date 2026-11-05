using System.Collections.ObjectModel;
using System.IO;
using CashGear.Capture.App.Resources;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Editor.Presets;
using CashGear.Capture.VideoEditor.Export;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CashGear.Capture.App.VideoEditor;

/// <summary>A preset in the batch's list, ticked or not.</summary>
public sealed partial class BatchPreset(NamedExport export, Action changed) : ObservableObject
{
    public NamedExport Export => export;

    public string Name => export.Name;

    [ObservableProperty]
    private bool _isChosen;

    partial void OnIsChosenChanged(bool value) => changed();
}

/// <summary>What the batch window asks of the desktop (behind an interface for tests).</summary>
public interface IBatchPickers
{
    /// <summary>Projects and recordings to add.</summary>
    IReadOnlyList<string> PickProjects();

    /// <summary>A folder to export to, or null.</summary>
    string? PickFolder(string current);
}

/// <summary>
/// Several presets of one project, or a queue of projects: each project in each ticked preset, one after another in the
/// background, with progress in the tray. The file names say which is which.
/// </summary>
public sealed partial class BatchExportViewModel : ObservableObject
{
    private readonly ExportQueue _queue;
    private readonly IBatchPickers? _pickers;

    public BatchExportViewModel(ExportQueue queue, IPresetStore? presets, IEnumerable<BatchSource> sources, string folder, IBatchPickers? pickers)
    {
        _queue = queue;
        _pickers = pickers;
        _folder = folder;
        Projects = [.. sources];
        Projects.CollectionChanged += (_, _) => Update();
        Presets = [.. BuiltInExports.All.Concat(presets?.Current.SavedExports ?? []).Select(e => new BatchPreset(e, Update))];
        _selectedProject = Projects.FirstOrDefault();
    }

    public ObservableCollection<BatchSource> Projects { get; }

    public IReadOnlyList<BatchPreset> Presets { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveProjectCommand))]
    private BatchSource? _selectedProject;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string _folder;

    /// <summary>How many files it will make.</summary>
    public string CountText => VideoEditorText.BatchCount(Projects.Count * Presets.Count(p => p.IsChosen));

    public bool CanAddProjects => _pickers is not null;

    /// <summary>Raised when the window should close.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Raised when the exports have gone to the queue: the app shows it.</summary>
    public event EventHandler? Started;

    [RelayCommand]
    private void AddProjects()
    {
        foreach (var path in _pickers?.PickProjects() ?? [])
        {
            if (!Projects.Any(p => string.Equals(p.File, path, StringComparison.OrdinalIgnoreCase)))
            {
                Projects.Add(new BatchSource(Path.GetFileNameWithoutExtension(path), path));
            }
        }

        SelectedProject ??= Projects.FirstOrDefault();
    }

    private bool HasSelectedProject() => SelectedProject is not null;

    [RelayCommand(CanExecute = nameof(HasSelectedProject))]
    private void RemoveProject()
    {
        if (SelectedProject is { } project)
        {
            Projects.Remove(project);
            SelectedProject = Projects.FirstOrDefault();
        }
    }

    [RelayCommand]
    private void Browse()
    {
        if (_pickers?.PickFolder(Folder) is { } picked)
        {
            Folder = picked;
        }
    }

    private bool CanStart() => Projects.Count > 0 && Presets.Any(p => p.IsChosen) && !string.IsNullOrWhiteSpace(Folder);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start()
    {
        var chosen = Presets.Where(p => p.IsChosen).Select(p => p.Export).ToList();
        foreach (var project in Projects)
        {
            _queue.Add(project, chosen, Folder);
        }

        Started?.Invoke(this, EventArgs.Empty);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void Update()
    {
        OnPropertyChanged(nameof(CountText));
        StartCommand.NotifyCanExecuteChanged();
    }
}
