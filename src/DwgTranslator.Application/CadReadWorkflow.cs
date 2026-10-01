using System.Text.Json.Nodes;
using DwgTranslator.Contracts;

namespace DwgTranslator.Application;

public sealed class CadReadWorkflow
{
    private readonly ICadGatewaySessionFactory _sessions;
    private readonly IClock _clock;
    private readonly Func<string, TimeSpan>? _exchangeTimeoutForSource;

    public CadReadWorkflow(ICadGateway cad, IClock clock)
        : this(new FixedCadGatewaySessionFactory(cad), clock)
    {
    }

    public CadReadWorkflow(
        ICadGatewaySessionFactory sessions,
        IClock clock,
        Func<string, TimeSpan>? exchangeTimeoutForSource = null)
    {
        _sessions = sessions;
        _clock = clock;
        _exchangeTimeoutForSource = exchangeTimeoutForSource;
    }

    public async Task<Result<CadReadResult>> InspectAndExtractAsync(
        Guid jobId,
        string sourcePath,
        string expectedSourceHash,
        CancellationToken cancellationToken)
    {
        if (jobId == Guid.Empty || string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrEmpty(expectedSourceHash) ||
            !ContractPatterns.Sha256().IsMatch(expectedSourceHash))
            return Failure("CAD_READ_INPUT_INVALID", ErrorCategory.Input, "A job, source path and source hash are required.");

        TimeSpan? exchangeTimeout = null;
        if (_exchangeTimeoutForSource is not null)
        {
            try
            {
                exchangeTimeout = _exchangeTimeoutForSource(sourcePath);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
            {
                return Failure("CAD_SESSION_TIMEOUT_POLICY_INVALID", ErrorCategory.Configuration,
                    "The bounded CAD exchange deadline could not be derived from the immutable source snapshot.");
            }

            if (exchangeTimeout <= TimeSpan.Zero)
                return Failure("CAD_SESSION_TIMEOUT_POLICY_INVALID", ErrorCategory.Configuration,
                    "The bounded CAD exchange deadline must be positive.");
        }

        return await CadSessionRunner.RunAsync(
            _sessions,
            CadSessionPurpose.ReadOnly,
            cad => ExecuteAsync(cad, jobId, sourcePath, expectedSourceHash, cancellationToken),
            cancellationToken,
            exchangeTimeout);
    }

    private async Task<Result<CadReadResult>> ExecuteAsync(
        ICadGateway cad,
        Guid jobId,
        string sourcePath,
        string expectedSourceHash,
        CancellationToken cancellationToken)
    {
        var capabilities = await cad.GetCapabilitiesAsync(cancellationToken);
        if (!capabilities.IsSuccess) return Results.Failure<CadReadResult>(capabilities.Error!);
        if (!string.Equals(capabilities.Value!.ProtocolVersion, ContractV1.SchemaVersion, StringComparison.Ordinal) ||
            !capabilities.Value.Operations.Contains(CadOperations.Inspect, StringComparer.Ordinal) ||
            !capabilities.Value.Operations.Contains(CadOperations.Extract, StringComparer.Ordinal))
            return Failure("CAD_CAPABILITIES_INCOMPATIBLE", ErrorCategory.Environment, "The CAD host does not support the required v1 read operations.");

        var inspectPayload = new JsonObject
        {
            ["sourcePath"] = sourcePath,
            ["expectedSourceHash"] = expectedSourceHash,
            ["readOnly"] = true,
            ["includeSpaces"] = new JsonArray("ModelSpace", "PaperSpace", "BlockDefinitions"),
            ["supportedEntityTypes"] = new JsonArray("TEXT", "MTEXT"),
            ["classifyFields"] = true,
            ["followExternalReferences"] = false
        };
        var inspectRequest = CreateRequest(MessageTypes.InspectRequest, jobId, expectedSourceHash, inspectPayload);
        if (!inspectRequest.IsSuccess) return Results.Failure<CadReadResult>(inspectRequest.Error!);
        var inspectResponse = await cad.ExchangeAsync(inspectRequest.Value!, cancellationToken);
        if (!inspectResponse.IsSuccess) return Results.Failure<CadReadResult>(inspectResponse.Error!);
        var inspection = CadInspectionResultPolicy.Validate(inspectResponse.Value!, expectedSourceHash);
        if (!inspection.IsSuccess) return Results.Failure<CadReadResult>(inspection.Error!);

        var extractPayload = new JsonObject
        {
            ["sourcePath"] = sourcePath,
            ["expectedSourceHash"] = expectedSourceHash,
            ["entityTypes"] = new JsonArray("TEXT", "MTEXT"),
            ["excludeFieldBackedText"] = true
        };
        var extractRequest = CreateRequest(MessageTypes.ExtractRequest, jobId, expectedSourceHash, extractPayload);
        if (!extractRequest.IsSuccess) return Results.Failure<CadReadResult>(extractRequest.Error!);
        var extractResponse = await cad.ExchangeAsync(extractRequest.Value!, cancellationToken);
        if (!extractResponse.IsSuccess) return Results.Failure<CadReadResult>(extractResponse.Error!);
        var segments = CadExtractionResultPolicy.Validate(extractResponse.Value!, expectedSourceHash,
            inspection.Value!.DrawingFingerprint, Path.GetFileName(sourcePath));
        if (!segments.IsSuccess) return Results.Failure<CadReadResult>(segments.Error!);
        var decoded = EnvelopeCodec.DecodePayload<CadExtractResponsePayload>(extractResponse.Value!, MessageTypes.ExtractResponse);
        if (!decoded.IsSuccess) return Results.Failure<CadReadResult>(decoded.Error!);

        return Results.Success(new CadReadResult(inspection.Value, segments.Value!, decoded.Value!.ExcludedFieldCount));
    }

    private Result<WireEnvelope> CreateRequest(string messageType, Guid jobId, string sourceHash, JsonObject payload)
    {
        var idempotency = IdempotencyMaterial.Fingerprint(
            messageType,
            ContractV1.SchemaVersion,
            new Dictionary<string, string>(),
            new JsonObject { ["jobId"] = jobId.ToString("D"), ["payload"] = payload.DeepClone() });
        return idempotency.IsSuccess
            ? Results.Success(WireEnvelope.Request(messageType, jobId, Guid.NewGuid(), idempotency.Value!, _clock.UtcNow, payload))
            : Results.Failure<WireEnvelope>(idempotency.Error!);
    }

    private static Result<CadReadResult> Failure(string code, ErrorCategory category, string message) =>
        Results.Failure<CadReadResult>(new ContractError(code, category, message, false));
}
