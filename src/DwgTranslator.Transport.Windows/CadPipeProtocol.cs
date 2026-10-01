using DwgTranslator.Contracts;

namespace DwgTranslator.Transport.Windows;

public static class CadPipeProtocol
{
    public static string PipeName(Guid instanceId)
    {
        if (instanceId == Guid.Empty)
            throw new ArgumentException("PIPE_INSTANCE_ID_EMPTY", nameof(instanceId));
        return $"dwgtranslator-v1-{Environment.ProcessId}-{instanceId:N}";
    }

    public static bool TryResponseType(string requestType, out string responseType)
    {
        responseType = requestType switch
        {
            MessageTypes.CapabilitiesRequest => MessageTypes.CapabilitiesResponse,
            MessageTypes.InspectRequest => MessageTypes.InspectResponse,
            MessageTypes.ExtractRequest => MessageTypes.ExtractResponse,
            MessageTypes.InvariantDiffRequest => MessageTypes.InvariantDiffResponse,
            MessageTypes.WriteRequest => MessageTypes.WriteResponse,
            MessageTypes.ReconcileRequest => MessageTypes.ReconcileResponse,
            MessageTypes.ValidateRequest => MessageTypes.ValidateResponse,
            MessageTypes.StatusRequest => MessageTypes.StatusResponse,
            MessageTypes.CancelRequest => MessageTypes.CancelResponse,
            _ => string.Empty
        };
        return responseType.Length != 0;
    }

    public static ContractError Error(string code, ErrorCategory category, string message, bool retryable = false) =>
        new(code, category, message, retryable);
}
