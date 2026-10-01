using System.Text.Json;
using DwgTranslator.Application;
using DwgTranslator.Contracts;
using DwgTranslator.Domain;

namespace DwgTranslator.Core.Tests;

[TestClass]
public sealed class CadSessionCleanupEvidenceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse(
        "2026-09-02T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    [TestMethod]
    public async Task ValidCadIntegrityFailureSurvivesSubsequentExitRequiredCleanup()
    {
        var adapterFailure = new ContractError(
            "GEOMETRY_INVARIANTS_CHANGED",
            ErrorCategory.Integrity,
            "Strict invariant comparison failed.",
            false,
            InvariantDiagnostics: Diagnostics());
        var workflow = new CadWriteWorkflow(
            new Sessions(Results.Failure<WireEnvelope>(adapterFailure), ExitRequired()),
            new Clock());

        var result = await WriteAsync(workflow);

        Assert.AreEqual("GEOMETRY_INVARIANTS_CHANGED", result.Error?.Code);
        Assert.AreSame(adapterFailure.InvariantDiagnostics, result.Error?.InvariantDiagnostics);
    }

    [TestMethod]
    public async Task TransportFailureDoesNotHideExitRequiredCleanup()
    {
        var transportFailure = new ContractError(
            "IPC_PEER_DISCONNECTED", ErrorCategory.Transport, "No response.", true);
        var workflow = new CadWriteWorkflow(
            new Sessions(Results.Failure<WireEnvelope>(transportFailure), ExitRequired()),
            new Clock());

        var result = await WriteAsync(workflow);

        Assert.AreEqual("CAD_PROCESS_EXIT_REQUIRED", result.Error?.Code);
        Assert.AreEqual(ErrorCategory.Environment, result.Error?.Category);
    }

    private static Task<Result<CadWriteResponsePayload>> WriteAsync(CadWriteWorkflow workflow)
    {
        var jobId = Guid.NewGuid();
        var segment = new CadTextSegment
        {
            SegmentId = "seg_sha256_" + new string('a', 64),
            Entity = new CadEntityReference
            {
                Type = "TEXT",
                Handle = "A1",
                Space = "ModelSpace",
                Layout = null,
                Layer = "NOTES",
                BlockPath = [],
                SubIndex = 0
            },
            SourceText = "PUMP",
            SourceTextHash = Hash('a'),
            LineBreakStyle = "None",
            ProtectedTokens = [],
            FieldClassification = "None",
            State = "Extracted"
        };
        var review = new TranslationReviewSnapshot(
            jobId, 1, "en-US", "translate-cad-text/1.0", 1, 1, Now,
            [new ReviewRowSnapshot(segment.SegmentId, segment.SourceText, "POMPE", "POMPE",
                SegmentState.Approved, null, null)]);
        return workflow.WriteAsync(jobId, "C:\\Input\\source.dwg", "C:\\Output\\translated.dwg",
            Hash('b'), [segment], review, CancellationToken.None);
    }

    private static ContractError ExitRequired() => new(
        "CAD_PROCESS_EXIT_REQUIRED", ErrorCategory.Environment,
        "The exact child still requires termination approval.", false);

    private static CadInvariantDiagnostics Diagnostics()
    {
        var before = new CadInvariantDiagnosticRow
        {
            EntityHandle = "B1",
            OwnerHandle = "10",
            DxfType = "LINE",
            RuntimeClass = "Line",
            OwnerBlockName = "*Model_Space",
            OwnerClass = "BLOCK_RECORD",
            IsTargetText = false,
            IsAnonymousDimensionBlockName = false,
            ReferencedByDimensionCount = 0,
            ReferencedByNonDimensionCount = 0,
            DerivedDimensionGraphicsCandidate = false,
            Layer = "NOTES",
            ColorIndex = 7,
            LinetypeHandle = "12",
            Lineweight = 25,
            Extents = new CadInvariantExtents { Minimum = "0,0,0", Maximum = "1,1,0" },
            InvariantRowFingerprint = Hash('c')
        };
        return new CadInvariantDiagnostics
        {
            Schema = CadInvariantDiagnosticsPolicy.Schema,
            AddedCount = 0,
            RemovedCount = 0,
            ChangedCount = 1,
            Truncated = false,
            Rows =
            [
                new CadInvariantDifference
                {
                    InvariantKey = "10|B1", ChangeKind = "Changed",
                    FieldsChanged = ["extents", "invariantRowFingerprint"],
                    Before = before,
                    After = before with
                    {
                        Extents = new CadInvariantExtents { Minimum = "0,0,0", Maximum = "2,1,0" },
                        InvariantRowFingerprint = Hash('d')
                    }
                }
            ]
        };
    }

    private static string Hash(char value) => "sha256:" + new string(value, 64);

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class Sessions(Result<WireEnvelope> exchange, ContractError closeError)
        : ICadGatewaySessionFactory
    {
        public Task<Result<ICadGatewayLease>> OpenAsync(
            CadSessionPurpose purpose, CancellationToken cancellationToken) =>
            Task.FromResult(Results.Success<ICadGatewayLease>(new Lease(exchange, closeError)));

        public Task<Result<ICadGatewayLease>> OpenAsync(
            CadSessionPurpose purpose, TimeSpan exchangeTimeout, CancellationToken cancellationToken) =>
            OpenAsync(purpose, cancellationToken);
    }

    private sealed class Lease(Result<WireEnvelope> exchange, ContractError closeError) : ICadGatewayLease
    {
        public ICadGateway Gateway { get; } = new Gateway(exchange);

        public Task<Result<bool>> CloseAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Results.Failure<bool>(closeError));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Gateway(Result<WireEnvelope> exchange) : ICadGateway
    {
        public Task<Result<CadCapabilities>> GetCapabilitiesAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<WireEnvelope>> ExchangeAsync(
            WireEnvelope request, CancellationToken cancellationToken) => Task.FromResult(exchange);
    }
}
