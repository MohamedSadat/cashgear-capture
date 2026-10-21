using CashGear.Capture.Media.Tests;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Playback;

namespace CashGear.Capture.VideoEditor.Tests;

public sealed class TranscriptAudioTests : IDisposable
{
    private readonly TestMediaFiles _media = new("cashgear-transcript-audio-");

    public void Dispose() => _media.Dispose();

    /// <summary>The frequency of a mono tone at 16 kHz, from its rising zero crossings.</summary>
    private static double Frequency(ReadOnlySpan<float> samples)
    {
        var crossings = new List<double>();
        for (var i = 1; i < samples.Length; i++)
        {
            if (samples[i - 1] < 0 && samples[i] >= 0)
            {
                crossings.Add(i - 1 + (samples[i - 1] / (samples[i - 1] - samples[i])));
            }
        }

        return (crossings.Count - 1) / ((crossings[^1] - crossings[0]) / TranscriptAudio.SampleRate);
    }

    private static double Rms(ReadOnlySpan<float> samples)
    {
        double sum = 0;
        foreach (var s in samples)
        {
            sum += s * s;
        }

        return Math.Sqrt(sum / samples.Length);
    }

    [Fact]
    public void The_chosen_tracks_are_heard_at_16_khz_whatever_the_timeline_mutes()
    {
        TestMedia.Require();
        var file = _media.Stamped("voices.mp4", 3, tracks: ["Microphone", "System audio"], tones: new HashSet<int> { 0, 1 });
        var project = ProjectFactory.FromRecording(ProjectFactory.ReadAsset(file));
        var microphone = project.Tracks.Single(t => t.Name == "Microphone");
        var system = project.Tracks.Single(t => t.Name == "System audio");
        project = project.WithTrack(microphone with { Muted = true });

        var voice = TranscriptAudio.Render(project, [microphone.Id]);
        voice.Length.Should().Be(3 * TranscriptAudio.SampleRate);
        Frequency(voice.AsSpan(1000, 40_000)).Should().BeApproximately(440, 1, "the microphone's tone, though muted on the timeline");
        var level = Rms(voice.AsSpan(1000, 40_000));
        level.Should().BeGreaterThan(0.05);

        Frequency(TranscriptAudio.Render(project, [system.Id]).AsSpan(1000, 40_000)).Should().BeApproximately(880, 2);
        var both = TranscriptAudio.Render(project);
        Rms(both.AsSpan(1000, 40_000)).Should().BeGreaterThan(level, "both tracks");
    }
}
