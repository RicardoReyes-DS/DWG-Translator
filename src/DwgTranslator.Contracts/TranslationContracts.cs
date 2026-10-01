using System.Text.Json.Serialization;

namespace DwgTranslator.Contracts;

public sealed record TranslationBatchRequestPayload
{
    [JsonRequired] public required string? SourceLanguage { get; init; }
    [JsonRequired] public required string TargetLanguage { get; init; }
    [JsonRequired] public required string PromptTemplateVersion { get; init; }
    [JsonRequired] public required List<TranslationGlossaryEntry> Glossary { get; init; }
    [JsonRequired] public required List<TranslationSegment> Segments { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public TranslationRoutingPolicy? Routing { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public TranslationEscalationContext? Escalation { get; init; }
}

public sealed record TranslationGlossaryEntry
{
    [JsonRequired] public required string Source { get; init; }
    [JsonRequired] public required string Target { get; init; }
    public bool CaseSensitive { get; init; }
}

public sealed record TranslationSegment
{
    [JsonRequired] public required string SegmentId { get; init; }
    [JsonRequired] public required string TextWithTokenAliases { get; init; }
    [JsonRequired] public required Dictionary<string, string> ProtectedTokenAliases { get; init; }
    [JsonRequired] public required TranslationSegmentContext Context { get; init; }
}

public sealed record TranslationSegmentContext
{
    [JsonRequired] public required string EntityType { get; init; }
    [JsonRequired] public required string Space { get; init; }
    [JsonRequired] public required string? Layout { get; init; }
    [JsonRequired] public required string Layer { get; init; }
    [JsonRequired] public required List<string> BlockPath { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ContextVersion { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ContextHash { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SemanticKey { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? SheetRole { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Discipline { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? DisciplineConflict { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? DisciplineResolution { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? XBand { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? YBand { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? Signals { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<TranslationNeighborExcerpt>? NeighborExcerpts { get; init; }
}

public sealed record TranslationNeighborExcerpt
{
    [JsonRequired] public required string SegmentId { get; init; }
    [JsonRequired] public required string SourceTextHash { get; init; }
    [JsonRequired] public required string EntityType { get; init; }
    [JsonRequired] public required string Relation { get; init; }
    [JsonRequired] public required int DistanceBand { get; init; }
    [JsonRequired] public required bool SameLayer { get; init; }
    [JsonRequired] public required string Text { get; init; }
}

public sealed record TranslationBatchResponsePayload
{
    [JsonRequired] public required string Model { get; init; }
    [JsonRequired] public required string PromptTemplateVersion { get; init; }
    [JsonRequired] public required List<TranslationProposal> Proposals { get; init; }
    [JsonRequired] public required TranslationUsage Usage { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public TranslationRoutingTrace? Routing { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ProviderRequestId { get; init; }
}

public sealed record TranslationProposal
{
    [JsonRequired] public required string SegmentId { get; init; }
    [JsonRequired] public required string TranslatedTextWithTokenAliases { get; init; }
    [JsonRequired] public required string TokenIntegrity { get; init; }
}

public sealed record TranslationUsage
{
    [JsonRequired] public required int InputTokens { get; init; }
    [JsonRequired] public required int OutputTokens { get; init; }
}
