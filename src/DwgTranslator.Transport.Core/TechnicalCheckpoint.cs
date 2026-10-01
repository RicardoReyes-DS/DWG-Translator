using System.Diagnostics;

namespace DwgTranslator.Transport.Core;

public static class TechnicalCheckpoint
{
    public static event Action<string, DateTimeOffset>? Emitted;

    public static void Emit(string stage)
    {
        if (string.IsNullOrWhiteSpace(stage) || stage.Length > 80) return;
        var timestamp = DateTimeOffset.UtcNow;
        Trace.WriteLine($"DwgTranslator checkpoint={stage} utc={timestamp:O}");
        Emitted?.Invoke(stage, timestamp);
    }
}
