using System.Globalization;
using System.IO;
using System.Text;
using CashGear.Capture.Media;

namespace CashGear.Capture.SyncTest;

/// <summary>One flash and the beep that went with it: when each is in the file, and the sound's lead or lag.</summary>
internal readonly record struct Clap(double Flash, double Beep)
{
    /// <summary>Positive: the beep comes after the flash in the file.</summary>
    public double OffsetMs => (Beep - Flash) * 1000;
}

/// <summary>What a recording's flashes and beeps say about its sync.</summary>
internal sealed record SyncReport(
    string File,
    double DurationSeconds,
    int Flashes,
    int Beeps,
    IReadOnlyList<Clap> Claps)
{
    public double MeanMs => Claps.Count == 0 ? double.NaN : Claps.Average(c => c.OffsetMs);

    public double StdMs => Claps.Count < 2 ? 0 : Math.Sqrt(Claps.Sum(c => Math.Pow(c.OffsetMs - MeanMs, 2)) / (Claps.Count - 1));

    public double MinMs => Claps.Count == 0 ? double.NaN : Claps.Min(c => c.OffsetMs);

    public double MaxMs => Claps.Count == 0 ? double.NaN : Claps.Max(c => c.OffsetMs);

    /// <summary>How much the offset changes from the first clap to the last, by a straight-line fit.</summary>
    public double FittedDriftMs
    {
        get
        {
            if (Claps.Count < 2)
            {
                return 0;
            }

            var meanT = Claps.Average(c => c.Flash);
            var meanO = MeanMs;
            var slope = Claps.Sum(c => (c.Flash - meanT) * (c.OffsetMs - meanO)) / Claps.Sum(c => Math.Pow(c.Flash - meanT, 2));
            return slope * (Claps[^1].Flash - Claps[0].Flash);
        }
    }

    /// <summary>Mean offset of the last stretch minus that of the first (5 minutes each, or a fifth of a short file).</summary>
    public double FirstToLastMs
    {
        get
        {
            if (Claps.Count < 2)
            {
                return 0;
            }

            var span = Math.Min(300, (Claps[^1].Flash - Claps[0].Flash) / 5);
            var first = Claps.Where(c => c.Flash <= Claps[0].Flash + span).Average(c => c.OffsetMs);
            var last = Claps.Where(c => c.Flash >= Claps[^1].Flash - span).Average(c => c.OffsetMs);
            return last - first;
        }
    }

    public string Summary(string? extra = null)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"File        {File}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Duration    {DurationSeconds:0.000} s");
        text.AppendLine(CultureInfo.InvariantCulture, $"Flashes     {Flashes}, beeps {Beeps}, paired {Claps.Count}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Offset      mean {MeanMs:0.0} ms (sound after picture), sd {StdMs:0.0} ms, range {MinMs:0.0} to {MaxMs:0.0} ms");
        text.AppendLine(CultureInfo.InvariantCulture, $"Drift       {FittedDriftMs:0.0} ms over the file (fitted line), {FirstToLastMs:0.0} ms first to last stretch");
        if (extra is not null)
        {
            text.AppendLine(extra);
        }

        return text.ToString();
    }

    public void WriteCsv(string path)
    {
        using var writer = new StreamWriter(path);
        writer.WriteLine("flash_s,beep_s,offset_ms");
        foreach (var clap in Claps)
        {
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{clap.Flash:0.0000},{clap.Beep:0.0000},{clap.OffsetMs:0.00}"));
        }
    }
}

/// <summary>Finds the flashes (in a rectangle of the picture) and the beeps (in the first sound track) and pairs them.</summary>
internal static class Analysis
{
    /// <summary>A flash is the square's luma going above this (black is 16, white 235).</summary>
    private const double FlashLuma = 128;

    /// <summary>
    /// A beep starts when the sound goes above this share of the loudest sound in the track, after at least half a
    /// second of quiet, and stays loud for 20 ms (a click doesn't). Relative, because system audio is captured after
    /// the device's volume.
    /// </summary>
    private const float BeepThreshold = 0.3f;

    public static SyncReport Analyze(string file, int x, int y, int width, int height)
    {
        using var reader = new MediaReader(file);
        var frames = reader.VideoLuma(x, y, width, height);
        var flashes = frames.Where((f, i) => f.Luma > FlashLuma && (i == 0 || frames[i - 1].Luma <= FlashLuma)).Select(f => f.Time).ToList();

        var beeps = new List<double>();
        if (reader.AudioStreams.FirstOrDefault() is { } sound)
        {
            var peak = 0f;
            reader.ReadAudio(sound.Index, (_, _, samples) =>
            {
                foreach (var sample in samples)
                {
                    peak = Math.Max(peak, Math.Abs(sample));
                }
            });

            var threshold = peak * BeepThreshold;
            var quiet = int.MaxValue;
            double? candidate = null;
            var loud = 0;
            var since = 0;
            reader.ReadAudio(sound.Index, (time, rate, samples) =>
            {
                for (var i = 0; i < samples.Length; i++)
                {
                    var above = Math.Abs(samples[i]) > threshold;
                    if (candidate is not null)
                    {
                        loud += above ? 1 : 0;
                        if (++since >= rate / 50)
                        {
                            if (loud > since / 10)
                            {
                                beeps.Add(candidate.Value); // still sounding 20 ms on: a beep, not a click
                            }

                            candidate = null;
                        }
                    }

                    if (above)
                    {
                        if (quiet > rate / 2 && candidate is null)
                        {
                            candidate = time + ((double)i / rate);
                            loud = 0;
                            since = 0;
                        }

                        quiet = 0;
                    }
                    else if (quiet < int.MaxValue)
                    {
                        quiet++;
                    }
                }
            });
        }

        var claps = new List<Clap>();
        foreach (var flash in flashes)
        {
            var nearest = beeps.Count == 0 ? double.NaN : beeps.MinBy(b => Math.Abs(b - flash));
            if (Math.Abs(nearest - flash) < 0.5)
            {
                claps.Add(new Clap(flash, nearest));
            }
        }

        var duration = frames.Count == 0 ? 0 : frames[^1].Time + (1.0 / 30);
        return new SyncReport(file, Math.Max(duration, reader.Duration), flashes.Count, beeps.Count, claps);
    }
}
