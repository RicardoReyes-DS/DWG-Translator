using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DwgTranslator.Contracts;

public sealed record WireEnvelope
{
    public required string SchemaVersion { get; init; }
    public required string MessageType { get; init; }
    public required Guid JobId { get; init; }
    public required Guid CorrelationId { get; init; }
    public required string IdempotencyKey { get; init; }
    public required DateTimeOffset SentAtUtc { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OperationStatus? Status { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonObject? Payload { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ContractError? Error { get; init; }

    public static WireEnvelope Request(
        string messageType,
        Guid jobId,
        Guid correlationId,
        string idempotencyKey,
        DateTimeOffset sentAtUtc,
        JsonObject payload) => new()
        {
            SchemaVersion = ContractV1.SchemaVersion,
            MessageType = messageType,
            JobId = jobId,
            CorrelationId = correlationId,
            IdempotencyKey = idempotencyKey,
            SentAtUtc = sentAtUtc,
            Payload = payload
        };
}

public static class MessageTypes
{
    public const string CapabilitiesRequest = "cad.capabilities.get.request";
    public const string CapabilitiesResponse = "cad.capabilities.get.response";
    public const string InspectRequest = "cad.job.inspect.request";
    public const string InspectResponse = "cad.job.inspect.response";
    public const string ExtractRequest = "cad.job.extract.request";
    public const string ExtractResponse = "cad.job.extract.response";
    public const string InvariantDiffRequest = "cad.job.invariant-diff.request";
    public const string InvariantDiffResponse = "cad.job.invariant-diff.response";
    public const string WriteRequest = "cad.job.write.request";
    public const string WriteResponse = "cad.job.write.response";
    public const string ReconcileRequest = "cad.job.reconcile.request";
    public const string ReconcileResponse = "cad.job.reconcile.response";
    public const string ValidateRequest = "cad.job.validate.request";
    public const string ValidateResponse = "cad.job.validate.response";
    public const string StatusRequest = "cad.job.status.request";
    public const string StatusResponse = "cad.job.status.response";
    public const string CancelRequest = "cad.job.cancel.request";
    public const string CancelResponse = "cad.job.cancel.response";
    public const string ProgressEvent = "cad.event.progress";
    public const string TranslationRequest = "translation.batch.request";
    public const string TranslationResponse = "translation.batch.response";
    public const string ReviewRecorded = "review.decision.recorded";

    private static readonly HashSet<string> Supported = new(StringComparer.Ordinal)
    {
        CapabilitiesRequest, CapabilitiesResponse, InspectRequest, InspectResponse, ExtractRequest, ExtractResponse, InvariantDiffRequest, InvariantDiffResponse,
        WriteRequest, WriteResponse, ReconcileRequest, ReconcileResponse, ValidateRequest, ValidateResponse, StatusRequest, StatusResponse,
        CancelRequest, CancelResponse, ProgressEvent, TranslationRequest, TranslationResponse, ReviewRecorded
    };

    public static bool IsSupported(string? value) => value is not null && Supported.Contains(value);
}

public static class CadOperations
{
    public const string Inspect = "cad.job.inspect";
    public const string Extract = "cad.job.extract";
    public const string InvariantDiff = "cad.job.invariant-diff";
    public const string Write = "cad.job.write";
    public const string Reconcile = "cad.job.reconcile-read-only";
}
