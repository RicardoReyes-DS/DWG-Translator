using System.Globalization;
using System.Text.RegularExpressions;
using DwgTranslator.Contracts;

namespace DwgTranslator.Application;

public static class TranslationRoutingPolicyFactory
{
    public static TranslationRoutingPolicy Auto() => Create(TranslationRouting.Auto, TranslationRouting.Terra, TranslationRouting.Sol);
    public static TranslationRoutingPolicy Economy() => Create(TranslationRouting.Economy, TranslationRouting.Luna, null);
    public static TranslationRoutingPolicy MaximumQuality() => Create(TranslationRouting.MaximumQuality, TranslationRouting.Sol, null);
    public static TranslationRoutingPolicy Manual(string model) => Create(TranslationRouting.Manual, model, null);

    public static Result<TranslationRoutingPolicy> Validate(TranslationRoutingPolicy? policy)
    {
        if (policy is null || policy.Version != TranslationRouting.PolicyVersion ||
            policy.ReasoningEffort != TranslationRouting.ReasoningNone || !ValidModel(policy.BaseModel) ||
            policy.BaseModel == "gpt-5.6" || policy.MaxEscalationsPerSegment is < 0 or > 1 ||
            policy.HighMinLengthRatio is <= 0 or >= 1 || policy.MediumMinLengthRatio < policy.HighMinLengthRatio ||
            policy.MediumMaxLengthRatio <= 1 || policy.HighMaxLengthRatio < policy.MediumMaxLengthRatio)
            return Failure("TRANSLATION_ROUTING_POLICY_INVALID");

        var valid = policy.RequestedMode switch
        {
            TranslationRouting.Auto => policy.BaseModel == TranslationRouting.Terra && policy.EscalationModel == TranslationRouting.Sol && policy.MaxEscalationsPerSegment == 1,
            TranslationRouting.Economy => policy.BaseModel == TranslationRouting.Luna && policy.EscalationModel is null && policy.MaxEscalationsPerSegment == 0,
            TranslationRouting.MaximumQuality => policy.BaseModel == TranslationRouting.Sol && policy.EscalationModel is null && policy.MaxEscalationsPerSegment == 0,
            TranslationRouting.Manual => policy.EscalationModel is null && policy.MaxEscalationsPerSegment == 0,
            _ => false
        };
        return valid ? Results.Success(policy) : Failure("TRANSLATION_ROUTING_POLICY_TIER_MISMATCH");
    }

    private static TranslationRoutingPolicy Create(string mode, string model, string? escalation) => new()
    {
        Version = TranslationRouting.PolicyVersion,
        RequestedMode = mode,
        BaseModel = model,
        ReasoningEffort = TranslationRouting.ReasoningNone,
        EscalationModel = escalation,
        MaxEscalationsPerSegment = escalation is null ? 0 : 1,
        EscalateHighRisk = escalation is not null
    };

    private static bool ValidModel(string? model) =>
        !string.IsNullOrWhiteSpace(model) && model.Length <= 120 && !model.Any(char.IsWhiteSpace);

    private static Result<TranslationRoutingPolicy> Failure(string code) =>
        Results.Failure<TranslationRoutingPolicy>(new ContractError(code, ErrorCategory.Configuration, "The linguistic routing policy is invalid.", false));
}

public sealed record TranslationRiskAssessment(
    string SegmentId,
    string RiskSeverity,
    IReadOnlyList<string> ReasonCodes,
    string ProposedTextWithAliases,
    bool InvariantsPreserved)
{
    public int Rank => RiskSeverity switch { "high" => 3, "medium" => 2, "low" => 1, _ => 0 };
}

public static partial class TranslationRiskEvaluator
{
    public static IReadOnlyList<TranslationRiskAssessment> Evaluate(
        TranslationBatchRequestPayload request,
        IReadOnlyList<TranslationProposal> proposals,
        TranslationRoutingPolicy policy)
    {
        var byId = proposals.Where(item => item is not null && !string.IsNullOrWhiteSpace(item.SegmentId))
            .GroupBy(item => item.SegmentId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var repeated = request.Segments.GroupBy(segment => new RepeatedSourceContextKey(
                segment.TextWithTokenAliases,
                segment.Context.SemanticKey ?? string.Empty))
            .Where(group => group.Count() > 1)
            .ToDictionary(group => group.Key, group => group.Select(item => item.SegmentId).ToArray());
        var result = new List<TranslationRiskAssessment>(request.Segments.Count);
        foreach (var segment in request.Segments)
        {
            var codes = new HashSet<string>(StringComparer.Ordinal);
            if (!byId.TryGetValue(segment.SegmentId, out var candidates))
            {
                codes.Add("MISSING_ID");
                result.Add(new(segment.SegmentId, "high", codes.ToArray(), string.Empty, false));
                continue;
            }
            if (candidates.Length != 1) codes.Add("DUPLICATE_ID");
            var proposal = candidates[0];
            var target = proposal.TranslatedTextWithTokenAliases ?? string.Empty;
            if (target.Length == 0) codes.Add("EMPTY_TRANSLATION");
            if (!TranslationTokenAliases.Restore(target, segment.ProtectedTokenAliases, segment.TextWithTokenAliases).IsSuccess ||
                proposal.TokenIntegrity != "Valid") codes.Add("PROTECTED_TOKEN_CHANGED");
            if (!Multiset(NumberPattern().Matches(segment.TextWithTokenAliases)).SequenceEqual(Multiset(NumberPattern().Matches(target)), StringComparer.Ordinal)) codes.Add("NUMBER_CHANGED");
            if (!Multiset(UnitPattern().Matches(segment.TextWithTokenAliases)).SequenceEqual(Multiset(UnitPattern().Matches(target)), StringComparer.OrdinalIgnoreCase)) codes.Add("UNIT_CHANGED");
            if (!Multiset(PlaceholderPattern().Matches(segment.TextWithTokenAliases)).SequenceEqual(Multiset(PlaceholderPattern().Matches(target)), StringComparer.Ordinal)) codes.Add("PLACEHOLDER_CHANGED");
            if (!Multiset(CadCodePattern().Matches(segment.TextWithTokenAliases)).SequenceEqual(Multiset(CadCodePattern().Matches(target)), StringComparer.Ordinal)) codes.Add("CAD_CODE_CHANGED");
            if (LineSignature(segment.TextWithTokenAliases) != LineSignature(target)) codes.Add("LINE_STRUCTURE_CHANGED");
            if (!Multiset(PunctuationPattern().Matches(segment.TextWithTokenAliases)).SequenceEqual(Multiset(PunctuationPattern().Matches(target)), StringComparer.Ordinal))
                codes.Add("PUNCTUATION_CHANGED");
            var ratio = target.Length / (double)Math.Max(1, segment.TextWithTokenAliases.Length);
            if (ratio < policy.HighMinLengthRatio) codes.Add("LENGTH_RATIO_HIGH_LOW");
            else if (ratio > policy.HighMaxLengthRatio) codes.Add("LENGTH_RATIO_HIGH_HIGH");
            else if (ratio < policy.MediumMinLengthRatio || ratio > policy.MediumMaxLengthRatio) codes.Add("LENGTH_RATIO_ANOMALOUS");
            if (!GlossaryValid(request.Glossary, segment.TextWithTokenAliases, target)) codes.Add("GLOSSARY_MISSING");
            var repeatedSourceKey = new RepeatedSourceContextKey(
                segment.TextWithTokenAliases,
                segment.Context.SemanticKey ?? string.Empty);
            if (repeated.TryGetValue(repeatedSourceKey, out var peers))
            {
                var peerTargets = peers.Where(byId.ContainsKey).Select(id => byId[id][0].TranslatedTextWithTokenAliases).Distinct(StringComparer.Ordinal).Take(2).Count();
                if (peerTargets > 1) codes.Add("REPEATED_SOURCE_INCONSISTENT");
            }
            var high = codes.Any(code => code is "MISSING_ID" or "DUPLICATE_ID" or "EMPTY_TRANSLATION" or "PROTECTED_TOKEN_CHANGED" or
                "NUMBER_CHANGED" or "UNIT_CHANGED" or "PLACEHOLDER_CHANGED" or "CAD_CODE_CHANGED" or "GLOSSARY_MISSING" or
                "LENGTH_RATIO_HIGH_LOW" or "LENGTH_RATIO_HIGH_HIGH");
            var low = codes.Count > 0 && codes.All(code => code == "PUNCTUATION_CHANGED");
            var medium = codes.Count > 0 && !low;
            var severity = high ? "high" : medium ? "medium" : low ? "low" : "none";
            result.Add(new(segment.SegmentId, severity, codes.OrderBy(code => code, StringComparer.Ordinal).ToArray(), target, !high));
        }
        return result;
    }

    public static bool PreferCandidate(TranslationRiskAssessment current, TranslationRiskAssessment candidate) =>
        candidate.InvariantsPreserved && (candidate.Rank < current.Rank || candidate.Rank == current.Rank && candidate.ReasonCodes.Count < current.ReasonCodes.Count);

    private static string[] Multiset(MatchCollection matches) => matches.Select(match => match.Value).OrderBy(value => value, StringComparer.Ordinal).ToArray();
    private static string LineSignature(string value) => string.Join(',', value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(line => line.Length == 0 ? "E" : "N"));

    private static bool GlossaryValid(IEnumerable<TranslationGlossaryEntry> glossary, string source, string target) => glossary.All(entry =>
    {
        var comparison = entry.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var required = Count(source, entry.Source, comparison);
        return required == 0 || Count(target, entry.Target, comparison) >= required;
    });

    private static int Count(string value, string term, StringComparison comparison)
    {
        if (term.Length == 0) return 0;
        var count = 0;
        for (var index = 0; index <= value.Length - term.Length;)
        {
            var found = value.IndexOf(term, index, comparison);
            if (found < 0) break;
            count++;
            index = found + term.Length;
        }
        return count;
    }

    private readonly record struct RepeatedSourceContextKey(string TextWithTokenAliases, string SemanticKey);

    [GeneratedRegex("(?<![\\p{L}\\d])[-+]?\\d+(?:[.,]\\d+)?(?![\\p{L}\\d])", RegexOptions.CultureInvariant)] private static partial Regex NumberPattern();
    [GeneratedRegex("(?i)(?<![\\p{L}])(?:mm|cm|m|km|in|ft|kg|g|kN|N|MPa|kPa|°C|°F|V|A|Hz)(?![\\p{L}])", RegexOptions.CultureInvariant)] private static partial Regex UnitPattern();
    [GeneratedRegex("(?:\\{\\{[^{}]+\\}\\}|%[A-Za-z0-9_]+%|<[^<>]+>)", RegexOptions.CultureInvariant)] private static partial Regex PlaceholderPattern();
    [GeneratedRegex("(?<![\\p{L}\\d])(?:[A-Z]{2,}[A-Z0-9_-]*\\d+[A-Z0-9_-]*|[A-Z]+-\\d+[A-Z0-9_-]*)(?![\\p{L}\\d])", RegexOptions.CultureInvariant)] private static partial Regex CadCodePattern();
    [GeneratedRegex("[,:;.!?]", RegexOptions.CultureInvariant)] private static partial Regex PunctuationPattern();
}
