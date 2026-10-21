namespace CashGear.Capture.Core.Transcripts;

/// <summary>A word (or a piece of one) of a transcript: when it is said, in seconds, and its text as recognized (with its leading space).</summary>
public sealed record TranscriptWord(double Start, double End, string Text);

/// <summary>A stretch of speech as a speech recognizer heard it: when, what (trimmed), and its words with their times when known.</summary>
public sealed record TranscriptSegment(double Start, double End, string Text, IReadOnlyList<TranscriptWord>? Words = null);
