using CashGear.Capture.Editor.Model;

namespace CashGear.Capture.Editor.Document;

/// <summary>Puts a new annotation on top.</summary>
public sealed class AddAnnotationCommand(Annotation annotation) : IEditorCommand
{
    public Annotation Annotation { get; } = annotation;

    public string Name => "add " + Annotation.Kind.DisplayName();

    public void Do(EditorDocument document) => document.Insert(document.Annotations.Count, Annotation);

    public void Undo(EditorDocument document) => document.Remove(Annotation.Id);
}

/// <summary>Deletes an annotation; undo puts it back at the same depth.</summary>
public sealed class RemoveAnnotationCommand(Annotation annotation) : IEditorCommand
{
    private int _index;

    public string Name => "delete " + annotation.Kind.DisplayName();

    public void Do(EditorDocument document) => _index = document.Remove(annotation.Id);

    public void Undo(EditorDocument document) => document.Insert(_index, annotation);
}

/// <summary>
/// Changes an annotation (move, resize, restyle, new text): swaps one version of the record for another.
/// Commands with the same non-null <paramref name="mergeKey"/> on the same annotation merge into one undo step.
/// </summary>
public sealed class ReplaceAnnotationCommand(Annotation before, Annotation after, string name, string? mergeKey = null) : IMergeableCommand
{
    private Annotation _after = after;

    public string Name { get; } = name;

    public Annotation Before { get; } = before;

    public Annotation After => _after;

    public void Do(EditorDocument document) => document.Replace(_after);

    public void Undo(EditorDocument document) => document.Replace(Before);

    public bool TryMerge(IEditorCommand next)
    {
        if (mergeKey is null || next is not ReplaceAnnotationCommand other || other.MergeKey != mergeKey || other.Before.Id != Before.Id)
        {
            return false;
        }

        _after = other.After;
        return true;
    }

    private string? MergeKey => mergeKey;
}

/// <summary>Changes an annotation's depth (bring to front, send to back).</summary>
public sealed class ReorderAnnotationCommand(AnnotationId id, int newIndex, string name) : IEditorCommand
{
    private int _oldIndex;

    public string Name { get; } = name;

    public void Do(EditorDocument document)
    {
        _oldIndex = document.IndexOf(id);
        document.Move(id, newIndex);
    }

    public void Undo(EditorDocument document) => document.Move(id, _oldIndex);
}

/// <summary>
/// An edit of the whole image (crop, rotate, resize, border, shadow) that also moves the annotations with it.
/// Owns the image it produced: disposing the command (when it falls off the history) frees it.
/// </summary>
public sealed class ImageEditCommand(string name, DocumentState before, DocumentState after) : IEditorCommand, IDisposable
{
    public string Name { get; } = name;

    public DocumentState Before { get; } = before;

    public DocumentState After { get; } = after;

    public void Do(EditorDocument document) => document.Restore(After);

    public void Undo(EditorDocument document) => document.Restore(Before);

    public void Dispose()
    {
        if (!ReferenceEquals(After.Image, Before.Image))
        {
            After.Image.Dispose();
        }
    }
}
