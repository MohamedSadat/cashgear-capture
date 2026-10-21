namespace CashGear.Capture.Licensing;

/// <summary>Whether a Pro feature may be used now (the app's licence service answers; tests and tools use <see cref="FeatureGate.Open"/>).</summary>
public interface IFeatureGate
{
    bool IsEnabled(ProFeature feature);
}

public static class FeatureGate
{
    /// <summary>Every feature on: this version's policy, and the default where no licence service is given.</summary>
    public static IFeatureGate Open { get; } = new Always(true);

    /// <summary>Every Pro feature off (tests of what the free edition does).</summary>
    public static IFeatureGate Closed { get; } = new Always(false);

    private sealed class Always(bool enabled) : IFeatureGate
    {
        public bool IsEnabled(ProFeature feature) => enabled;
    }
}
