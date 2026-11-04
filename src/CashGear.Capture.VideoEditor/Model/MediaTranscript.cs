using System.Text.Json.Serialization;

namespace CashGear.Capture.VideoEditor.Model;

/// <summary>
/// A word of a transcript on its stream's own clock, in seconds: where it is said in the media, whatever the timeline
/// does with that part. A segment that came without word times is one unit, a whole sentence.
/// </summary>
/// <param name="Text">The word as it is shown, without spaces around it: corrected, if it was.</param>
/// <param name="IsSentence">It is a whole sentence: cut as one.</param>
/// <param name="Heard">What was heard, when the text was corrected by hand.</param>
public sealed record SpokenWord(double Start, double End, string Text, bool IsSentence = false, string? Heard = null)
{
    [JsonIgnore]
    public double Middle => (Start + End) / 2;
}

/// <summary>
/// What is said in a media file: the speech of one of its sound streams, in the order it is said. It belongs to the
/// media, not the timeline, and is shown through the clips (<see cref="Editing.TranscriptView"/>), so it survives cuts,
/// speed changes and freezes.
/// </summary>
/// <param name="Stream">The sound stream it was heard from.</param>
/// <param name="Language">Two letters.</param>
public sealed record MediaTranscript(int Stream, string Language, IReadOnlyList<SpokenWord> Words)
{
    /// <summary>Whether any part of it has only whole sentences to cut by.</summary>
    [JsonIgnore]
    public bool HasSentences => Words.Any(w => w.IsSentence);

    /// <summary>Written right to left.</summary>
    [JsonIgnore]
    public bool IsRightToLeft => Language is "ar" or "he" or "fa" or "ur";
}
