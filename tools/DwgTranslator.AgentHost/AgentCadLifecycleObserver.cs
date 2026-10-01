using System.Collections.Concurrent;
using DwgTranslator.Agent;
using DwgTranslator.Infrastructure.Windows;

namespace DwgTranslator.AgentHost;

/// <summary>Bridges redacted adapter observations into durable recovery receipts.</summary>
public sealed class AgentCadLifecycleObserver : ICadBoundaryObserver
{
    private readonly AgentCadLifecycleReceiptStore _store;
    private readonly ConcurrentDictionary<Guid, AgentCadLifecycleReceipt> _receipts = new();

    public AgentCadLifecycleObserver(string logRoot) => _store = new AgentCadLifecycleReceiptStore(logRoot);

    public void Observe(CadBoundaryObservation observation)
    {
        if (observation.JobId is not { } jobId || jobId == Guid.Empty || observation.ProcessId is not > 0 ||
            string.IsNullOrWhiteSpace(observation.ProcessExecutablePath) || observation.ProcessStartedAtUtc is not { } started ||
            string.IsNullOrWhiteSpace(observation.RequestFingerprint)) return;

        var receipt = _receipts.AddOrUpdate(jobId,
            _ => Create(jobId, observation, started),
            (_, current) => Update(current, observation));
        _store.Upsert(receipt);
    }

    private static AgentCadLifecycleReceipt Create(Guid jobId, CadBoundaryObservation item, DateTimeOffset started) => new(
        jobId, null, item.RequestId ?? Guid.Empty.ToString("D"), item.RequestFingerprint!, item.ProcessId!.Value,
        Path.GetFullPath(item.ProcessExecutablePath!), started, DateTimeOffset.UtcNow,
        item.ResponseHash, item.ExtractedCount, item.ReceivedAtUtc, item.QuitRequestedAtUtc,
        item.ProcessExitedAtUtc, item.ExitCode, item.ErrorCode,
        item.RequiresProcessTerminationApproval,
        IsReceivedResponse(item) ? item.ErrorCode : null,
        IsReceivedResponse(item) ? item.ErrorCategory : null,
        IsReceivedResponse(item) ? item.Retryable : null,
        IsReceivedResponse(item) ? item.DiagnosticId : null,
        IsReceivedResponse(item) ? item.TechnicalStage : null,
        IsReceivedResponse(item) ? item.NativeErrorStatus : null);

    private static AgentCadLifecycleReceipt Update(AgentCadLifecycleReceipt current, CadBoundaryObservation item)
    {
        // A receipt represents one CAD IPC exchange.  Reusing an AutoCAD process for a later
        // request must never carry a successful response from an earlier extraction into a
        // generation timeout/reconciliation decision.
        if (string.Equals(item.Stage, "request", StringComparison.Ordinal))
        {
            return current with
            {
                OperationId = null,
                RequestId = item.RequestId!,
                RequestHash = item.RequestFingerprint!,
                ProcessId = item.ProcessId!.Value,
                ExecutablePath = Path.GetFullPath(item.ProcessExecutablePath!),
                ProcessStartedAtUtc = item.ProcessStartedAtUtc!.Value,
                LaunchedAtUtc = DateTimeOffset.UtcNow,
                ResponseHash = null,
                ExtractedCount = null,
                ReceivedAtUtc = null,
                QuitRequestedAtUtc = null,
                ProcessExitedAtUtc = null,
                ExitCode = null,
                CleanupOutcome = null,
                RequiresProcessTerminationApproval = false,
                ResponseErrorCode = null,
                ResponseErrorCategory = null,
                ResponseRetryable = null,
                ResponseDiagnosticId = null,
                ResponseTechnicalStage = null,
                ResponseNativeErrorStatus = null
            };
        }

        var receivedResponse = IsReceivedResponse(item);
        return current with
        {
            RequestId = item.RequestId ?? current.RequestId,
            RequestHash = item.RequestFingerprint ?? current.RequestHash,
            ResponseHash = item.ResponseHash ?? current.ResponseHash,
            ExtractedCount = item.ExtractedCount ?? current.ExtractedCount,
            ReceivedAtUtc = item.ReceivedAtUtc ?? current.ReceivedAtUtc,
            QuitRequestedAtUtc = item.QuitRequestedAtUtc ?? current.QuitRequestedAtUtc,
            ProcessExitedAtUtc = item.ProcessExitedAtUtc ?? current.ProcessExitedAtUtc,
            ExitCode = item.ExitCode ?? current.ExitCode,
            CleanupOutcome = item.ErrorCode ?? (item.ProcessExitedAtUtc is not null ? "CAD_PROCESS_EXITED" : current.CleanupOutcome),
            RequiresProcessTerminationApproval = current.RequiresProcessTerminationApproval || item.RequiresProcessTerminationApproval,
            ResponseErrorCode = receivedResponse ? item.ErrorCode : current.ResponseErrorCode,
            ResponseErrorCategory = receivedResponse ? item.ErrorCategory : current.ResponseErrorCategory,
            ResponseRetryable = receivedResponse ? item.Retryable : current.ResponseRetryable,
            ResponseDiagnosticId = receivedResponse ? item.DiagnosticId : current.ResponseDiagnosticId,
            ResponseTechnicalStage = receivedResponse ? item.TechnicalStage : current.ResponseTechnicalStage,
            ResponseNativeErrorStatus = receivedResponse ? item.NativeErrorStatus : current.ResponseNativeErrorStatus
        };
    }

    private static bool IsReceivedResponse(CadBoundaryObservation item) =>
        string.Equals(item.Stage, "response", StringComparison.Ordinal) &&
        item.ResponseHash is not null && item.ReceivedAtUtc is not null;
}
