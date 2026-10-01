using System.Text.Json;
using System.Text;
using DwgTranslator.Contracts;
using DwgTranslator.Domain;

namespace DwgTranslator.Application;

public static class TranslationBatchFactory
{
    public const int DefaultMaxSegments = 50;
    public const int DefaultMaxCharacters = 20_000;
    public const int MaximumNeighborExcerpts = 4;
    public const int MaximumNeighborExcerptScalars = 160;
    public const int MaximumNeighborExcerptScalarsPerSegment = 512;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static Result<IReadOnlyList<TranslationBatchRequestPayload>> Create(
        IReadOnlyList<CadTextSegment> segments,
        string? sourceLanguage,
        string targetLanguage,
        string promptTemplateVersion,
        IReadOnlyList<TranslationGlossaryEntry>? glossary = null,
        int maxSegments = DefaultMaxSegments,
        int maxCharacters = DefaultMaxCharacters,
        TranslationRoutingPolicy? routing = null)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Count == 0 || maxSegments is < 1 or > 1_000 || maxCharacters is < 1 or > 1_000_000 ||
            !LanguageTag.Create(targetLanguage).IsSuccess || (sourceLanguage is not null && !LanguageTag.Create(sourceLanguage).IsSuccess) ||
            string.IsNullOrWhiteSpace(promptTemplateVersion) || promptTemplateVersion.Length > 120)
            return Failure("TRANSLATION_BATCH_CONFIGURATION_INVALID", "Translation batching configuration is invalid.");
        if (routing is not null && !TranslationRoutingPolicyFactory.Validate(routing).IsSuccess)
            return Failure("TRANSLATION_ROUTING_POLICY_INVALID", "Translation routing configuration is invalid.");
        if (segments.Select(item => item.SegmentId).Distinct(StringComparer.Ordinal).Count() != segments.Count)
            return Failure("TRANSLATION_BATCH_SEGMENT_DUPLICATE", "Translation batches require unique segment IDs.");
        var contextualCount = segments.Count(segment => segment.SemanticContext is not null);
        if (contextualCount != 0 && contextualCount != segments.Count)
            return Failure("TRANSLATION_CONTEXT_PARTIAL", "Semantic context must cover either all segments or none for a legacy job.");
        if (contextualCount != 0)
        {
            var aggregate = CadSemanticContextBuilder.AggregateHash(segments);
            if (!aggregate.IsSuccess)
                return Results.Failure<IReadOnlyList<TranslationBatchRequestPayload>>(aggregate.Error!);
        }
        var glossaryItems = glossary?.ToList() ?? [];
        if (glossaryItems.Count > 10_000 || glossaryItems.Any(item => string.IsNullOrEmpty(item.Source) || item.Source.Length > 500 || string.IsNullOrEmpty(item.Target) || item.Target.Length > 500))
            return Failure("TRANSLATION_GLOSSARY_INVALID", "Glossary entries are invalid or exceed contract limits.");

        var sourceById = segments.ToDictionary(segment => segment.SegmentId, StringComparer.Ordinal);
        var prepared = new List<TranslationSegment>(segments.Count);
        foreach (var segment in segments)
        {
            if (!ContractPatterns.SegmentId().IsMatch(segment.SegmentId) || segment.State != "Extracted" || segment.FieldClassification != "None" ||
                segment.Entity.BlockPath.Count != 0 || segment.Entity.Type is not ("TEXT" or "MTEXT"))
                return Failure("TRANSLATION_SEGMENT_INVALID", "Only direct extracted TEXT/MTEXT segments without fields can be translated.");
            var protectedText = TranslationTokenAliases.Protect(segment.SourceText, segment.ProtectedTokens);
            if (!protectedText.IsSuccess) return Results.Failure<IReadOnlyList<TranslationBatchRequestPayload>>(protectedText.Error!);
            var context = BuildContext(segment, sourceById);
            if (!context.IsSuccess) return Results.Failure<IReadOnlyList<TranslationBatchRequestPayload>>(context.Error!);
            prepared.Add(new TranslationSegment
            {
                SegmentId = segment.SegmentId,
                TextWithTokenAliases = protectedText.Value!.Text,
                ProtectedTokenAliases = new Dictionary<string, string>(protectedText.Value.Aliases, StringComparer.Ordinal),
                Context = context.Value!
            });
        }

        var result = new List<TranslationBatchRequestPayload>();
        var current = new List<TranslationSegment>();
        var characters = 0;
        foreach (var segment in prepared)
        {
            var size = JsonSerializer.Serialize(segment, JsonOptions).Length;
            if (size > maxCharacters) return Failure("TRANSLATION_SEGMENT_TOO_LARGE", "A single protected segment exceeds the batch character limit.");
            if (current.Count == maxSegments || characters + size > maxCharacters)
            {
                result.Add(Payload(current));
                current = [];
                characters = 0;
            }
            current.Add(segment);
            characters += size;
        }
        if (current.Count > 0) result.Add(Payload(current));
        return Results.Success<IReadOnlyList<TranslationBatchRequestPayload>>(result);

        TranslationBatchRequestPayload Payload(List<TranslationSegment> items)
        {
            var requestIds = items.Select(item => item.SegmentId).ToHashSet(StringComparer.Ordinal);
            var requestItems = items.Select(item => item.Context.NeighborExcerpts is null
                ? item
                : item with
                {
                    Context = item.Context with
                    {
                        NeighborExcerpts = item.Context.NeighborExcerpts
                    .Where(excerpt => requestIds.Contains(excerpt.SegmentId)).ToList()
                    }
                }).ToList();
            return new()
            {
                SourceLanguage = sourceLanguage,
                TargetLanguage = targetLanguage,
                PromptTemplateVersion = promptTemplateVersion,
                Glossary = glossaryItems,
                Segments = requestItems,
                Routing = routing
            };
        }
    }

    public static string ConfigurationHash(TranslationBatchRequestPayload payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        return "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static Result<TranslationSegmentContext> BuildContext(
        CadTextSegment segment,
        Dictionary<string, CadTextSegment> sourceById)
    {
        var semantic = segment.SemanticContext;
        if (semantic is null)
        {
            return Results.Success(new TranslationSegmentContext
            {
                EntityType = segment.Entity.Type,
                Space = segment.Entity.Space,
                Layout = segment.Entity.Layout,
                Layer = segment.Entity.Layer,
                BlockPath = []
            });
        }

        var excerpts = new List<TranslationNeighborExcerpt>(semantic.Neighbors.Count);
        var remaining = MaximumNeighborExcerptScalarsPerSegment;
        foreach (var neighbor in semantic.Neighbors.Take(MaximumNeighborExcerpts))
        {
            if (remaining == 0 || !sourceById.TryGetValue(neighbor.SegmentId, out var source)) break;
            var maximum = Math.Min(MaximumNeighborExcerptScalars, remaining);
            if (!TryTakeScalars(source, maximum, out var excerpt, out var used)) continue;
            remaining -= used;
            excerpts.Add(new TranslationNeighborExcerpt
            {
                SegmentId = neighbor.SegmentId,
                SourceTextHash = neighbor.SourceTextHash,
                EntityType = neighbor.EntityType,
                Relation = neighbor.Relation,
                DistanceBand = neighbor.DistanceBand,
                SameLayer = neighbor.SameLayer,
                Text = excerpt
            });
        }
        return Results.Success(new TranslationSegmentContext
        {
            EntityType = segment.Entity.Type,
            Space = segment.Entity.Space,
            Layout = segment.Entity.Layout,
            Layer = segment.Entity.Layer,
            BlockPath = [],
            ContextVersion = semantic.Version,
            ContextHash = semantic.ContextHash,
            SemanticKey = semantic.SemanticKey,
            SheetRole = semantic.SheetRole,
            Discipline = semantic.Discipline,
            DisciplineConflict = semantic.DisciplineConflict,
            DisciplineResolution = semantic.DisciplineResolution,
            XBand = semantic.XBand,
            YBand = semantic.YBand,
            Signals = semantic.Signals.ToList(),
            NeighborExcerpts = excerpts
        });
    }

    private static bool TryTakeScalars(CadTextSegment source, int maximum, out string excerpt, out int used)
    {
        var result = new StringBuilder(Math.Min(source.SourceText.Length, maximum));
        used = 0;
        foreach (var rune in source.SourceText.EnumerateRunes())
        {
            if (used == maximum) break;
            result.Append(rune.ToString());
            used++;
        }
        excerpt = result.ToString();
        if (excerpt.Length == source.SourceText.Length || SafeProtectedTokenBoundary(source.SourceText, excerpt.Length, source.ProtectedTokens)) return true;
        excerpt = string.Empty;
        used = 0;
        return false;
    }

    internal static bool SafeProtectedTokenBoundary(string sourceText, int utf16Boundary, IReadOnlyList<CadProtectedToken>? protectedTokens)
    {
        if (protectedTokens is null) return false;
        var searchStart = 0;
        for (var index = 0; index < protectedTokens.Count; index++)
        {
            var token = protectedTokens[index];
            if (token is null || token.Ordinal != index || string.IsNullOrEmpty(token.Token)) return false;
            var start = sourceText.IndexOf(token.Token, searchStart, StringComparison.Ordinal);
            if (start < 0) return false;
            var end = start + token.Token.Length;
            if (start < utf16Boundary && end > utf16Boundary) return false;
            searchStart = end;
        }
        return true;
    }

    private static Result<IReadOnlyList<TranslationBatchRequestPayload>> Failure(string code, string message) =>
        Results.Failure<IReadOnlyList<TranslationBatchRequestPayload>>(new ContractError(code, ErrorCategory.Translation, message, false));
}
