using System.Text.Json;
using Autodesk.AutoCAD.Runtime;
using DwgTranslator.Contracts;
using DwgTranslator.Transport.Core;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace DwgTranslator.AutoCAD.Write;

internal static class CadWriteRequestHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] SupportedOperations = [CadOperations.Write, CadOperations.Reconcile];

    public static async Task<Result<WireEnvelope>> HandleAsync(WireEnvelope request, CancellationToken cancellationToken)
    {
        string? technicalStage = null;
        try
        {
            if (request.MessageType == MessageTypes.CapabilitiesRequest)
                return Success(request, MessageTypes.CapabilitiesResponse, new { protocolVersion = ContractV1.SchemaVersion, hostVersion = "AutoCAD 2026 (25.1.179.0)", operations = SupportedOperations });
            if (request.MessageType is not (MessageTypes.WriteRequest or MessageTypes.ReconcileRequest))
                return Failure("IPC_OPERATION_UNSUPPORTED", ErrorCategory.Contract, "The adapter supports write and read-only orphan reconciliation only.");
            cancellationToken.ThrowIfCancellationRequested();
            TechnicalCheckpoint.Emit("cad-dispatch-start");
            var result = await CallbackDispatch.CompleteFromCallbackAsync(
                callback => AcApplication.DocumentManager.ExecuteInApplicationContext(_ => callback(), null),
                () =>
                {
                    TechnicalCheckpoint.Emit("cad-callback-enter");
                    try
                    {
                        if (request.MessageType == MessageTypes.ReconcileRequest)
                        {
                            var reconciled = CadWriteService.Reconcile(request, cancellationToken);
                            TechnicalCheckpoint.Emit("cad-service-complete");
                            var response = reconciled.IsSuccess
                                ? Success(request, MessageTypes.ReconcileResponse, reconciled.Value!)
                                : Results.Failure<WireEnvelope>(reconciled.Error!);
                            TechnicalCheckpoint.Emit("cad-response-serialized");
                            return response;
                        }
                        var written = CadWriteService.Execute(request, cancellationToken, stage =>
                        {
                            technicalStage = stage;
                            TechnicalCheckpoint.Emit(stage);
                        });
                        TechnicalCheckpoint.Emit("cad-service-complete");
                        var writeResponse = written.IsSuccess
                            ? Success(request, MessageTypes.WriteResponse, written.Value!)
                            : Results.Failure<WireEnvelope>(written.Error!);
                        TechnicalCheckpoint.Emit("cad-response-serialized");
                        return writeResponse;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception exception) { return Results.Failure<WireEnvelope>(CadWriteErrorPolicy.FromAutoCAD(exception, technicalStage)); }
                    catch (InvalidDataException exception) { return Results.Failure<WireEnvelope>(CadWriteErrorPolicy.FromIntegrity(exception, technicalStage)); }
                    catch (FormatException) { return Failure("WRITE_HANDLE_INVALID", ErrorCategory.Contract, "An approved entity handle is outside the supported range."); }
                    catch (OverflowException) { return Failure("WRITE_HANDLE_INVALID", ErrorCategory.Contract, "An approved entity handle is outside the supported range."); }
                    catch (IOException) { return Failure("CAD_FILE_IO_FAILED", ErrorCategory.Concurrency, "A source, candidate or destination file operation failed."); }
                    catch (UnauthorizedAccessException) { return Failure("CAD_FILE_ACCESS_DENIED", ErrorCategory.Environment, "The selected paths are not writable by the current user."); }
                }, cancellationToken).ConfigureAwait(false);
            TechnicalCheckpoint.Emit("cad-dispatch-completed-from-callback");
            return result;
        }
        catch (OperationCanceledException) { return Failure("CANCELLED", ErrorCategory.Environment, "The write operation was cancelled at a safe boundary."); }
        catch (Autodesk.AutoCAD.Runtime.Exception exception) { return Results.Failure<WireEnvelope>(CadWriteErrorPolicy.FromAutoCAD(exception, technicalStage)); }
        catch (InvalidDataException exception) { return Results.Failure<WireEnvelope>(CadWriteErrorPolicy.FromIntegrity(exception, technicalStage)); }
        catch (FormatException) { return Failure("WRITE_HANDLE_INVALID", ErrorCategory.Contract, "An approved entity handle is outside the supported range."); }
        catch (OverflowException) { return Failure("WRITE_HANDLE_INVALID", ErrorCategory.Contract, "An approved entity handle is outside the supported range."); }
        catch (IOException) { return Failure("CAD_FILE_IO_FAILED", ErrorCategory.Concurrency, "A source, candidate or destination file operation failed."); }
        catch (UnauthorizedAccessException) { return Failure("CAD_FILE_ACCESS_DENIED", ErrorCategory.Environment, "The selected paths are not writable by the current user."); }
        catch (System.Exception) { return Failure("INTERNAL_ERROR", ErrorCategory.Internal, "The write adapter failed without exposing drawing content or paths."); }
    }

    private static Result<WireEnvelope> Success(WireEnvelope request, string responseType, object payload)
    {
        var node = JsonSerializer.SerializeToNode(payload, Json)?.AsObject();
        return node is null ? Failure("CAD_PAYLOAD_SERIALIZATION_FAILED", ErrorCategory.Internal, "The CAD payload could not be serialized.") : Results.Success(new WireEnvelope
        {
            SchemaVersion = ContractV1.SchemaVersion,
            MessageType = responseType,
            JobId = request.JobId,
            CorrelationId = request.CorrelationId,
            IdempotencyKey = request.IdempotencyKey,
            SentAtUtc = DateTimeOffset.UtcNow,
            Status = OperationStatus.Succeeded,
            Payload = node
        });
    }

    private static Result<WireEnvelope> Failure(string code, ErrorCategory category, string message) =>
        Results.Failure<WireEnvelope>(new ContractError(code, category, message, false));
    private static Result<CadWriteResponsePayload> FailurePayload(string code, ErrorCategory category, string message) =>
        Results.Failure<CadWriteResponsePayload>(new ContractError(code, category, message, false));
}

internal static class CadWriteErrorPolicy
{
    public static ContractError FromIntegrity(InvalidDataException exception, string? technicalStage = null)
    {
        ContractError error = exception.Message switch
    {
        "SOURCE_CHANGED" => new("SOURCE_CHANGED", ErrorCategory.Integrity, "The source changed during candidate preparation.", false),
        "SOURCE_TEXT_CHANGED" => new("SOURCE_TEXT_CHANGED", ErrorCategory.Integrity, "An entity no longer contains the inspected source text.", false),
        "FIELD_BACKED_EXCLUDED" => new("FIELD_BACKED_EXCLUDED", ErrorCategory.Unsupported, "A selected entity became field-backed and cannot be written.", false),
        "WRITE_HANDLE_NOT_FOUND" or "WRITE_HANDLE_NOT_ENTITY" => new("WRITE_MAPPING_TARGET_INVALID", ErrorCategory.Integrity, "An approved mapping no longer resolves to its direct CAD entity.", false),
        "WRITE_ENTITY_TYPE_INVALID" => new("WRITE_ENTITY_TYPE_INVALID", ErrorCategory.Integrity, "An approved mapping no longer resolves to TEXT or MTEXT.", false),
        "WRITE_VISUAL_IDENTITY_MISMATCH" => new("WRITE_VISUAL_IDENTITY_MISMATCH", ErrorCategory.Integrity, "An approved mapping no longer matches its inspected CAD identity.", false),
        "POST_WRITE_TEXT_MISMATCH" => new("POST_WRITE_TEXT_MISMATCH", ErrorCategory.Integrity, "A candidate entity differs from its approved final text.", false),
        "VISUAL_INVARIANTS_CHANGED" => new("VISUAL_INVARIANTS_CHANGED", ErrorCategory.Integrity, "A candidate target changed a strict visual invariant.", false),
        "NORMALIZED_BASELINE_TEXT_CHANGED" => new("NORMALIZED_BASELINE_TEXT_CHANGED", ErrorCategory.Integrity, "The normalized baseline no longer contains the inspected source text.", false),
        "NORMALIZED_BASELINE_VISUAL_INVARIANTS_CHANGED" => new("NORMALIZED_BASELINE_VISUAL_INVARIANTS_CHANGED", ErrorCategory.Integrity, "A normalized baseline target changed a strict visual invariant.", false),
        "NORMALIZED_BASELINE_ARTIFACT_CHANGED" => new("NORMALIZED_BASELINE_ARTIFACT_CHANGED", ErrorCategory.Integrity, "The normalized baseline bytes changed during read-only validation.", false),
        "NORMALIZED_BASELINE_TRANSITION_UNSAFE" => new("NORMALIZED_BASELINE_TRANSITION_UNSAFE", ErrorCategory.Integrity, "The normalized baseline produced a material invariant transition.", false),
        "NORMALIZED_BASELINE_UNSTABLE" => new("NORMALIZED_BASELINE_UNSTABLE", ErrorCategory.Integrity, "The normalized baseline did not converge within the bounded validation reopenings.", false),
        "CANDIDATE_ARTIFACT_CHANGED" => new("CANDIDATE_ARTIFACT_CHANGED", ErrorCategory.Integrity, "The candidate bytes changed during read-only validation.", false),
        "CANDIDATE_INVARIANT_TRANSITION_UNSAFE" => new("CANDIDATE_INVARIANT_TRANSITION_UNSAFE", ErrorCategory.Integrity, "The candidate produced a material invariant transition.", false),
        "CANDIDATE_INVARIANTS_UNSTABLE" => new("CANDIDATE_INVARIANTS_UNSTABLE", ErrorCategory.Integrity, "The candidate did not converge within the bounded validation reopenings.", false),
        "NORMALIZED_BASELINE_CLEANUP_FAILED" => new("NORMALIZED_BASELINE_CLEANUP_FAILED", ErrorCategory.Integrity, "The temporary normalized baseline could not be removed; no output was promoted.", false),
        _ => new("CANDIDATE_INTEGRITY_FAILED", ErrorCategory.Integrity, "The candidate failed a strict integrity check.", false)
    };

        return error with { TechnicalStage = SupportedStageOrNull(technicalStage) };
    }

    public static ContractError FromAutoCAD(Autodesk.AutoCAD.Runtime.Exception exception, string? technicalStage = null)
    {
        var error = exception.ErrorStatus switch
        {
            ErrorStatus.FilerError or ErrorStatus.DwgNeedsRecovery => new ContractError("CANDIDATE_VALIDATION_FAILED", ErrorCategory.Integrity, "AutoCAD rejected the candidate as invalid or corrupt.", false),
            ErrorStatus.FileAccessErr => new ContractError("DOCUMENT_LOCKED", ErrorCategory.Concurrency, "AutoCAD could not obtain exclusive candidate access.", false),
            ErrorStatus.NoDocument => new ContractError("CAD_SESSION_UNAVAILABLE", ErrorCategory.Environment, "No AutoCAD command context is available.", false),
            _ => new ContractError("CAD_WRITE_FAILED", ErrorCategory.Environment, "AutoCAD failed during the bounded write lifecycle.", false)
        };
        var nativeStatus = Enum.GetName(exception.ErrorStatus);
        return error with
        {
            TechnicalStage = SupportedStageOrNull(technicalStage),
            NativeErrorStatus = CadNativeErrorStatuses.IsSupported(nativeStatus)
                ? nativeStatus
                : null
        };
    }

    private static string? SupportedStageOrNull(string? value) =>
        CadWriteTechnicalStages.IsSupported(value) ? value : null;
}
