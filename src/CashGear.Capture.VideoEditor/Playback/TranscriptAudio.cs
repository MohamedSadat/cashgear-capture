using CashGear.Capture.VideoEditor.Model;

namespace CashGear.Capture.VideoEditor.Playback;

/// <summary>
/// The sound speech recognition hears: the chosen sound tracks as they are heard on the timeline (their volumes,
/// fades, cuts and speeds, through <see cref="TimelineMixer"/>), mixed to mono and taken down to 16 kHz through a
/// low-pass filter (nothing above 7 kHz folds back as noise).
/// </summary>
public static class TranscriptAudio
{
    public const int SampleRate = 16_000;

    private const int Factor = TimelineMixer.SampleRate / SampleRate;
    private const int Taps = 96;

    /// <summary>The filter: a Blackman-windowed sinc cutting off at 7 kHz, its gain 1.</summary>
    private static readonly float[] Filter = MakeFilter();

    /// <summary>
    /// The sound of <paramref name="trackIds"/> (all sound tracks when null) at 16 kHz mono, as long as the project.
    /// Muting and solo on the timeline don't matter here: the tracks asked for are heard.
    /// </summary>
    public static float[] Render(VideoProject project, IReadOnlyCollection<string>? trackIds = null, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        var heard = project with
        {
            Tracks = [.. project.Tracks.Select(t => t.HasSound ? t with { Muted = trackIds is not null && !trackIds.Contains(t.Id), Solo = false } : t)],
        };
        var total = TimelineMixer.Sample(project.Duration);
        var output = new float[(int)(total / Factor)];
        using var mixer = new TimelineMixer();
        var block = new float[TimelineMixer.SampleRate * TimelineMixer.Channels];
        var history = new float[Taps - 1];
        var mono = new float[history.Length + TimelineMixer.SampleRate];
        var written = 0;
        for (long at = 0; at < total && written < output.Length; at += TimelineMixer.SampleRate)
        {
            ct.ThrowIfCancellationRequested();
            var count = (int)Math.Min(TimelineMixer.SampleRate, total - at);
            var samples = block.AsSpan(0, count * TimelineMixer.Channels);
            mixer.Mix(heard, at, samples);

            // Mono after the filter's history; then every third sample of the filtered sound.
            history.CopyTo(mono, 0);
            for (var i = 0; i < count; i++)
            {
                mono[history.Length + i] = (samples[2 * i] + samples[(2 * i) + 1]) / 2;
            }

            for (var i = 0; i + Factor <= count && written < output.Length; i += Factor)
            {
                float sum = 0;
                for (var k = 0; k < Taps; k++)
                {
                    sum += Filter[k] * mono[i + k];
                }

                output[written++] = sum;
            }

            Array.Copy(mono, count, history, 0, history.Length);
            progress?.Report((double)(at + count) / total);
        }

        return output;
    }

    /// <summary>
    /// A stream's sound on its own clock, whatever the timeline does with it: sample <c>n</c> at 16 kHz is <c>n / 16000</c> s
    /// on the stream's clock. What a transcript that belongs to the media is heard from.
    /// </summary>
    public static float[] RenderStream(MediaAsset asset, int stream, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var clip = new Clip { Id = Ids.New(), AssetId = asset.Id, Stream = stream, SourceIn = 0, SourceOut = asset.Duration };
        var project = VideoProject.Empty(new ProjectCanvas(16, 16, 30)) with { Assets = [asset], Tracks = [new Track(Ids.New(), TrackKind.Audio, "", [clip])] };
        return Render(project, null, progress, ct);
    }

    private static float[] MakeFilter()
    {
        const double cutoff = 7_000.0 / TimelineMixer.SampleRate;
        var taps = new double[Taps];
        var middle = (Taps - 1) / 2.0;
        for (var k = 0; k < Taps; k++)
        {
            var x = k - middle;
            var sinc = x == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * x) / (Math.PI * x);
            var window = 0.42 - (0.5 * Math.Cos(2 * Math.PI * k / (Taps - 1))) + (0.08 * Math.Cos(4 * Math.PI * k / (Taps - 1)));
            taps[k] = sinc * window;
        }

        var gain = taps.Sum();
        return [.. taps.Select(t => (float)(t / gain))];
    }
}
