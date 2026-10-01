using System.Text.Json;

namespace DwgTranslator.AgentMcp;

public sealed class SelfTestAgentHostReadClient : IAgentHostReadClient
{
    private static readonly JsonElement Empty = JsonDocument.Parse("{}").RootElement.Clone();

    public Task<AgentMcpResult> HealthAsync(CancellationToken cancellationToken) =>
        Result("health", "ready");

    public Task<AgentMcpResult> CapabilitiesAsync(CancellationToken cancellationToken) =>
        Result("capabilities", "read-only");

    public Task<AgentMcpResult> ListJobsAsync(CancellationToken cancellationToken) =>
        Result("jobs list", "empty");

    public Task<AgentMcpResult> GetJobAsync(Guid jobId, CancellationToken cancellationToken) =>
        Task.FromResult(new AgentMcpResult("dwg-agent-cli/1.0", false, "jobs get", DateTimeOffset.UtcNow, Empty,
            new("AGENT_JOB_NOT_FOUND", "The self-test workspace is empty.")));

    public Task<AgentMcpResult> CreateInspectionPlanAsync(string sourcePath, CancellationToken cancellationToken) =>
        Result("cad inspection plan", "planned");

    public Task<AgentMcpResult> InspectDwgAsync(
        string sourcePath,
        string expectedSourceHash,
        string approval,
        CancellationToken cancellationToken) =>
        Result("cad inspect dry-run", "synthetic");

    public Task<AgentMcpResult> CreateInvariantDiffPlanAsync(object request, CancellationToken cancellationToken) => Result("invariant diff plan", "synthetic");
    public Task<AgentMcpResult> RunInvariantDiffAsync(object request, CancellationToken cancellationToken) => Result("invariant diff run", "synthetic");

    public Task<AgentMcpResult> CreateTranslationPlanAsync(object request, CancellationToken cancellationToken) => Result("translation plan", "synthetic");
    public Task<AgentMcpResult> PrepareTranslationAsync(object request, CancellationToken cancellationToken) => Result("translation prepare", "synthetic");
    public Task<AgentMcpResult> GetTranslationReviewAsync(object request, CancellationToken cancellationToken) => Result("translation review get", "synthetic");
    public Task<AgentMcpResult> ApplyTranslationReviewAsync(object request, CancellationToken cancellationToken) => Result("translation review apply", "synthetic");
    public Task<AgentMcpResult> CreateGenerationPlanAsync(object request, CancellationToken cancellationToken) => Result("generation plan", "synthetic");
    public Task<AgentMcpResult> CreateGenerationReconciliationPlanAsync(object request, CancellationToken cancellationToken) => Result("generation reconciliation plan", "synthetic");
    public Task<AgentMcpResult> ApplyGenerationReconciliationAsync(object request, CancellationToken cancellationToken) => Result("generation reconciliation apply", "synthetic");
    public Task<AgentMcpResult> GenerateAsync(object request, CancellationToken cancellationToken) => Result("generation execute", "synthetic");
    public Task<AgentMcpResult> JobStatusAsync(Guid jobId, CancellationToken cancellationToken) => Result("workflow job status", "synthetic");
    public Task<AgentMcpResult> JobNextActionAsync(Guid jobId, CancellationToken cancellationToken) => Result("workflow job next action", "synthetic");
    public Task<AgentMcpResult> CancelWorkflowAsync(object request, CancellationToken cancellationToken) => Result("workflow cancel", "synthetic");
    public Task<AgentMcpResult> CreateBatchPlanAsync(object request, CancellationToken cancellationToken) => Result("batch plan", "synthetic");
    public Task<AgentMcpResult> StartBatchAsync(object request, CancellationToken cancellationToken) => Result("batch start", "synthetic");
    public Task<AgentMcpResult> BatchStatusAsync(Guid batchId, CancellationToken cancellationToken) => Result("batch status", "synthetic");
    public Task<AgentMcpResult> BatchNextActionAsync(Guid batchId, CancellationToken cancellationToken) => Result("batch next action", "synthetic");
    public Task<AgentMcpResult> BatchReviewSummaryAsync(Guid batchId, CancellationToken cancellationToken) => Result("batch review summary", "synthetic");
    public Task<AgentMcpResult> ApproveAndGenerateBatchAsync(object request, CancellationToken cancellationToken) => Result("batch approve and generate", "synthetic");
    public Task<AgentMcpResult> BatchReportAsync(Guid batchId, CancellationToken cancellationToken) => Result("batch report", "synthetic");
    public Task<AgentMcpResult> CancelBatchAsync(object request, CancellationToken cancellationToken) => Result("batch cancel", "synthetic");
    public Task<AgentMcpResult> CreateBatchRecoveryPlanAsync(object request, CancellationToken cancellationToken) => Result("batch recovery plan", "synthetic");
    public Task<AgentMcpResult> StartBatchRecoveryAsync(object request, CancellationToken cancellationToken) => Result("batch recovery start", "synthetic");
    public Task<AgentMcpResult> ReconcileBatchReviewAsync(object request, CancellationToken cancellationToken) => Result("batch reconcile review", "synthetic");

    private static Task<AgentMcpResult> Result(string command, string status)
    {
        var data = JsonDocument.Parse($"{{\"status\":\"{status}\"}}").RootElement.Clone();
        return Task.FromResult(new AgentMcpResult("dwg-agent-cli/1.0", true, command, DateTimeOffset.UtcNow, data, null));
    }
}
