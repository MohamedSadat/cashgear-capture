using CashGear.Capture.VideoEditor.Model;

namespace CashGear.Capture.VideoEditor.Playback;

/// <summary>Gives the next samples of a stream (pairs of L, R); returns how many pairs, fewer than asked only at its end.</summary>
internal delegate int SoundRead(Span<float> interleaved);

/// <summary>
/// A speed ramp's sound with its pitch kept: waveform-similarity overlap-add, the method of FFmpeg's <c>atempo</c>. Short
/// overlapping pieces of the source, each from where the clip's own time map says it should be, are laid one after another on the
/// timeline; each piece may start a little off that place, so it continues the piece before it in step with its wave. So the sound
/// never falls behind its pictures by more than <see cref="Search"/> frames, however the speed changes. One thread at a time.
/// </summary>
/// <remarks>
/// atempo itself was tried first, in short blocks at the speed of each. Each change of its tempo sets its position anew from the piece
/// it is in, half a piece on whatever the speed, so at short blocks the sound ran about 40 % short at a slow speed.
/// </remarks>
internal sealed class RampStretcher(SoundRead read)
{
    /// <summary>A piece: about 43 ms, as atempo takes at 48 kHz.</summary>
    public const int Piece = 2048;

    /// <summary>How far apart the pieces are laid: half of one, so their windows add up to 1.</summary>
    public const int Hop = Piece / 2;

    /// <summary>How far from its place a piece may start: about 11 ms.</summary>
    public const int Search = 512;

    private const int Channels = TimelineMixer.Channels;

    /// <summary>The wave is compared at a quarter of the rate, then the best place is looked at closely.</summary>
    private const int Coarse = 4;

    private static readonly float[] Window = MakeWindow();

    private readonly float[] _chunk = new float[1024 * Channels];
    private readonly float[] _target = new float[Hop / Coarse];
    private readonly float[] _coarse = new float[(Hop / Coarse) + (2 * Search / Coarse) + 1];

    // What has been read of the source, the first frame being source frame _sourceStart.
    private float[] _source = new float[Piece * 4 * Channels];
    private long _sourceStart;
    private int _sourceFrames;
    private bool _ended;

    // What has been laid on the timeline, the first frame being timeline sample _laidStart.
    private float[] _laid = new float[Piece * 2 * Channels];
    private long _laidStart;
    private long _nextPiece;
    private long _clipStart;
    private long _previousStart;
    private bool _hasPrevious;

    /// <summary>
    /// Where to start reading the source so as to play from timeline sample <paramref name="from"/>: the caller goes there, and
    /// reading goes on with <paramref name="read"/>.
    /// </summary>
    public long Start(Clip clip, long from)
    {
        ArgumentNullException.ThrowIfNull(clip);
        _clipStart = TimelineMixer.Sample(clip.Start);
        var piece = Math.Max(-1, Math.Floor((double)(from - _clipStart) / Hop) - 1);
        _nextPiece = _clipStart + ((long)piece * Hop);
        _laidStart = _nextPiece;
        Array.Clear(_laid);
        _hasPrevious = false;
        _ended = false;
        _sourceFrames = 0;
        _sourceStart = Math.Max(0, Place(clip, _nextPiece) - Search);
        return _sourceStart;
    }

    /// <summary>The sound for timeline samples <paramref name="from"/> onward.</summary>
    public void Render(Clip clip, long from, Span<float> block)
    {
        var frames = block.Length / Channels;
        var end = from + frames;

        // A frame is whole when every piece over it has been laid: the last of them starts at it, or before.
        while (_nextPiece < end)
        {
            LayPiece(clip);
        }

        Grow(ref _laid, (int)(end - _laidStart) * Channels);
        _laid.AsSpan((int)(from - _laidStart) * Channels, frames * Channels).CopyTo(block);

        // Forget what has been played.
        var keep = (int)(end - _laidStart);
        var left = Math.Max(0, (int)(_nextPiece + Piece - Hop - end));
        _laid.AsSpan(keep * Channels, left * Channels).CopyTo(_laid);
        _laid.AsSpan(left * Channels).Clear();
        _laidStart = end;
    }

    private static float[] MakeWindow()
    {
        var window = new float[Piece];
        for (var i = 0; i < Piece; i++)
        {
            window[i] = (float)(0.5 - (0.5 * Math.Cos(2 * Math.PI * i / Piece)));
        }

        return window;
    }

    private static void Grow(ref float[] array, int length)
    {
        if (array.Length < length)
        {
            Array.Resize(ref array, Math.Max(length, array.Length * 2));
        }
    }

    /// <summary>The source frame a piece laid at a timeline sample starts at: its middle is what the clip shows there. Before and after the clip, its speed goes on.</summary>
    private static long Place(Clip clip, long sample)
    {
        var time = (double)(sample + (Piece / 2)) / TimelineMixer.SampleRate;
        var source = time < clip.Start ? clip.SourceIn + ((time - clip.Start) * clip.Speed)
            : time > clip.End ? clip.SourceOut + ((time - clip.End) * (clip.EndSpeed ?? clip.Speed))
            : clip.SourceAt(time);
        return (long)Math.Round(source * TimelineMixer.SampleRate) - (Piece / 2);
    }

    private void LayPiece(Clip clip)
    {
        var place = Place(clip, _nextPiece);
        var start = _hasPrevious ? Aligned(place) : place;
        Have(start + Piece);
        var at = (int)(_nextPiece - _laidStart);
        Grow(ref _laid, (at + Piece) * Channels);
        for (var i = 0; i < Piece; i++)
        {
            var index = (start + i - _sourceStart) * Channels;
            if (index >= 0 && index + 1 < _sourceFrames * Channels)
            {
                _laid[(at + i) * Channels] += _source[index] * Window[i];
                _laid[((at + i) * Channels) + 1] += _source[index + 1] * Window[i];
            }
        }

        _previousStart = start;
        _hasPrevious = true;
        _nextPiece += Hop;
        Forget(Math.Min(start + Hop, Place(clip, _nextPiece)) - (Search * 2));
    }

    /// <summary>The place within reach of <paramref name="place"/> where the source goes on best from the piece before: at a quarter of the rate, then exactly.</summary>
    private long Aligned(long place)
    {
        var next = _previousStart + Hop;
        Have(Math.Max(next + Hop, place + Search + Hop));
        var energy = 0.0;
        for (var i = 0; i < _target.Length; i++)
        {
            _target[i] = Mono(next + (i * Coarse), Coarse);
            energy += _target[i] * _target[i];
        }

        if (energy < 1e-9)
        {
            return place; // nothing to keep in step with
        }

        for (var i = 0; i < _coarse.Length; i++)
        {
            _coarse[i] = Mono(place - Search + (i * Coarse), Coarse);
        }

        var best = 0;
        var bestScore = double.MinValue;
        for (var lag = 0; lag <= 2 * Search / Coarse; lag++)
        {
            var score = Likeness(_coarse.AsSpan(lag, _target.Length));
            if (score > bestScore)
            {
                bestScore = score;
                best = lag;
            }
        }

        var coarse = place - Search + (best * Coarse);
        var bestFine = coarse;
        bestScore = double.MinValue;
        for (var candidate = Math.Max(place - Search, coarse - Coarse); candidate <= Math.Min(place + Search, coarse + Coarse); candidate++)
        {
            double together = 0;
            double own = 1e-12;
            for (var i = 0; i < Hop; i += 2)
            {
                var (a, b) = (Mono(next + i, 1), Mono(candidate + i, 1));
                together += a * b;
                own += b * b;
            }

            var score = together / Math.Sqrt(own);
            if (score > bestScore)
            {
                bestScore = score;
                bestFine = candidate;
            }
        }

        return bestFine;
    }

    private double Likeness(ReadOnlySpan<float> candidate)
    {
        double together = 0;
        double own = 1e-12;
        for (var i = 0; i < candidate.Length; i++)
        {
            together += _target[i] * candidate[i];
            own += candidate[i] * candidate[i];
        }

        return together / Math.Sqrt(own);
    }

    /// <summary>Both channels of <paramref name="count"/> frames from a source frame, as one number; silence outside what is read.</summary>
    private float Mono(long frame, int count)
    {
        var sum = 0f;
        for (var n = 0; n < count; n++)
        {
            var index = (frame + n - _sourceStart) * Channels;
            if (index >= 0 && index + 1 < _sourceFrames * Channels)
            {
                sum += _source[index] + _source[index + 1];
            }
        }

        return sum;
    }

    /// <summary>Reads on until the source frames before <paramref name="until"/> are there, or there are no more.</summary>
    private void Have(long until)
    {
        while (!_ended && _sourceStart + _sourceFrames < until)
        {
            var got = read(_chunk);
            Grow(ref _source, (_sourceFrames + got) * Channels);
            _chunk.AsSpan(0, got * Channels).CopyTo(_source.AsSpan(_sourceFrames * Channels));
            _sourceFrames += got;
            _ended = got < _chunk.Length / Channels;
        }
    }

    /// <summary>Forgets the source before a frame, when that is enough to be worth moving the rest.</summary>
    private void Forget(long before)
    {
        var drop = before - _sourceStart;
        if (drop >= Piece * 4 && drop <= _sourceFrames)
        {
            var count = _sourceFrames - (int)drop;
            _source.AsSpan((int)drop * Channels, count * Channels).CopyTo(_source);
            _sourceFrames = count;
            _sourceStart = before;
        }
    }
}
