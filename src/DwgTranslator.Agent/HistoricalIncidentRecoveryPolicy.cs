using DwgTranslator.Application;
using DwgTranslator.Contracts;

namespace DwgTranslator.Agent;

/// <summary>Historical incident exceptions are unavailable in this source candidate.</summary>
internal static class HistoricalIncidentRecoveryPolicy
{
    internal static bool IsAuthorizedRecoverySubset(AgentBatchDocument original,
        IReadOnlyList<AgentBatchRecoveryEntry> entries) => false;

    internal static bool IsKnownStaleProjection(AgentBatchDocument original, AgentBatchPlan plan,
        AgentBatchFileProgress file, JobDocument current) => false;

    internal static bool ValidFreshReviewDecisionSet(Guid jobId, string sourceBasename,
        ApprovedContextRevalidationReceipt marker, TranslationReviewSnapshot review,
        IReadOnlyList<ReviewDecisionInput> decisions, ReviewAutomationReceipt? authority) => false;

    internal static bool TryGetRecovery25GenerationWriteAdapter(out string assemblyPath)
    {
        assemblyPath = string.Empty;
        return false;
    }

    internal static bool TryGetFresh600GenerationWriteAdapter(out string assemblyPath)
    {
        assemblyPath = string.Empty;
        return false;
    }

    internal static bool IsExactFresh600LateBootstrapBinding(AgentBatchDocument batch,
        AgentBatchPlan plan, AgentBatchFileProgress file, Guid jobId, long jobVersion,
        AgentWorkflowOperation operation) => false;

    internal static bool ValidSupersedingApprovedDecisionSet(Guid jobId, string sourceBasename,
        string contextHash, ReviewAutomationReceipt? authority,
        IReadOnlyList<ReviewDecisionInput> decisions) => false;

    internal static bool IsSupersedingAuthorityScope(ReviewAutomationReceipt? authority) => false;

    internal static bool PermitsResolvedTerminologyAmbiguity(bool exactSupersedingSet,
        TerminologyResult source, TerminologyResult corrected,
        IReadOnlyList<string> validationErrors) => false;
}
