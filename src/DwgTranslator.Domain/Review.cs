namespace DwgTranslator.Domain;

public enum SegmentState
{
    Extracted,
    Queued,
    Translating,
    Proposed,
    Edited,
    Approved,
    Excluded,
    WriteSucceeded,
    WriteFailed
}

public sealed record SegmentReview
{
    private SegmentReview(string segmentId, SegmentState state, string? finalText, string? exclusionReason)
    {
        SegmentId = segmentId;
        State = state;
        FinalText = finalText;
        ExclusionReason = exclusionReason;
    }

    public string SegmentId { get; }
    public SegmentState State { get; }
    public string? FinalText { get; }
    public string? ExclusionReason { get; }

    public static DomainResult<SegmentReview> Pending(string? segmentId, SegmentState state, string? text = null)
    {
        if (string.IsNullOrWhiteSpace(segmentId) || state is SegmentState.Approved or SegmentState.Excluded)
            return DomainResults.Failure<SegmentReview>("SEGMENT_REVIEW_INVALID");
        return DomainResults.Success(new SegmentReview(segmentId, state, text, null));
    }

    public static DomainResult<SegmentReview> Approve(string? segmentId, string? finalText)
    {
        if (string.IsNullOrWhiteSpace(segmentId) || string.IsNullOrEmpty(finalText))
            return DomainResults.Failure<SegmentReview>("APPROVED_TEXT_REQUIRED");
        return DomainResults.Success(new SegmentReview(segmentId, SegmentState.Approved, finalText, null));
    }

    public static DomainResult<SegmentReview> Exclude(string? segmentId, string? reason)
    {
        if (string.IsNullOrWhiteSpace(segmentId))
            return DomainResults.Failure<SegmentReview>("SEGMENT_ID_REQUIRED");
        if (string.IsNullOrWhiteSpace(reason))
            return DomainResults.Failure<SegmentReview>("EXCLUSION_REASON_REQUIRED");
        return DomainResults.Success(new SegmentReview(segmentId, SegmentState.Excluded, null, reason));
    }

    public DomainResult<SegmentReview> Edit(string? text) => string.IsNullOrEmpty(text)
        ? DomainResults.Failure<SegmentReview>("EDITED_TEXT_REQUIRED")
        : DomainResults.Success(new SegmentReview(SegmentId, SegmentState.Edited, text, null));
}

public sealed record ApprovalCoverage(int Selected, int Approved, int Excluded)
{
    public bool IsComplete => Selected > 0 && Selected == Approved + Excluded;
}

public static class ApprovalPolicy
{
    public static DomainResult<ApprovalCoverage> Evaluate(IEnumerable<SegmentReview> segments)
    {
        var materialized = segments.ToArray();
        if (materialized.Length == 0)
            return DomainResults.Failure<ApprovalCoverage>("APPROVAL_EMPTY");
        if (materialized.Any(segment => string.IsNullOrWhiteSpace(segment.SegmentId)) ||
            materialized.Select(segment => segment.SegmentId).Distinct(StringComparer.Ordinal).Count() != materialized.Length)
            return DomainResults.Failure<ApprovalCoverage>("SEGMENT_ID_DUPLICATE_OR_EMPTY");

        var approved = materialized.Count(segment =>
            segment.State == SegmentState.Approved && !string.IsNullOrEmpty(segment.FinalText) && segment.ExclusionReason is null);
        var excluded = materialized.Count(segment =>
            segment.State == SegmentState.Excluded && segment.FinalText is null && !string.IsNullOrWhiteSpace(segment.ExclusionReason));
        var coverage = new ApprovalCoverage(materialized.Length, approved, excluded);
        return coverage.IsComplete
            ? DomainResults.Success(coverage)
            : DomainResults.Failure<ApprovalCoverage>("APPROVAL_INCOMPLETE_OR_INVALID");
    }

    public static bool CanWrite(JobState jobState, IEnumerable<SegmentReview> segments) =>
        jobState == JobState.Approved && Evaluate(segments).IsSuccess;
}
