namespace DwgTranslator.Domain;

public enum JobState
{
    Draft,
    Inspecting,
    Extracted,
    Translating,
    ReviewRequired,
    Approved,
    Writing,
    Validating,
    Completed,
    Failed,
    Cancelled
}

public enum CancellationDisposition
{
    Allowed,
    PendingSafeBoundary,
    RejectedTerminal
}

public static class JobLifecycle
{
    private static readonly Dictionary<JobState, IReadOnlySet<JobState>> Allowed =
        new Dictionary<JobState, IReadOnlySet<JobState>>
        {
            [JobState.Draft] = new HashSet<JobState> { JobState.Inspecting, JobState.Failed, JobState.Cancelled },
            [JobState.Inspecting] = new HashSet<JobState> { JobState.Extracted, JobState.Failed, JobState.Cancelled },
            [JobState.Extracted] = new HashSet<JobState> { JobState.Translating, JobState.Cancelled },
            [JobState.Translating] = new HashSet<JobState> { JobState.ReviewRequired, JobState.Failed, JobState.Cancelled },
            [JobState.ReviewRequired] = new HashSet<JobState> { JobState.Approved, JobState.Translating, JobState.Cancelled },
            [JobState.Approved] = new HashSet<JobState> { JobState.Writing, JobState.ReviewRequired, JobState.Cancelled },
            [JobState.Writing] = new HashSet<JobState> { JobState.Validating, JobState.Failed },
            [JobState.Validating] = new HashSet<JobState> { JobState.Completed, JobState.Failed },
            [JobState.Completed] = new HashSet<JobState>(),
            [JobState.Failed] = new HashSet<JobState>(),
            [JobState.Cancelled] = new HashSet<JobState>()
        };

    public static bool CanTransition(JobState from, JobState to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    public static CancellationDisposition RequestCancellation(JobState state, bool insideIndivisibleCadBoundary)
    {
        if (state is JobState.Completed or JobState.Failed or JobState.Cancelled)
            return CancellationDisposition.RejectedTerminal;
        if (insideIndivisibleCadBoundary || state is JobState.Writing or JobState.Validating)
            return CancellationDisposition.PendingSafeBoundary;
        return CancellationDisposition.Allowed;
    }
}

public sealed record ResumeCheckpoint(
    JobState SafeState,
    ContentHash SourceHash,
    string ContractVersion,
    ContentHash ConfigurationHash);

public static class ResumePolicy
{
    public static bool CanResume(
        JobState currentState,
        ResumeCheckpoint checkpoint,
        ContentHash actualSourceHash,
        string actualContractVersion,
        ContentHash actualConfigurationHash) =>
        currentState == JobState.Failed &&
        checkpoint.SafeState is not JobState.Writing and not JobState.Validating &&
        checkpoint.SourceHash == actualSourceHash &&
        checkpoint.ConfigurationHash == actualConfigurationHash &&
        string.Equals(checkpoint.ContractVersion, actualContractVersion, StringComparison.Ordinal);
}
