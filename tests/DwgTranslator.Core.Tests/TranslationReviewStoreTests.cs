using DwgTranslator.Application;
using DwgTranslator.Domain;
using DwgTranslator.Infrastructure.Local;

namespace DwgTranslator.Core.Tests;

[TestClass]
public sealed class TranslationReviewStoreTests
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize() => _root = Path.Combine(Path.GetTempPath(), "dwg-review-store-" + Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [TestMethod]
    public async Task ReviewSessionPersistsAtomicallyAndEnforcesVersion()
    {
        var store = new LocalTranslationReviewStore(new WorkspacePaths(_root));
        var jobId = Guid.NewGuid();
        var first = Snapshot(jobId, 0, SegmentState.Proposed, null);
        Assert.IsTrue((await store.SaveAsync(first, CancellationToken.None)).IsSuccess);
        var loaded = await store.LoadAsync(jobId, CancellationToken.None);
        Assert.AreEqual("BOMBA {TAG}", loaded.Value!.Rows[0].ProposedText);
        Assert.AreEqual("REVIEW_VERSION_CONFLICT", (await store.SaveAsync(first, CancellationToken.None)).Error!.Code);
        var second = Snapshot(jobId, 1, SegmentState.Approved, null);
        Assert.IsTrue((await store.SaveAsync(second, CancellationToken.None)).IsSuccess);
        Assert.AreEqual(1L, (await store.LoadAsync(jobId, CancellationToken.None)).Value!.Version);
    }

    [TestMethod]
    public async Task ExcludedRowsRequireReason()
    {
        var store = new LocalTranslationReviewStore(new WorkspacePaths(_root));
        Assert.AreEqual("REVIEW_ROW_INVALID", (await store.SaveAsync(Snapshot(Guid.NewGuid(), 0, SegmentState.Excluded, null), CancellationToken.None)).Error!.Code);
    }

    [TestMethod]
    public async Task ContextReceiptMustMatchSnapshotWhileLegacyRemainsReadable()
    {
        var store = new LocalTranslationReviewStore(new WorkspacePaths(_root));
        var legacy = Snapshot(Guid.NewGuid(), 0, SegmentState.Proposed, null);
        Assert.IsTrue((await store.SaveAsync(legacy, CancellationToken.None)).IsSuccess);
        Assert.IsNull((await store.LoadAsync(legacy.JobId, CancellationToken.None)).Value!.ContextHash);

        var contextHash = "sha256:" + new string('b', 64);
        var receipt = new ReviewAutomationReceipt(
            ReviewAutomationPolicy.ContextualAgentCreateNew, Guid.NewGuid(), "sha256:" + new string('c', 64),
            contextHash, "sha256:" + new string('d', 64), "sha256:" + new string('e', 64));
        var contextual = Snapshot(Guid.NewGuid(), 0, SegmentState.Approved, null) with
        {
            ContextPolicyVersion = CadSemanticContextBuilder.PolicyVersion,
            ContextHash = contextHash,
            ReviewAutomationReceipt = receipt
        };
        Assert.IsTrue((await store.SaveAsync(contextual, CancellationToken.None)).IsSuccess);
        var tampered = contextual with
        {
            JobId = Guid.NewGuid(),
            ReviewAutomationReceipt = receipt with { ContextHash = "sha256:" + new string('f', 64) }
        };
        Assert.AreEqual("REVIEW_SNAPSHOT_INVALID",
            (await store.SaveAsync(tampered, CancellationToken.None)).Error!.Code);
        var unknownPolicy = Snapshot(Guid.NewGuid(), 0, SegmentState.Proposed, null) with
        {
            ContextPolicyVersion = "cad-semantic-context/unknown",
            ContextHash = contextHash
        };
        Assert.AreEqual("REVIEW_SNAPSHOT_INVALID",
            (await store.SaveAsync(unknownPolicy, CancellationToken.None)).Error!.Code);
    }

    [TestMethod]
    public async Task ConcurrentCompareAndSwapAllowsExactlyOneReviewWinner()
    {
        var store = new LocalTranslationReviewStore(new WorkspacePaths(_root));
        var jobId = Guid.NewGuid();
        Assert.IsTrue((await store.SaveAsync(Snapshot(jobId, 0, SegmentState.Proposed, null), CancellationToken.None)).IsSuccess);
        var first = Snapshot(jobId, 1, SegmentState.Approved, null);
        var second = first with
        {
            Rows = [first.Rows[0] with { ProposedText = "BOMBA-2 {TAG}", FinalText = "BOMBA-2 {TAG}" }]
        };

        var results = await Task.WhenAll(
            store.SaveAsync(first, expectedVersion: 0, CancellationToken.None),
            store.SaveAsync(second, expectedVersion: 0, CancellationToken.None));

        Assert.AreEqual(1, results.Count(result => result.IsSuccess));
        Assert.AreEqual(1, results.Count(result => result.Error?.Code == "REVIEW_VERSION_CONFLICT"));
        var loaded = await store.LoadAsync(jobId, CancellationToken.None);
        Assert.AreEqual(results.Single(result => result.IsSuccess).Value!.Rows[0].FinalText, loaded.Value!.Rows[0].FinalText);
        Assert.AreEqual(1L, loaded.Value.Version);
    }

    private static TranslationReviewSnapshot Snapshot(Guid jobId, long version, SegmentState state, string? reason) => new(
        jobId, version, "es-MX", "translate-cad-text/1.0", 1, 1, DateTimeOffset.Parse("2026-08-12T22:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
        [new ReviewRowSnapshot("seg_sha256_" + new string('a', 64), "PUMP {TAG}", "BOMBA {TAG}", "BOMBA {TAG}", state, reason, null)]);
}
