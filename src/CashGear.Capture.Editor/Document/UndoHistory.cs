namespace CashGear.Capture.Editor.Document;

/// <summary>A reversible change to an <see cref="EditorDocument"/>.</summary>
public interface IEditorCommand
{
    /// <summary>Lower-case description for the Undo/Redo tooltips ("move arrow").</summary>
    string Name { get; }

    void Do(EditorDocument document);

    void Undo(EditorDocument document);
}

/// <summary>A command that can absorb the next one into a single undo step (dragging a slider, nudging with the arrow keys).</summary>
public interface IMergeableCommand : IEditorCommand
{
    /// <summary>Takes over <paramref name="next"/>'s result if it continues this change; false to keep them separate.</summary>
    bool TryMerge(IEditorCommand next);
}

/// <summary>
/// Undo and redo stacks plus the "saved" marker behind the unsaved-changes prompt. Commands that own resources
/// (images made by an edit) are disposed when they can no longer be reached: when a new edit discards the redo
/// stack, and with the history.
/// </summary>
public sealed class UndoHistory : IDisposable
{
    private readonly EditorDocument _document;
    private readonly List<IEditorCommand> _undo = [];
    private readonly List<IEditorCommand> _redo = [];

    /// <summary>The undo depth at the last save; null once that state was discarded (only a new save cleans the document).</summary>
    private int? _savedAt = 0;

    internal UndoHistory(EditorDocument document) => _document = document;

    public event EventHandler? Changed;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public string? UndoName => CanUndo ? _undo[^1].Name : null;

    public string? RedoName => CanRedo ? _redo[^1].Name : null;

    public int UndoCount => _undo.Count;

    /// <summary>Whether the document differs from what was last saved (or, if never saved, from how it was opened).</summary>
    public bool IsDirty => _savedAt != _undo.Count;

    /// <summary>Applies <paramref name="command"/> and records it (merged into the previous step where that step allows it).</summary>
    public void Execute(IEditorCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Do(_document);
        DiscardRedo();

        // Never merge into the saved state: that state must stay reachable for IsDirty to be right.
        var merged = _savedAt != _undo.Count && _undo.Count > 0 && _undo[^1] is IMergeableCommand last && last.TryMerge(command);
        if (merged)
        {
            (command as IDisposable)?.Dispose();
        }
        else
        {
            _undo.Add(command);
        }

        OnChanged();
    }

    public void Undo()
    {
        if (!CanUndo)
        {
            return;
        }

        var command = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        command.Undo(_document);
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
        command.Do(_document);
        _undo.Add(command);
        OnChanged();
    }

    /// <summary>The current state is what's on disk now.</summary>
    public void MarkSaved()
    {
        _savedAt = _undo.Count;
        OnChanged();
    }

    public void Dispose()
    {
        foreach (var command in _undo.Concat(_redo).OfType<IDisposable>())
        {
            command.Dispose();
        }

        _undo.Clear();
        _redo.Clear();
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

        foreach (var command in _redo.OfType<IDisposable>())
        {
            command.Dispose();
        }

        _redo.Clear();
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
