namespace DwgTranslator.Agent;

/// <summary>Computes bounded CAD exchange time from an immutable source snapshot.</summary>
public static class CadDeadlinePolicy
{
    private const long MiB = 1024L * 1024L;

    public static TimeSpan ForSourceBytes(AgentBetaConfiguration configuration, long sourceBytes)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentOutOfRangeException.ThrowIfNegative(sourceBytes);
        var sourceMiB = (sourceBytes + MiB - 1) / MiB;
        var extraMiB = Math.Max(0, sourceMiB - configuration.CadLargeDwgThresholdMiB);
        var seconds = checked((long)configuration.CadExchangeSeconds +
            extraMiB * configuration.CadExchangeSecondsPerMiB);
        return TimeSpan.FromSeconds(Math.Min(seconds, configuration.CadExchangeMaxSeconds));
    }
}
