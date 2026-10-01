using System.Text.Json;
using Autodesk.AutoCAD.Runtime;
using DwgTranslator.Contracts;

namespace DwgTranslator.AutoCAD.Adapter;

internal static class CadRequestHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] SupportedOperations = [CadOperations.Inspect, CadOperations.Extract, CadOperations.InvariantDiff];
    public static async Task<Result<WireEnvelope>> HandleAsync(WireEnvelope request, CancellationToken cancellationToken)
    {
        try
        {
            if (request.MessageType == MessageTypes.CapabilitiesRequest)
                return Success(request, MessageTypes.CapabilitiesResponse, new { protocolVersion = ContractV1.SchemaVersion, hostVersion = "AutoCAD 2026 (25.1.179.0)", operations = SupportedOperations });
            // Inspect/extract/invariant-diff open their own Database with shared read access.
            // They must not depend on an interactive document command context: the plug-in pipe
            // listener is hosted outside an AutoCAD command when launched through /b NETLOAD.
            var handled = request.MessageType switch
            {
                MessageTypes.InspectRequest => CadReadService.Inspect(request, cancellationToken),
                MessageTypes.ExtractRequest => CadReadService.Extract(request, cancellationToken),
                MessageTypes.InvariantDiffRequest => CadInvariantDiffService.Compare(request, cancellationToken),
                _ => Results.Failure<object>(new ContractError("IPC_OPERATION_UNSUPPORTED", ErrorCategory.Contract, "The adapter supports capabilities, inspect and extract only.", false))
            };
            if (!handled.IsSuccess) return Results.Failure<WireEnvelope>(handled.Error!);
            var responseType = request.MessageType switch
            {
                MessageTypes.InspectRequest => MessageTypes.InspectResponse,
                MessageTypes.ExtractRequest => MessageTypes.ExtractResponse,
                _ => MessageTypes.InvariantDiffResponse
            };
            return Success(request, responseType, handled.Value!);
        }
        catch (OperationCanceledException) { return Failure("CANCELLED", ErrorCategory.Environment, "The read-only CAD operation was cancelled at a safe entity boundary.", false); }
        catch (Autodesk.AutoCAD.Runtime.Exception exception) { return Results.Failure<WireEnvelope>(CadErrorPolicy.FromAutoCAD(exception)); }
        catch (IOException) { return Failure("DOCUMENT_LOCKED", ErrorCategory.Concurrency, "The drawing could not be opened for shared read access.", false); }
        catch (UnauthorizedAccessException) { return Failure("CAD_SOURCE_ACCESS_DENIED", ErrorCategory.Environment, "The drawing is not readable by the current user.", false); }
        catch (System.Exception) { return Failure("INTERNAL_ERROR", ErrorCategory.Internal, "The read-only adapter failed without exposing drawing content or paths.", false); }
    }

    private static Result<WireEnvelope> Success(WireEnvelope request, string responseType, object payload)
    {
        var node = JsonSerializer.SerializeToNode(payload, JsonOptions)?.AsObject();
        return node is null ? Failure("CAD_PAYLOAD_SERIALIZATION_FAILED", ErrorCategory.Internal, "The CAD payload could not be serialized.", false) : Results.Success(new WireEnvelope { SchemaVersion = ContractV1.SchemaVersion, MessageType = responseType, JobId = request.JobId, CorrelationId = request.CorrelationId, IdempotencyKey = request.IdempotencyKey, SentAtUtc = DateTimeOffset.UtcNow, Status = OperationStatus.Succeeded, Payload = node });
    }
    private static Result<WireEnvelope> Failure(string code, ErrorCategory category, string message, bool retryable) => Results.Failure<WireEnvelope>(new ContractError(code, category, message, retryable));
}

internal static class CadErrorPolicy
{
    public static ContractError FromAutoCAD(Autodesk.AutoCAD.Runtime.Exception exception)
    {
        var status = exception.ErrorStatus.ToString();
        var nativeStatus = CadNativeErrorStatuses.IsSupported(status) ? status : null;
        return exception.ErrorStatus switch
        {
            ErrorStatus.FilerError or ErrorStatus.DwgNeedsRecovery => new("CORRUPT_ENTITY", ErrorCategory.Integrity,
                "AutoCAD rejected the drawing as invalid or corrupt.", false, NativeErrorStatus: nativeStatus),
            ErrorStatus.FileAccessErr => new("DOCUMENT_LOCKED", ErrorCategory.Concurrency,
                "AutoCAD could not open the drawing for shared read access.", false, NativeErrorStatus: nativeStatus),
            ErrorStatus.NoDocument => new("CAD_SESSION_UNAVAILABLE", ErrorCategory.Environment,
                "No AutoCAD command context is available.", false, NativeErrorStatus: nativeStatus),
            _ => new("CAD_UNAVAILABLE", ErrorCategory.Environment,
                "AutoCAD read operation failed with an allowlisted native status when available.", false,
                NativeErrorStatus: nativeStatus)
        };
    }
}
