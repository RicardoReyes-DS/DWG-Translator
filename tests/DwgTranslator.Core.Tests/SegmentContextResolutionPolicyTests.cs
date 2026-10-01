using DwgTranslator.Application;
using DwgTranslator.Contracts;
using DwgTranslator.Domain;

namespace DwgTranslator.Core.Tests;

[TestClass]
public sealed class SegmentContextResolutionPolicyTests
{
    [TestMethod]
    public void ContextConflictAndResolutionAuthorityAreRejected()
    {
        var reviewerHash = "sha256:" + new string('a', 64);
        var qaHash = "sha256:" + new string('b', 64);
        var contextHash = "sha256:" + new string('c', 64);
        var sourceHash = SegmentContextResolutionPolicy.TextHash("FFL");
        var segment = Segment("seg_sha256_" + new string('a', 64),
            sourceHash, contextHash, conflict: true);
        var review = new TranslationReviewSnapshot(
            Guid.NewGuid(), 8, "en-US", "prompt", 1, 1, DateTimeOffset.UtcNow,
            [new(segment.SegmentId, "FFL", "FINISHED FLOOR LEVEL", "FINISHED FLOOR LEVEL",
                SegmentState.Approved, null, null)],
            ContextPolicyVersion: CadSemanticContextBuilder.PolicyVersionOneTwo,
            ContextHash: "sha256:" + new string('d', 64));
        var authority = new SegmentContextResolutionAuthority(
            SegmentContextResolutionPolicy.Version, segment.SegmentId, sourceHash, contextHash,
            SegmentContextResolutionPolicy.TextHash("FINISHED FLOOR LEVEL"),
            SegmentContextResolutionPolicy.VerticalSignal, "REVIEWED_VERTICAL_LEVEL_CONTEXT",
            reviewerHash, qaHash, string.Empty);
        var receipt = new ReviewAutomationReceipt(
            ReviewAutomationPolicy.ContextualAgentCreateNew, Guid.NewGuid(),
            "sha256:" + new string('e', 64), review.ContextHash!, reviewerHash, qaHash, [authority]);

        Assert.IsFalse(SegmentContextResolutionPolicy.IsStructurallyValid(authority));
        Assert.IsFalse(SegmentContextResolutionPolicy.ValidForReceipt(receipt, review, [segment]));
        Assert.IsFalse(SegmentContextResolutionPolicy.ValidForReceipt(receipt,
            review with { Rows = [review.Rows[0] with { FinalText = "TAMPERED" }] }, [segment]));

        var other = Segment("seg_sha256_" + new string('f', 64), sourceHash, contextHash, conflict: true);
        var otherAuthority = authority with { SegmentId = other.SegmentId, AuthorityHash = string.Empty };
        Assert.IsFalse(SegmentContextResolutionPolicy.IsStructurallyValid(otherAuthority));
        Assert.IsFalse(SegmentContextResolutionPolicy.ValidForReceipt(
            receipt with { SegmentContextResolutions = [otherAuthority] },
            review with { Rows = [review.Rows[0] with { SegmentId = other.SegmentId }] }, [other]));
    }

    [TestMethod]
    public void NoConflictRejectsAnySegmentAuthority()
    {
        var segment = Segment("seg_sha256_" + new string('f', 64),
            SegmentContextResolutionPolicy.TextHash("NOTE"),
            "sha256:" + new string('c', 64), conflict: false);
        var review = new TranslationReviewSnapshot(Guid.NewGuid(), 1, "en-US", "prompt", 1, 1,
            DateTimeOffset.UtcNow,
            [new(segment.SegmentId, "NOTE", "NOTE", "NOTE", SegmentState.Approved, null, null)]);
        var receipt = new ReviewAutomationReceipt(ReviewAutomationPolicy.ContextualAgentCreateNew,
            Guid.NewGuid(), "sha256:" + new string('d', 64), "sha256:" + new string('e', 64),
            "sha256:" + new string('a', 64), "sha256:" + new string('b', 64));

        Assert.IsTrue(SegmentContextResolutionPolicy.ValidForReceipt(receipt, review, [segment]));
    }

    private static CadTextSegment Segment(string segmentId, string sourceHash, string contextHash, bool conflict) => new()
    {
        SegmentId = segmentId,
        Entity = new CadEntityReference
        {
            Type = "MTEXT",
            Handle = "10",
            Space = "ModelSpace",
            Layout = null,
            BlockPath = [],
            Layer = "NOTES",
            SubIndex = 0
        },
        SourceText = conflict ? "FFL" : "NOTE",
        SourceTextHash = sourceHash,
        LineBreakStyle = "None",
        ProtectedTokens = [],
        FieldClassification = "PlainText",
        State = "Extracted",
        SemanticContext = new CadSemanticContext
        {
            Version = CadSemanticContextBuilder.PolicyVersionOneTwo,
            ContextHash = contextHash,
            SemanticKey = "sha256:" + new string('1', 64),
            SheetRole = "Model",
            Discipline = "Architectural",
            DisciplineEvidence = [],
            DisciplineConflict = false,
            DisciplineResolution = CadSemanticContextBuilder.DrawingNameArchitecturalFallback,
            AnchorSource = "ExtentsCenter",
            XBand = 0,
            YBand = 0,
            ReadingOrder = 0,
            Signals = conflict ? [SegmentContextResolutionPolicy.VerticalSignal] : [],
            NeighborhoodDigest = "sha256:" + new string('2', 64),
            Neighbors = []
        }
    };
}
