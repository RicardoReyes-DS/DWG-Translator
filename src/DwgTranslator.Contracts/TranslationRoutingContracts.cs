using System.Text.Json.Serialization;

namespace DwgTranslator.Contracts;

public static class TranslationRouting
{
    public const string PolicyVersion = "linguistic-routing/1.0";
    public const string SchemaVersion = "dwg-translation-batch/1.0";
    public const string Auto = "Auto";
    public const string Economy = "Economy";
    public const string MaximumQuality = "MaximumQuality";
    public const string Manual = "Manual";
    public const string Terra = "gpt-5.6-terra";
    public const string Luna = "gpt-5.6-luna";
    public const string Sol = "gpt-5.6-sol";
    public const string ReasoningNone = "none";
}

public sealed record TranslationRoutingPolicy
{
    [JsonRequired] public required string Version { get; init; }
    [JsonRequired] public required string RequestedMode { get; init; }
    [JsonRequired] public required string BaseModel { get; init; }
    [JsonRequired] public required string ReasoningEffort { get; init; }
    public string? EscalationModel { get; init; }
    public int MaxEscalationsPerSegment { get; init; } = 1;
    public bool EscalateHighRisk { get; init; } = true;
    public double HighMinLengthRatio { get; init; } = 0.30;
    public double HighMaxLengthRatio { get; init; } = 3.50;
    public double MediumMinLengthRatio { get; init; } = 0.50;
    public double MediumMaxLengthRatio { get; init; } = 2.50;
}

public sealed record TranslationEscalationContext
{
    [JsonRequired] public required string BaseModel { get; init; }
    [JsonRequired] public required List<TranslationEscalationSegment> Segments { get; init; }
}

public sealed record TranslationEscalationSegment
{
    [JsonRequired] public required string SegmentId { get; init; }
    [JsonRequired] public required string BaseProposalWithTokenAliases { get; init; }
    [JsonRequired] public required List<string> ReasonCodes { get; init; }
}

public sealed record TranslationCallTrace
{
    [JsonRequired] public required string Tier { get; init; }
    [JsonRequired] public required string RequestedModel { get; init; }
    [JsonRequired] public required string EffectiveModel { get; init; }
    [JsonRequired] public required TranslationUsage Usage { get; init; }
    public string? RequestId { get; init; }
    [JsonRequired] public required long LatencyMilliseconds { get; init; }
    [JsonRequired] public required string PromptVersion { get; init; }
    [JsonRequired] public required string SchemaVersion { get; init; }
    [JsonRequired] public required string Outcome { get; init; }
    public string? ErrorCode { get; init; }
}

public sealed record TranslationSegmentTrace
{
    [JsonRequired] public required string SegmentId { get; init; }
    [JsonRequired] public required string RequestedMode { get; init; }
    [JsonRequired] public required string BaseModel { get; init; }
    [JsonRequired] public required string EffectiveModel { get; init; }
    [JsonRequired] public required bool Escalated { get; init; }
    [JsonRequired] public required List<string> EscalationReasonCodes { get; init; }
    [JsonRequired] public required string ValidatorResult { get; init; }
    [JsonRequired] public required string RiskSeverity { get; init; }
    [JsonRequired] public required string RoutingVersion { get; init; }
}

public sealed record TranslationRoutingTrace
{
    [JsonRequired] public required string RequestedMode { get; init; }
    [JsonRequired] public required string BaseModel { get; init; }
    [JsonRequired] public required string RoutingVersion { get; init; }
    [JsonRequired] public required int EscalatedSegmentCount { get; init; }
    [JsonRequired] public required List<TranslationCallTrace> Calls { get; init; }
    [JsonRequired] public required List<TranslationSegmentTrace> Segments { get; init; }
}
