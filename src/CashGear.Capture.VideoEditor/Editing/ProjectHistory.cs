using CashGear.Capture.VideoEditor.Model;

namespace CashGear.Capture.VideoEditor.Editing;

/// <summary>A reversible change to a project, like the image editor's <c>IEditorCommand</c>.</summary>
public interface IProjectCommand
{
    /// <summary>Lower-case description for the Undo/Redo tooltips ("split").</summary>
    string Name { get; }

    VideoProject Do(VideoProject project);

    VideoProject Undo(VideoProject project);
}

/// <summary>A command that can absorb the next one into a single undo step (dragging a clip, a slider).</summary>
public interface IMergeableProjectCommand : IProjectCommand
{
    /// <summary>Takes over <paramref name="next"/>'s result if it continues this change; false to keep them separate.</summary>
    bool TryMerge(IProjectCommand next);
}

/// <summary>
/// Any edit: the project before and after it (projects are immutable, so this is all undo needs). Changes with the
/// same non-null merge key merge into one undo step.
/// </summary>
public sealed class ProjectChange(string name, VideoProject before, VideoProject after, string? mergeKey = null) : IMergeableProjectCommand
{
    public string Name { get; } = name;

    public VideoProject Before { get; } = before;

    public VideoProject After { get; private set; } = after;

    public string? MergeKey { get; } = mergeKey;

    public VideoProject Do(VideoProject project) => After;

    public VideoProject Undo(VideoProject project) => Before;

    public bool TryMerge(IProjectCommand next)
    {
        if (MergeKey is null || next is not ProjectChange other || other.MergeKey != MergeKey || !ReferenceEquals(other.Before, After))
        {
            return false;
        }

        After = other.After;
        return true;
    }
}

/// <summary>
/// The project being edited with its undo and redo stacks and the "saved" marker behind the unsaved-changes prompt;
/// the same rules as the image editor's <c>UndoHistory</c>.
/// </summary>
public sealed class ProjectHistory(VideoProject project)
{
    private readonly List<IProjectCommand> _undo = [];
    private readonly List<IProjectCommand> _redo = [];

    /// <summary>The undo depth at the last save; null once that state was discarded (only a new save cleans the project).</summary>
    private int? _savedAt = 0;

    public event EventHandler? Changed;

    public VideoProject Project { get; private set; } = project;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public string? UndoName => CanUndo ? _undo[^1].Name : null;

    public string? RedoName => CanRedo ? _redo[^1].Name : null;

    public int UndoCount => _undo.Count;

    /// <summary>Whether the project differs from what was last saved (or, if never saved, from how it was opened).</summary>
    public bool IsDirty => _savedAt != _undo.Count;

    /// <summary>Applies <paramref name="command"/> and records it (merged into the previous step where that step allows it).</summary>
    public void Execute(IProjectCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        Project = command.Do(Project);
        DiscardRedo();

        // Never merge into the saved state: that state must stay reachable for IsDirty to be right.
        var merged = _savedAt != _undo.Count && _undo.Count > 0 && _undo[^1] is IMergeableProjectCommand last && last.TryMerge(command);
        if (!merged)
        {
            _undo.Add(command);
        }

        OnChanged();
    }

    /// <summary>Records an edit that made <paramref name="after"/> from the current project; nothing when it changed nothing.</summary>
    public void Apply(string name, VideoProject after, string? mergeKey = null)
    {
        ArgumentNullException.ThrowIfNull(after);
        if (ReferenceEquals(after, Project))
        {
            return;
        }

        Execute(new ProjectChange(name, Project, after, mergeKey));
    }

    public void Undo()
    {
        if (!CanUndo)
        {
            return;
        }

        var command = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Project = command.Undo(Project);
        _redo.Add(command);
        OnChanged();
    }

    public void Redo()
    {
        if (!CanRedo)
        {
            return;
        }

        var command = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        Project = command.Do(Project);
        _undo.Add(command);
        OnChanged();
    }

    /// <summary>The current state is what's on disk now (<paramref name="saved"/>: the project as saved, with its paths).</summary>
    public void MarkSaved(VideoProject? saved = null)
    {
        if (saved is not null)
        {
            // The saved copy differs only in where its media is written down: keep editing it, without an undo step.
            Project = saved;
        }

        _savedAt = _undo.Count;
        OnChanged();
    }

    /// <summary>
    /// Replaces the project without an undo step (media located again): the history starts again from it, since the
    /// steps before would bring the old paths back. The project counts as unsaved.
    /// </summary>
    public void Replace(VideoProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Project = project;
        _undo.Clear();
        _redo.Clear();
        _savedAt = null;
        OnChanged();
    }

    private void DiscardRedo()
    {
        if (_redo.Count == 0)
        {
            return;
        }

        if (_savedAt > _undo.Count)
        {
            _savedAt = null; // the saved state was in the part being thrown away
        }

        _redo.Clear();
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
