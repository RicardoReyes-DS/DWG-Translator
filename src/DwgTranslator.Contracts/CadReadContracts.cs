using System.Text.Json.Serialization;

namespace DwgTranslator.Contracts;

public sealed record CadInspectResponsePayload
{
    [JsonRequired]
    public required string SourceHash { get; init; }

    [JsonRequired]
    public required CadHostDescriptor Autocad { get; init; }

    [JsonRequired]
    public required string DrawingFingerprint { get; init; }

    [JsonRequired]
    public required Dictionary<string, int> Inventory { get; init; }

    [JsonRequired]
    public required int SupportedCount { get; init; }

    [JsonRequired]
    public required List<CadUnsupportedSummary> Unsupported { get; init; }

    [JsonRequired]
    public required List<string> Warnings { get; init; }
}

public sealed record CadHostDescriptor
{
    [JsonRequired]
    public required string Product { get; init; }

    [JsonRequired]
    public required int Year { get; init; }

    [JsonRequired]
    public required string ApiVersion { get; init; }
}

public sealed record CadUnsupportedSummary
{
    [JsonRequired]
    public required string Classification { get; init; }

    [JsonRequired]
    public required string EntityType { get; init; }

    [JsonRequired]
    public required int Count { get; init; }

    [JsonRequired]
    public required string ReasonCode { get; init; }

    public string? OriginalClassification { get; init; }
}

public sealed record CadExtractResponsePayload
{
    [JsonRequired]
    public required string SourceHash { get; init; }

    [JsonRequired]
    public required List<CadTextSegment> Segments { get; init; }

    [JsonRequired]
    public required int ExcludedFieldCount { get; init; }
}

public sealed record CadTextSegment
{
    [JsonRequired]
    public required string SegmentId { get; init; }

    [JsonRequired]
    public required CadEntityReference Entity { get; init; }

    [JsonRequired]
    public required string SourceText { get; init; }

    [JsonRequired]
    public required string SourceTextHash { get; init; }

    [JsonRequired]
    public required string LineBreakStyle { get; init; }

    [JsonRequired]
    public required List<CadProtectedToken> ProtectedTokens { get; init; }

    [JsonRequired]
    public required string FieldClassification { get; init; }

    [JsonRequired]
    public required string State { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CadSemanticContext? SemanticContext { get; init; }
}

public sealed record CadSemanticContext
{
    [JsonRequired] public required string Version { get; init; }
    [JsonRequired] public required string ContextHash { get; init; }
    [JsonRequired] public required string SemanticKey { get; init; }
    [JsonRequired] public required string SheetRole { get; init; }
    [JsonRequired] public required string Discipline { get; init; }
    [JsonRequired] public required List<string> DisciplineEvidence { get; init; }
    [JsonRequired] public required bool DisciplineConflict { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? DisciplineResolution { get; init; }
    [JsonRequired] public required string AnchorSource { get; init; }
    [JsonRequired] public required int XBand { get; init; }
    [JsonRequired] public required int YBand { get; init; }
    [JsonRequired] public required int ReadingOrder { get; init; }
    [JsonRequired] public required List<string> Signals { get; init; }
    [JsonRequired] public required string NeighborhoodDigest { get; init; }
    [JsonRequired] public required List<CadSemanticNeighbor> Neighbors { get; init; }
}

public sealed record CadSemanticNeighbor
{
    [JsonRequired] public required string SegmentId { get; init; }
    [JsonRequired] public required string SourceTextHash { get; init; }
    [JsonRequired] public required string EntityType { get; init; }
    [JsonRequired] public required string Relation { get; init; }
    [JsonRequired] public required int DistanceBand { get; init; }
    [JsonRequired] public required bool SameLayer { get; init; }
}

public sealed record CadEntityReference
{
    [JsonRequired]
    public required string Type { get; init; }

    [JsonRequired]
    public required string Handle { get; init; }

    [JsonRequired]
    public required string Space { get; init; }

    [JsonRequired]
    public required string? Layout { get; init; }

    [JsonRequired]
    public required List<string> BlockPath { get; init; }

    [JsonRequired]
    public required string Layer { get; init; }

    [JsonRequired]
    public required int SubIndex { get; init; }
}

public sealed record CadProtectedToken
{
    [JsonRequired]
    public required string Token { get; init; }

    [JsonRequired]
    public required string Kind { get; init; }

    [JsonRequired]
    public required int Ordinal { get; init; }
}
