using DwgTranslator.Contracts;

namespace DwgTranslator.Agent;

/// <summary>Durable operation transitions shared by prepare, resume and generation.</summary>
internal sealed class AgentWorkflowOperationCoordinator(
    AgentWorkflowFileStore files,
    IAgentTranslationWorkflowBackend backend,
    Func<DateTimeOffset> utcNow)
{
    internal Task<bool> HeartbeatAsync(AgentWorkflowOperation operation)
    {
        var now = utcNow().ToUniversalTime();
        return files.TryTransitionOperationAsync(operation with
        {
            UpdatedAtUtc = now,
            LastHeartbeatAtUtc = now
        }, "Running", CancellationToken.None);
    }

    internal async Task<AgentWorkflowOperation> UpdateRunningAsync(
        AgentWorkflowOperation operation,
        string stage,
        DateTimeOffset? deadlineAtUtc = null,
        string? cleanupOutcome = null)
    {
        var now = utcNow().ToUniversalTime();
        var replacement = operation with
        {
            Stage = stage,
            UpdatedAtUtc = now,
            StartedAtUtc = operation.StartedAtUtc ?? now,
            DeadlineAtUtc = deadlineAtUtc?.ToUniversalTime() ?? operation.DeadlineAtUtc,
            LastHeartbeatAtUtc = now,
            CleanupOutcome = cleanupOutcome ?? operation.CleanupOutcome
        };
        return await files.TryTransitionOperationAsync(replacement, "Running", CancellationToken.None)
            .ConfigureAwait(false) ? replacement : operation;
    }

    internal Task<bool> CompleteAsync(AgentWorkflowOperation operation, string stage)
    {
        var now = utcNow().ToUniversalTime();
        return files.TryTransitionOperationAsync(operation with
        {
            State = "Completed",
            Stage = stage,
            UpdatedAtUtc = now,
            ErrorCode = null,
            Retryable = false,
            LastHeartbeatAtUtc = now,
            CompletedAtUtc = now,
            DurationMilliseconds = OperationDurationMilliseconds(operation, now)
        }, "Running", CancellationToken.None);
    }

    internal async Task<bool> FailAsync(
        AgentWorkflowOperation operation,
        string code,
        bool retryable,
        ErrorCategory? category = null,
        string? technicalStage = null,
        string? nativeErrorStatus = null)
    {
        var now = utcNow().ToUniversalTime();
        var transitioned = await files.TryTransitionOperationAsync(operation with
        {
            State = "Failed",
            Stage = "Failed",
            UpdatedAtUtc = now,
            ErrorCode = code,
            Retryable = retryable,
            ErrorCategory = category,
            TechnicalStage = technicalStage,
            NativeErrorStatus = nativeErrorStatus,
            LastHeartbeatAtUtc = now,
            CompletedAtUtc = now,
            DurationMilliseconds = OperationDurationMilliseconds(operation, now)
        }, "Running", CancellationToken.None).ConfigureAwait(false);
        if (transitioned)
            await backend.FailAsync(operation.JobId, code, retryable, CancellationToken.None).ConfigureAwait(false);
        return transitioned;
    }

    internal AgentWorkflowOperation New(string kind, Guid jobId, string stage)
    {
        var now = utcNow().ToUniversalTime();
        return new(AgentWorkflowFileStore.OpaqueToken(24), kind, jobId, "Running", stage, now, now);
    }

    private static long OperationDurationMilliseconds(AgentWorkflowOperation operation, DateTimeOffset completedAtUtc) =>
        Math.Max(0L, (long)Math.Ceiling((completedAtUtc -
            (operation.StartedAtUtc ?? operation.CreatedAtUtc)).TotalMilliseconds));
}
