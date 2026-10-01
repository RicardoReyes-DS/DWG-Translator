using System.Text.Json;
using System.Text.Json.Serialization;

namespace DwgTranslator.Contracts;

public static class EnvelopeCodec
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static byte[] Encode(WireEnvelope envelope)
    {
        var validation = Validate(envelope);
        if (!validation.IsSuccess)
        {
            throw new InvalidDataException(validation.Error!.Code);
        }

        return JsonSerializer.SerializeToUtf8Bytes(envelope, Options);
    }

    public static Result<WireEnvelope> Decode(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<WireEnvelope>(utf8Json, Options);
            return envelope is null
                ? Fail("IPC_ENVELOPE_NULL", "Envelope is null.")
                : Validate(envelope);
        }
        catch (JsonException)
        {
            return Fail("IPC_JSON_INVALID", "Envelope is not valid JSON for contract v1.");
        }
    }

    public static Result<WireEnvelope> Validate(WireEnvelope envelope)
    {
        if (envelope is null)
            return Fail("IPC_ENVELOPE_NULL", "Envelope is null.");
        if (!string.Equals(envelope.SchemaVersion, ContractV1.SchemaVersion, StringComparison.Ordinal))
            return Fail("IPC_SCHEMA_UNSUPPORTED", "schemaVersion is not supported.");
        if (string.IsNullOrEmpty(envelope.MessageType) || !ContractPatterns.MessageType().IsMatch(envelope.MessageType) || !MessageTypes.IsSupported(envelope.MessageType))
            return Fail("IPC_OPERATION_UNSUPPORTED", "messageType is invalid or unsupported.");
        if (envelope.JobId == Guid.Empty)
            return Fail("IPC_JOB_ID_MISSING", "jobId is required.");
        if (envelope.CorrelationId == Guid.Empty)
            return Fail("IPC_CORRELATION_MISSING", "correlationId is required.");
        if (string.IsNullOrEmpty(envelope.IdempotencyKey) || !ContractPatterns.Sha256().IsMatch(envelope.IdempotencyKey))
            return Fail("IPC_IDEMPOTENCY_KEY_INVALID", "idempotencyKey must be a lowercase SHA-256 value.");
        if (envelope.SentAtUtc.Offset != TimeSpan.Zero)
            return Fail("IPC_TIMESTAMP_INVALID", "sentAtUtc must be UTC.");

        var isRequest = envelope.Status is null;
        if (isRequest && (envelope.Payload is null || envelope.Error is not null))
            return Fail("IPC_REQUEST_SHAPE_INVALID", "A request requires payload and cannot contain status or error.");
        if (!isRequest && envelope.Status == OperationStatus.Failed && (envelope.Error is null || envelope.Payload is not null))
            return Fail("IPC_ERROR_SHAPE_INVALID", "A failed response requires error and cannot contain payload.");
        if (!isRequest && envelope.Status != OperationStatus.Failed && (envelope.Payload is null || envelope.Error is not null))
            return Fail("IPC_RESPONSE_SHAPE_INVALID", "A non-failed response requires payload and cannot contain error.");
        if (envelope.Error is { } error &&
            (string.IsNullOrWhiteSpace(error.Code) || string.IsNullOrWhiteSpace(error.Message) || error.Message.Length > ContractV1.MaxErrorMessageLength))
            return Fail("IPC_ERROR_INVALID", "The typed error is invalid.");
        if (envelope.Error?.InvariantDiagnostics is { } diagnostics && !CadInvariantDiagnosticsLimits.IsValid(diagnostics))
            return Fail("IPC_ERROR_INVALID", "Invariant diagnostics exceed the bounded safe contract.");
        if (envelope.Error?.TechnicalStage is { } technicalStage && !CadWriteTechnicalStages.IsSupported(technicalStage))
            return Fail("IPC_ERROR_INVALID", "The CAD technical stage is not recognized.");
        if (envelope.Error?.NativeErrorStatus is { } nativeStatus && !CadNativeErrorStatuses.IsSupported(nativeStatus))
            return Fail("IPC_ERROR_INVALID", "The native CAD error status is invalid.");

        return Results.Success(envelope);
    }

    public static Result<T> DecodePayload<T>(WireEnvelope envelope, string expectedMessageType)
        where T : class
    {
        if (envelope is null)
            return Fail<T>("IPC_ENVELOPE_NULL", "Envelope is null.");
        var validation = Validate(envelope);
        if (!validation.IsSuccess)
            return Results.Failure<T>(validation.Error!);
        if (!string.Equals(envelope.MessageType, expectedMessageType, StringComparison.Ordinal))
            return Fail<T>("IPC_PAYLOAD_MESSAGE_TYPE_INVALID", "Envelope messageType does not match the requested payload contract.");

        try
        {
            var payload = envelope.Payload?.Deserialize<T>(Options);
            return payload is null
                ? Fail<T>("IPC_PAYLOAD_NULL", "Envelope payload is null.")
                : Results.Success(payload);
        }
        catch (JsonException)
        {
            return Fail<T>("IPC_PAYLOAD_INVALID", "Payload is not valid for the requested contract.");
        }
    }

    private static Result<WireEnvelope> Fail(string code, string message) =>
        Results.Failure<WireEnvelope>(new ContractError(code, ErrorCategory.Contract, message, false));

    private static Result<T> Fail<T>(string code, string message) =>
        Results.Failure<T>(new ContractError(code, ErrorCategory.Contract, message, false));
}
