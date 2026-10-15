using System.Collections.Immutable;
using CashGear.Capture.Editor.Model;
using SkiaSharp;

namespace CashGear.Capture.Editor.Document;

/// <summary>The image and the annotations on it at one moment. Both parts are immutable, so a state can be kept for undo.</summary>
public sealed record DocumentState(SKImage Image, ImmutableList<Annotation> Annotations);

/// <summary>
/// A capture being edited: the base image, the annotations over it (bottom to top) and the undo history. Every
/// change goes through <see cref="Execute"/> as an <see cref="IEditorCommand"/>, so everything can be undone.
/// The document owns the image it was created with; images made by edits are owned by their commands.
/// </summary>
public sealed class EditorDocument : IDisposable
{
    private readonly SKImage _original;
    private int _lastId;

    public EditorDocument(SKImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        _original = image;
        Image = image;
        History = new UndoHistory(this);
    }

    public SKImage Image { get; private set; }

    /// <summary>Bottom to top: later annotations draw over earlier ones and are hit first.</summary>
    public ImmutableList<Annotation> Annotations { get; private set; } = [];

    public int Width => Image.Width;

    public int Height => Image.Height;

    public UndoHistory History { get; }

    public DocumentState State => new(Image, Annotations);

    /// <summary>The number the next step badge gets: one more than the highest so far (deleting #2 doesn't renumber).</summary>
    public int NextStepNumber => Annotations.OfType<StepNumberAnnotation>().Select(s => s.Number).DefaultIfEmpty(0).Max() + 1;

    /// <summary>Raised after every change to the image or annotations, including previews during a drag.</summary>
    public event EventHandler? Changed;

    public AnnotationId NewId() => new(++_lastId);

    public Annotation? Find(AnnotationId id) => Annotations.Find(a => a.Id == id);

    public int IndexOf(AnnotationId id) => Annotations.FindIndex(a => a.Id == id);

    public void Execute(IEditorCommand command) => History.Execute(command);

    /// <summary>Shows an edit in progress (a drag) without recording it. The caller records the final state as a command.</summary>
    internal void Preview(Annotation annotation) => Replace(annotation);

    internal void Insert(int index, Annotation annotation)
    {
        Annotations = Annotations.Insert(index, annotation);
        OnChanged();
    }

    internal int Remove(AnnotationId id)
    {
        var index = IndexOf(id);
        if (index < 0)
        {
            throw new InvalidOperationException($"Annotation {id} is not in the document.");
        }

        Annotations = Annotations.RemoveAt(index);
        OnChanged();
        return index;
    }

    internal void Replace(Annotation annotation)
    {
        var index = IndexOf(annotation.Id);
        if (index < 0)
        {
            throw new InvalidOperationException($"Annotation {annotation.Id} is not in the document.");
        }

        Annotations = Annotations.SetItem(index, annotation);
        OnChanged();
    }

    internal void Move(AnnotationId id, int index)
    {
        var annotation = Find(id) ?? throw new InvalidOperationException($"Annotation {id} is not in the document.");
        Annotations = Annotations.Remove(annotation).Insert(index, annotation);
        OnChanged();
    }

    internal void Restore(DocumentState state)
    {
        Image = state.Image;
        Annotations = state.Annotations;
        OnChanged();
    }

    public void Dispose()
    {
        History.Dispose();
        _original.Dispose();
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
