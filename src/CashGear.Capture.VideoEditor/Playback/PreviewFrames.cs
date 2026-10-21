using CashGear.Capture.Media;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Rendering;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.VideoEditor.Playback;

/// <summary>
/// The preview's pictures: a <see cref="FrameSource"/> (a decoder thread) per clip near the playhead, so a cut
/// doesn't make one decoder seek while playing, and clips that start soon are decoded before they are reached.
/// Pictures are decoded no larger than the canvas. <see cref="FrameReady"/> says when a picture that was missing
/// has arrived.
/// </summary>
public sealed class PreviewFrames : IFrameProvider, IDisposable
{
    /// <summary>Clips starting this soon get their decoder (and first picture) ready.</summary>
    private const double Ahead = 1.5;

    /// <summary>Clips this far behind the playhead keep their decoder for a step back.</summary>
    private const double Behind = 1.0;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, (FrameSource Source, string Path, int Stream)> _sources = [];
    private readonly ILoggerFactory _loggers;
    private readonly VideoDecoding _decoding;

    public PreviewFrames(ILoggerFactory loggers, VideoDecoding decoding = VideoDecoding.Hardware)
    {
        _loggers = loggers;
        _decoding = decoding;
    }

    /// <summary>Raised on a decoder thread when a picture arrives.</summary>
    public event EventHandler? FrameReady;

    /// <summary>Whether any picture so far came from the graphics card.</summary>
    public bool IsHardware
    {
        get
        {
            lock (_gate)
            {
                return _sources.Values.Any(s => s.Source.IsHardware);
            }
        }
    }

    /// <summary>The clips that couldn't be decoded, with why.</summary>
    public IReadOnlyList<(string Clip, Exception Failure)> Failures
    {
        get
        {
            lock (_gate)
            {
                return [.. _sources.Where(s => s.Value.Source.Failure is not null).Select(s => (s.Key, s.Value.Source.Failure!))];
            }
        }
    }

    public VideoFrame? Frame(Clip clip, MediaAsset asset, double sourceTime)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(asset);
        return SourceFor(clip, asset, null).TryGet(sourceTime);
    }

    /// <summary>
    /// Keeps decoders for the clips around <paramref name="time"/> (and those starting soon, asked for their first
    /// picture), closes the others, and tells them whether the preview is playing.
    /// </summary>
    public void Update(VideoProject project, double time, bool playing)
    {
        ArgumentNullException.ThrowIfNull(project);
        var wanted = new HashSet<string>();
        foreach (var track in project.Tracks.Where(t => t.HasPictures && !t.Hidden))
        {
            foreach (var clip in track.Clips)
            {
                if (clip.End < time - Behind || clip.Start > time + Ahead || project.Asset(clip.AssetId) is not { } asset)
                {
                    continue;
                }

                wanted.Add(clip.Id);
                var source = SourceFor(clip, asset, project.Canvas);
                source.SetPlaying(playing && clip.Covers(time));
                if (clip.Start > time)
                {
                    source.TryGet(clip.SourceIn)?.Release(); // its first picture, ready when it is reached
                }
            }
        }

        List<FrameSource> closing;
        lock (_gate)
        {
            closing = [.. _sources.Where(s => !wanted.Contains(s.Key)).Select(s => s.Value.Source)];
            foreach (var id in _sources.Keys.Where(id => !wanted.Contains(id)).ToList())
            {
                _sources.Remove(id);
            }
        }

        foreach (var source in closing)
        {
            source.FrameReady -= OnFrameReady;
            source.Dispose();
        }
    }

    public void Dispose()
    {
        List<FrameSource> all;
        lock (_gate)
        {
            all = [.. _sources.Values.Select(s => s.Source)];
            _sources.Clear();
        }

        foreach (var source in all)
        {
            source.FrameReady -= OnFrameReady;
            source.Dispose();
        }
    }

    private FrameSource SourceFor(Clip clip, MediaAsset asset, ProjectCanvas? canvas)
    {
        lock (_gate)
        {
            if (_sources.TryGetValue(clip.Id, out var existing) && existing.Path == asset.Path && existing.Stream == clip.Stream)
            {
                return existing.Source;
            }

            if (existing.Source is { } stale)
            {
                stale.FrameReady -= OnFrameReady;
                stale.Dispose();
            }

            var (width, height) = DecodeSize(asset.Stream(clip.Stream), canvas);
            var source = new FrameSource(asset.Path, clip.Stream, _decoding, width, height, _loggers.CreateLogger<FrameSource>());
            source.FrameReady += OnFrameReady;
            _sources[clip.Id] = (source, asset.Path, clip.Stream);
            return source;
        }
    }

    /// <summary>The stream's size, made smaller (keeping its shape) when it is larger than the canvas.</summary>
    internal static (int Width, int Height) DecodeSize(AssetStream? stream, ProjectCanvas? canvas)
    {
        var width = stream is { Width: > 0 } ? stream.Width : canvas?.Width ?? 1280;
        var height = stream is { Height: > 0 } ? stream.Height : canvas?.Height ?? 720;
        if (canvas is not null && (width > canvas.Width || height > canvas.Height))
        {
            var scale = Math.Min((double)canvas.Width / width, (double)canvas.Height / height);
            width = Math.Max(2, (int)Math.Round(width * scale));
            height = Math.Max(2, (int)Math.Round(height * scale));
        }

        return (width, height);
    }

    private void OnFrameReady(object? sender, EventArgs e) => FrameReady?.Invoke(this, EventArgs.Empty);
}
