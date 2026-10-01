using System.Text.Json;
using System.Text.Json.Nodes;
using DwgTranslator.Contracts;

namespace DwgTranslator.Application;

public sealed class CadInvariantDiffWorkflow
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ICadGatewaySessionFactory _sessions;
    private readonly IClock _clock;

    public CadInvariantDiffWorkflow(ICadGatewaySessionFactory sessions, IClock clock)
    {
        _sessions = sessions;
        _clock = clock;
    }

    public Task<Result<CadInvariantDiffResponsePayload>> CompareAsync(
        Guid operationId, string sourcePath, string sourceHash, string candidatePath, string candidateHash,
        CancellationToken cancellationToken) =>
        CadSessionRunner.RunAsync(_sessions, CadSessionPurpose.ReadOnly,
            gateway => ExecuteAsync(gateway, operationId, sourcePath, sourceHash, candidatePath, candidateHash, cancellationToken),
            cancellationToken);

    private async Task<Result<CadInvariantDiffResponsePayload>> ExecuteAsync(
        ICadGateway gateway, Guid operationId, string sourcePath, string sourceHash, string candidatePath, string candidateHash,
        CancellationToken cancellationToken)
    {
        var capabilities = await gateway.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        if (!capabilities.IsSuccess) return Results.Failure<CadInvariantDiffResponsePayload>(capabilities.Error!);
        if (!string.Equals(capabilities.Value!.ProtocolVersion, ContractV1.SchemaVersion, StringComparison.Ordinal) ||
            !capabilities.Value.Operations.Contains(CadOperations.InvariantDiff, StringComparer.Ordinal))
            return Failure("CAD_INVARIANT_DIFF_UNSUPPORTED", "The configured read-only AutoCAD adapter does not support invariant differential diagnostics.");

        var payload = new CadInvariantDiffRequestPayload
        {
            SourcePath = sourcePath,
            ExpectedSourceHash = sourceHash,
            CandidatePath = candidatePath,
            ExpectedCandidateHash = candidateHash,
            ReadOnly = true
        };
        var node = JsonSerializer.SerializeToNode(payload, Json)?.AsObject();
        if (node is null) return Failure("CAD_INVARIANT_DIFF_PAYLOAD_INVALID", "The invariant differential request could not be encoded.");
        var key = IdempotencyMaterial.Fingerprint(MessageTypes.InvariantDiffRequest, ContractV1.SchemaVersion,
            new Dictionary<string, string>(), new JsonObject
            {
                ["jobId"] = operationId.ToString("D"),
                ["payload"] = node.DeepClone()
            });
        if (!key.IsSuccess) return Results.Failure<CadInvariantDiffResponsePayload>(key.Error!);
        var response = await gateway.ExchangeAsync(WireEnvelope.Request(MessageTypes.InvariantDiffRequest, operationId,
            Guid.NewGuid(), key.Value!, _clock.UtcNow, node), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess) return Results.Failure<CadInvariantDiffResponsePayload>(response.Error!);
        if (response.Value!.Status == OperationStatus.Failed && response.Value.Error is not null)
            return Results.Failure<CadInvariantDiffResponsePayload>(response.Value.Error);
        var decoded = EnvelopeCodec.DecodePayload<CadInvariantDiffResponsePayload>(response.Value!, MessageTypes.InvariantDiffResponse);
        if (!decoded.IsSuccess) return decoded;
        return CadInvariantDiffCompactPolicy.Validate(decoded.Value, sourceHash, candidateHash);
    }

    private static Result<CadInvariantDiffResponsePayload> Failure(string code, string message) =>
        Results.Failure<CadInvariantDiffResponsePayload>(new ContractError(code, ErrorCategory.Environment, message, false));
}
