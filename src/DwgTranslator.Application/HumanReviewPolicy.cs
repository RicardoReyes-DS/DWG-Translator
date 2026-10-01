using DwgTranslator.Contracts;
using DwgTranslator.Domain;

namespace DwgTranslator.Application;

public static class HumanReviewPolicy
{
    public static Result<SegmentReview> Approve(CadTextSegment source, AcceptedTranslation proposal, string finalText)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(proposal);
        if (!string.Equals(source.SegmentId, proposal.SegmentId, StringComparison.Ordinal) || string.IsNullOrEmpty(finalText))
            return Failure("REVIEW_SEGMENT_MISMATCH", "Review source, proposal and final text do not match.");
        var expectedTokens = source.ProtectedTokens;
        var actualTokens = CadProtectedTokenPolicy.Extract(finalText);
        if (!expectedTokens.Select(item => item.Token).SequenceEqual(actualTokens.Select(item => item.Token), StringComparer.Ordinal))
            return Failure("TOKEN_INTEGRITY_FAILED", "Final reviewed text must preserve every CAD token exactly and in order.");
        var protectedSource = TranslationTokenAliases.Protect(source.SourceText, expectedTokens);
        var protectedFinal = TranslationTokenAliases.Protect(finalText, actualTokens);
        if (!protectedSource.IsSuccess || !protectedFinal.IsSuccess)
            return Failure("TOKEN_INTEGRITY_FAILED", "Final reviewed text must preserve text placement around every CAD token.");
        var restored = TranslationTokenAliases.Restore(
            protectedFinal.Value!.Text,
            protectedSource.Value!.Aliases,
            protectedSource.Value.Text);
        if (!restored.IsSuccess)
            return Failure("TOKEN_INTEGRITY_FAILED", "Final reviewed text must preserve text placement around every CAD token.");
        var approved = SegmentReview.Approve(source.SegmentId, restored.Value!);
        return approved.IsSuccess ? Results.Success(approved.Value!) : Failure(approved.ErrorCode!, "The final review decision is invalid.");
    }

    public static Result<SegmentReview> Exclude(CadTextSegment source, string reasonCode)
    {
        ArgumentNullException.ThrowIfNull(source);
        var excluded = SegmentReview.Exclude(source.SegmentId, reasonCode);
        return excluded.IsSuccess ? Results.Success(excluded.Value!) : Failure(excluded.ErrorCode!, "The exclusion decision is invalid.");
    }

    private static Result<SegmentReview> Failure(string code, string message) =>
        Results.Failure<SegmentReview>(new ContractError(code, ErrorCategory.Integrity, message, false));
}
