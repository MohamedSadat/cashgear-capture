using System.Collections.Concurrent;
using System.Diagnostics;
using CashGear.Capture.VideoEditor.Model;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.VideoEditor.Playback;

/// <summary>
/// Plays a project: a thread mixes its sound a little ahead into a queue that the audio output plays, and the
/// position is the output's clock (the samples heard), so the pictures the preview shows for <see cref="Position"/>
/// stay with the sound. Without a sound device a stopwatch keeps the time. The project is read anew for each block,
/// so edits are heard as they are made.
/// </summary>
public sealed partial class PlaybackEngine : IDisposable
{
    /// <summary>How far ahead of the device the sound is mixed.</summary>
    private static readonly TimeSpan MixAhead = TimeSpan.FromMilliseconds(250);

    private const int BlockFrames = 1024;

    private readonly Func<VideoProject> _project;
    private readonly Func<IAudioOutput?> _createOutput;
    private readonly ILogger _logger;
    private readonly BlockingCollection<float[]> _queue = new(new ConcurrentQueue<float[]>(), 64);
    private readonly ConcurrentBag<float[]> _spare = [];
    private readonly Stopwatch _clock = new();
    private IAudioOutput? _output;
    private Thread? _mixer;
    private CancellationTokenSource? _stop;
    private float[]? _partial;
    private int _partialOffset;
    private double _start;
    private double _paused;
    private bool _playing;
    private bool _useClock;
    private long _silence;

    /// <param name="createOutput">The sound device (null or a throwing device: the time is kept by a stopwatch).</param>
    public PlaybackEngine(Func<VideoProject> project, Func<IAudioOutput?> createOutput, ILogger logger)
    {
        _project = project;
        _createOutput = createOutput;
        _logger = logger;
    }

    /// <summary>Raised (on a worker thread) when playing reaches the project's end.</summary>
    public event EventHandler? Ended;

    public bool IsPlaying => _playing;

    /// <summary>Whether sound is playing through a device (else the time is kept by a stopwatch).</summary>
    public bool HasSound => _output is not null && !_useClock;

    /// <summary>Where playing is (timeline seconds): the sound heard now while playing, else where it stopped.</summary>
    public double Position
    {
        get
        {
            if (!_playing)
            {
                return _paused;
            }

            // Silence played while the mixer was behind didn't move the timeline on.
            var played = _useClock ? _clock.Elapsed.TotalSeconds : (double)Math.Max(0, _output!.Played - Interlocked.Read(ref _silence)) / TimelineMixer.SampleRate;
            return _start + played;
        }
    }

    public void Play(double from)
    {
        Stop();
        var duration = _project().Duration;
        _start = from >= duration - 0.01 ? 0 : Math.Max(0, from);
        _paused = _start;
        _stop = new CancellationTokenSource();
        var stop = _stop.Token;
        try
        {
            _output ??= _createOutput();
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            LogNoSound(e);
        }

        _useClock = _output is null;
        Interlocked.Exchange(ref _silence, 0);
        _mixer = new Thread(() => MixLoop(TimelineMixer.Sample(_start), stop)) { IsBackground = true, Name = "Video editor mixer" };
        _mixer.Start();
        try
        {
            if (_output is not null)
            {
                // The first blocks before the device starts, so it doesn't begin with silence the timeline never had.
                var until = DateTime.UtcNow.AddMilliseconds(500);
                while (_queue.Count < 4 && DateTime.UtcNow < until && _mixer.IsAlive)
                {
                    Thread.Sleep(2);
                }

                _output.Start(Pull);
            }
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // The device couldn't start: the mixer runs on the clock instead.
            LogNoSound(e);
            _output?.Dispose();
            _output = null;
            _useClock = true;
        }

        _clock.Restart();
        _playing = true;
    }

    public void Pause()
    {
        if (!_playing)
        {
            return;
        }

        _paused = Math.Min(Position, _project().Duration);
        Stop();
    }

    /// <summary>Goes to <paramref name="time"/>, playing on from there if it was playing.</summary>
    public void Seek(double time)
    {
        if (_playing)
        {
            Play(time);
        }
        else
        {
            _paused = Math.Max(0, time);
        }
    }

    /// <summary>Called by the preview's render loop: stops (and raises <see cref="Ended"/>) once the end is reached.</summary>
    public bool CheckEnd()
    {
        if (!_playing)
        {
            return false;
        }

        var duration = _project().Duration;
        if (Position < duration)
        {
            return false;
        }

        Stop();
        _paused = duration;
        Ended?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Dispose()
    {
        Stop();
        _output?.Dispose();
        _output = null;
        _queue.Dispose();
    }

    private void Stop()
    {
        _playing = false;
        _output?.Stop();
        _stop?.Cancel();
        _mixer?.Join(TimeSpan.FromSeconds(2));
        _mixer = null;
        _stop?.Dispose();
        _stop = null;
        while (_queue.TryTake(out var block))
        {
            _spare.Add(block);
        }

        _partial = null;
        _clock.Reset();
    }

    /// <summary>The mixing thread: blocks of the timeline's sound, from <paramref name="sample"/> on, kept ahead of the device.</summary>
    private void MixLoop(long sample, CancellationToken stop)
    {
        using var mixer = new TimelineMixer();
        var aheadBlocks = (int)Math.Ceiling(MixAhead.TotalSeconds * TimelineMixer.SampleRate / BlockFrames);
        try
        {
            while (!stop.IsCancellationRequested)
            {
                if (_queue.Count >= aheadBlocks && !_useClock)
                {
                    stop.WaitHandle.WaitOne(5);
                    continue;
                }

                var block = _spare.TryTake(out var reused) ? reused : new float[BlockFrames * TimelineMixer.Channels];
                mixer.Mix(_project(), sample, block);
                sample += BlockFrames;
                if (_useClock)
                {
                    // No device takes the sound: keep only a little, so the mixing doesn't run far ahead.
                    _spare.Add(block);
                    stop.WaitHandle.WaitOne(TimeSpan.FromSeconds((double)BlockFrames / TimelineMixer.SampleRate));
                    continue;
                }

                _queue.Add(block, stop);
            }
        }
        catch (OperationCanceledException)
        {
            // stopped
        }
        catch (Exception e) when (e is Media.FfmpegException or IOException)
        {
            LogMixFailed(e);
        }
    }

    /// <summary>The device's thread: the next samples from the queue (silence when the mixer is behind).</summary>
    private void Pull(Span<float> output)
    {
        var done = 0;
        while (done < output.Length)
        {
            if (_partial is null)
            {
                if (!_queue.TryTake(out _partial))
                {
                    output[done..].Clear();
                    Interlocked.Add(ref _silence, (output.Length - done) / TimelineMixer.Channels);
                    return;
                }

                _partialOffset = 0;
            }

            var take = Math.Min(output.Length - done, _partial.Length - _partialOffset);
            _partial.AsSpan(_partialOffset, take).CopyTo(output[done..]);
            done += take;
            _partialOffset += take;
            if (_partialOffset == _partial.Length)
            {
                _spare.Add(_partial);
                _partial = null;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Video editor: no sound device, the preview keeps time with a clock")]
    private partial void LogNoSound(Exception e);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Video editor: mixing the preview's sound failed")]
    private partial void LogMixFailed(Exception e);
}
