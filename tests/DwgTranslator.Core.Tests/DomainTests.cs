using DwgTranslator.Domain;

namespace DwgTranslator.Core.Tests;

[TestClass]
public sealed class DomainTests
{
    [TestMethod]
    public void LifecycleAllowsOnlyNormativeEdges()
    {
        Assert.IsTrue(JobLifecycle.CanTransition(JobState.Draft, JobState.Inspecting));
        Assert.IsTrue(JobLifecycle.CanTransition(JobState.Draft, JobState.Failed));
        Assert.IsTrue(JobLifecycle.CanTransition(JobState.Approved, JobState.Writing));
        Assert.IsFalse(JobLifecycle.CanTransition(JobState.Draft, JobState.Writing));
        Assert.IsFalse(JobLifecycle.CanTransition(JobState.Completed, JobState.Draft));
    }

    [TestMethod]
    public void CancellationDistinguishesImmediatePendingAndTerminal()
    {
        Assert.AreEqual(CancellationDisposition.Allowed, JobLifecycle.RequestCancellation(JobState.Approved, false));
        Assert.AreEqual(CancellationDisposition.PendingSafeBoundary, JobLifecycle.RequestCancellation(JobState.Writing, false));
        Assert.AreEqual(CancellationDisposition.PendingSafeBoundary, JobLifecycle.RequestCancellation(JobState.Approved, true));
        Assert.AreEqual(CancellationDisposition.RejectedTerminal, JobLifecycle.RequestCancellation(JobState.Completed, false));
    }

    [TestMethod]
    public void ApprovalRequiresValidCompleteUniqueCoverage()
    {
        var approved = SegmentReview.Approve("one", "BOMBA").Value!;
        var excluded = SegmentReview.Exclude("two", "field-backed").Value!;
        Assert.IsTrue(ApprovalPolicy.Evaluate([approved, excluded]).IsSuccess);
        Assert.IsTrue(ApprovalPolicy.CanWrite(JobState.Approved, [approved, excluded]));
        Assert.IsFalse(ApprovalPolicy.CanWrite(JobState.ReviewRequired, [approved, excluded]));
        Assert.AreEqual("APPROVAL_EMPTY", ApprovalPolicy.Evaluate([]).ErrorCode);
        Assert.AreEqual("SEGMENT_ID_DUPLICATE_OR_EMPTY", ApprovalPolicy.Evaluate([approved, approved]).ErrorCode);
    }

    [TestMethod]
    public void InvalidApprovalAndExclusionCannotBeConstructed()
    {
        Assert.AreEqual("APPROVED_TEXT_REQUIRED", SegmentReview.Approve("one", string.Empty).ErrorCode);
        Assert.AreEqual("APPROVED_TEXT_REQUIRED", SegmentReview.Approve(string.Empty, "text").ErrorCode);
        Assert.AreEqual("EXCLUSION_REASON_REQUIRED", SegmentReview.Exclude("one", " ").ErrorCode);
        Assert.AreEqual("SEGMENT_ID_REQUIRED", SegmentReview.Exclude(null, "reason").ErrorCode);
        Assert.AreEqual("SEGMENT_REVIEW_INVALID", SegmentReview.Pending("one", SegmentState.Approved).ErrorCode);
    }

    [TestMethod]
    public void EditingRevokesApproval()
    {
        var approved = SegmentReview.Approve("one", "old").Value!;
        var edited = approved.Edit("new").Value!;
        Assert.AreEqual(SegmentState.Edited, edited.State);
        Assert.IsFalse(ApprovalPolicy.Evaluate([edited]).IsSuccess);
    }

    [TestMethod]
    public void ResumeRequiresFailedStateAndMatchingSafeCheckpoint()
    {
        var source = ContentHash.Create(TestData.HashA).Value;
        var configuration = ContentHash.Create(TestData.HashB).Value;
        var checkpoint = new ResumeCheckpoint(JobState.ReviewRequired, source, "1.0.0", configuration);

        Assert.IsTrue(ResumePolicy.CanResume(JobState.Failed, checkpoint, source, "1.0.0", configuration));
        Assert.IsFalse(ResumePolicy.CanResume(JobState.Cancelled, checkpoint, source, "1.0.0", configuration));
        Assert.IsFalse(ResumePolicy.CanResume(JobState.Failed, checkpoint, source, "2.0.0", configuration));
        Assert.IsFalse(ResumePolicy.CanResume(JobState.Failed, checkpoint with { SafeState = JobState.Writing }, source, "1.0.0", configuration));
    }

    [TestMethod]
    public void ValueObjectsValidateAndNormalize()
    {
        Assert.IsFalse(JobId.Create(Guid.Empty).IsSuccess);
        Assert.IsTrue(ContentHash.Create(TestData.HashA).IsSuccess);
        Assert.IsFalse(ContentHash.Create(TestData.HashA.ToUpperInvariant()).IsSuccess);
        Assert.AreEqual("es-MX", LanguageTag.Create("ES-mx").Value.Value);
        Assert.AreEqual("zh-Hant-TW", LanguageTag.Create("ZH-hant-tw").Value.Value);
        Assert.IsFalse(LanguageTag.Create("not_a_language").IsSuccess);
    }
}
