using DwgTranslator.Contracts;
using DwgTranslator.Domain;

namespace DwgTranslator.Application;

public sealed record TranslationQualityMetrics(
    int TotalSegments,
    int ApprovedSegments,
    int ExcludedSegments,
    int AcceptedWithoutEdit,
    int EditedByOperator,
    int WarningSegments,
    int LineBreaksPreserved,
    int OuterWhitespacePreserved,
    int GlossaryCompliant,
    double AcceptanceWithoutEditRate,
    double AverageExpansionRatio,
    double MaximumExpansionRatio);

public static class TranslationQualityEvaluator
{
    public static Result<TranslationQualityMetrics> Evaluate(
        TranslationReviewSnapshot review,
        IReadOnlyList<TranslationGlossaryEntry>? glossary)
    {
        ArgumentNullException.ThrowIfNull(review);
        glossary ??= [];
        if (review.Rows.Count == 0 || review.Rows.Any(row => row.State is not (SegmentState.Approved or SegmentState.Excluded)))
            return Failure("QUALITY_REVIEW_INCOMPLETE", "Quality metrics require a complete human review.");

        var approved = review.Rows.Where(row => row.State == SegmentState.Approved).ToArray();
        var accepted = approved.Count(row => string.Equals(row.ProposedText, row.FinalText, StringComparison.Ordinal));
        var lineBreaks = approved.Count(row => LineBreakCount(row.OriginalText) == LineBreakCount(row.FinalText));
        var whitespace = approved.Count(row => OuterWhitespace(row.OriginalText) == OuterWhitespace(row.FinalText));
        var glossaryCompliant = approved.Count(row => GlossaryCompliant(row.OriginalText, row.FinalText, glossary));
        var ratios = approved.Select(row => ExpansionRatio(row.OriginalText, row.FinalText)).ToArray();
        return Results.Success(new TranslationQualityMetrics(
            review.Rows.Count,
            approved.Length,
            review.Rows.Count - approved.Length,
            accepted,
            approved.Length - accepted,
            review.Rows.Count(row => !string.IsNullOrWhiteSpace(row.WarningCode)),
            lineBreaks,
            whitespace,
            glossaryCompliant,
            approved.Length == 0 ? 0 : (double)accepted / approved.Length,
            ratios.Length == 0 ? 0 : ratios.Average(),
            ratios.Length == 0 ? 0 : ratios.Max()));
    }

    private static int LineBreakCount(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Count(character => character == '\n');

    private static string OuterWhitespace(string value)
    {
        var leading = value.TakeWhile(char.IsWhiteSpace);
        var trailing = value.Reverse().TakeWhile(char.IsWhiteSpace).Reverse();
        return new string(leading.Append('\0').Concat(trailing).ToArray());
    }

    private static double ExpansionRatio(string source, string final) => source.Length == 0 ? (final.Length == 0 ? 1 : final.Length) : (double)final.Length / source.Length;

    private static bool GlossaryCompliant(string source, string final, IReadOnlyList<TranslationGlossaryEntry> glossary)
    {
        foreach (var entry in glossary)
        {
            var comparison = entry.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var required = CountOccurrences(source, entry.Source, comparison);
            if (required > 0 && CountOccurrences(final, entry.Target, comparison) < required) return false;
        }
        return true;
    }

    private static int CountOccurrences(string value, string term, StringComparison comparison)
    {
        if (string.IsNullOrEmpty(term)) return 0;
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

    private static Result<TranslationQualityMetrics> Failure(string code, string message) =>
        Results.Failure<TranslationQualityMetrics>(new ContractError(code, ErrorCategory.Input, message, false));
}
