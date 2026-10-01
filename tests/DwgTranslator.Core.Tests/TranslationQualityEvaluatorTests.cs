using DwgTranslator.Application;
using DwgTranslator.Contracts;
using DwgTranslator.Domain;

namespace DwgTranslator.Core.Tests;

[TestClass]
public sealed class TranslationQualityEvaluatorTests
{
    [TestMethod]
    public void MeasuresOperatorAcceptanceFormattingGlossaryAndExpansion()
    {
        var review = new TranslationReviewSnapshot(Guid.NewGuid(), 1, "es-MX", "translate-cad-text/1.1", 1, 1,
            DateTimeOffset.UtcNow,
            [
                Row("seg-001", " PUMP\nROOM ", " BOMBA\nCUARTO ", " BOMBA\nCUARTO ", SegmentState.Approved),
                Row("seg-002", "PUMP", "BOMBA", "BOMBA PRINCIPAL", SegmentState.Approved),
                Row("seg-003", "KEEP", "KEEP", "KEEP", SegmentState.Excluded)
            ]);

        var result = TranslationQualityEvaluator.Evaluate(review,
            [new TranslationGlossaryEntry { Source = "PUMP", Target = "BOMBA", CaseSensitive = false }]);

        Assert.IsTrue(result.IsSuccess, result.Error?.Code);
        Assert.AreEqual(3, result.Value!.TotalSegments);
        Assert.AreEqual(2, result.Value.ApprovedSegments);
        Assert.AreEqual(1, result.Value.AcceptedWithoutEdit);
        Assert.AreEqual(1, result.Value.EditedByOperator);
        Assert.AreEqual(2, result.Value.LineBreaksPreserved);
        Assert.AreEqual(2, result.Value.OuterWhitespacePreserved);
        Assert.AreEqual(2, result.Value.GlossaryCompliant);
        Assert.AreEqual(0.5, result.Value.AcceptanceWithoutEditRate);
        Assert.IsTrue(result.Value.MaximumExpansionRatio > 1);
    }

    [TestMethod]
    public void RejectsIncompleteReview()
    {
        var review = new TranslationReviewSnapshot(Guid.NewGuid(), 0, "es-MX", "translate-cad-text/1.1", 1, 1,
            DateTimeOffset.UtcNow, [Row("seg-001", "A", "B", "B", SegmentState.Proposed)]);

        Assert.AreEqual("QUALITY_REVIEW_INCOMPLETE", TranslationQualityEvaluator.Evaluate(review, []).Error!.Code);
    }

    private static ReviewRowSnapshot Row(string id, string original, string proposed, string final, SegmentState state) =>
        new(id, original, proposed, final, state, state == SegmentState.Excluded ? "KEEP_ORIGINAL" : null, null);
}
