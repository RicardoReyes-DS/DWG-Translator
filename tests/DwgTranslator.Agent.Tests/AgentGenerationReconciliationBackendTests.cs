using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DwgTranslator.Agent;
using DwgTranslator.Application;
using DwgTranslator.Contracts;
using DwgTranslator.Domain;
using DwgTranslator.Infrastructure.Local;

namespace DwgTranslator.Agent.Tests;

[TestClass]
public sealed class AgentGenerationReconciliationBackendTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    private string _root = null!;

    [TestInitialize]
    public void Initialize() => _root = Path.Combine(Path.GetTempPath(), "dwg-agent-generation-reconcile", Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [TestMethod]
    public async Task UnsealedReconciliationRequestDoesNotReturnFailedGenerationToApproved()
    {
        var paths = new WorkspacePaths(_root);
        var jobs = new LocalJobStore(paths);
        var reviews = new LocalTranslationReviewStore(paths);
        var jobId = Guid.NewGuid();
        var sourceHash = "sha256:" + new string('a', 64);
        var segment = new CadTextSegment
        {
            SegmentId = "seg_sha256_" + new string('b', 64),
            Entity = new CadEntityReference { Type = "MTEXT", Handle = "10", Space = "ModelSpace", Layout = null, Layer = "0", BlockPath = [], SubIndex = 0 },
            SourceText = "REDACTED",
            SourceTextHash = "sha256:" + new string('c', 64),
            LineBreakStyle = "None",
            ProtectedTokens = [],
            FieldClassification = "PlainText",
            State = "Extracted"
        };
        var data = new DwgTranslationJobData(new("C:\\Input\\source.dwg", "C:\\Output\\source-ENG.dwg", sourceHash, null, "en-US", "prompt", null),
            "sha256:" + new string('d', 64), null, [segment], null);
        var initial = new JobDocument(jobId, JobState.Draft, 0, DateTimeOffset.UtcNow, JsonSerializer.SerializeToNode(data, Json)!.AsObject());
        Assert.IsTrue((await jobs.CreateAsync(initial, default)).IsSuccess);
        var current = initial;
        for (var version = 1; version <= 4; version++)
        {
            current = current with { State = version == 4 ? JobState.Approved : JobState.ReviewRequired, Version = version, UpdatedAtUtc = DateTimeOffset.UtcNow };
            Assert.IsTrue((await jobs.SaveAsync(current, version - 1, default)).IsSuccess);
        }
        Assert.IsTrue((await jobs.SaveCheckpointAsync(new(jobId, JobState.Approved, 4, DateTimeOffset.UtcNow, sourceHash,
            data.ConfigurationHash, ContractV1.SchemaVersion, new JsonObject { ["state"] = "Approved", ["jobVersion"] = 4 }), default)).IsSuccess);
        for (var version = 5; version <= 7; version++)
        {
            var node = current.Data.DeepClone().AsObject();
            if (version == 7) node["failure"] = new JsonObject { ["code"] = "IPC_TIMEOUT", ["retryable"] = true, ["stage"] = "Writing" };
            current = current with { State = version == 7 ? JobState.Failed : JobState.Writing, Version = version, UpdatedAtUtc = DateTimeOffset.UtcNow, Data = node };
            Assert.IsTrue((await jobs.SaveAsync(current, version - 1, default)).IsSuccess);
        }
        var review = new TranslationReviewSnapshot(jobId, 0, "en-US", "prompt", 1, 1, DateTimeOffset.UtcNow,
            [new(segment.SegmentId, segment.SourceText, "PROPOSAL", "PROPOSAL", SegmentState.Approved, null, null)]);
        Assert.IsTrue((await reviews.SaveAsync(review, default)).IsSuccess);
        var backend = new AgentTranslationWorkflowBackend(null!, jobs, reviews, new SystemClock(), _root);

        var reconciled = await backend.ReconcileFailedGenerationToApprovedAsync(jobId, 7,
            UnsealedAuthority(0, 1), default);

        Assert.IsFalse(reconciled.IsSuccess);
        StringAssert.StartsWith(reconciled.Error!.Code, "GENERATION_RECONCILIATION_");
    }

    [TestMethod]
    public async Task MissingCheckpointOrIncompleteReviewFailsClosed()
    {
        var paths = new WorkspacePaths(_root);
        var jobs = new LocalJobStore(paths);
        var reviews = new LocalTranslationReviewStore(paths);
        var jobId = Guid.NewGuid();
        var data = new DwgTranslationJobData(new("C:\\Input\\source.dwg", "C:\\Output\\source-ENG.dwg", "sha256:" + new string('a', 64), null, "en-US", "prompt", null),
            "sha256:" + new string('d', 64), null, [], null);
        var failedData = JsonSerializer.SerializeToNode(data, Json)!.AsObject();
        failedData["failure"] = new JsonObject { ["code"] = "IPC_TIMEOUT", ["retryable"] = true };
        var failed = new JobDocument(jobId, JobState.Failed, 0, DateTimeOffset.UtcNow, failedData);
        Assert.IsTrue((await jobs.CreateAsync(failed, default)).IsSuccess);
        var backend = new AgentTranslationWorkflowBackend(null!, jobs, reviews, new SystemClock(), _root);

        var result = await backend.ReconcileFailedGenerationToApprovedAsync(jobId, 0,
            UnsealedAuthority(1, 0), default);

        Assert.IsFalse(result.IsSuccess);
        StringAssert.StartsWith(result.Error!.Code, "GENERATION_RECONCILIATION_");
    }

    private static AgentGenerationReconciliationAuthority UnsealedAuthority(long reviewVersion, int decisionCount) =>
        new("sha256:" + new string('a', 64), "IPC_TIMEOUT", ErrorCategory.Transport, true,
            4, "sha256:" + new string('b', 64), reviewVersion,
            "sha256:" + new string('c', 64), decisionCount,
            "sha256:" + new string('d', 64));
}
