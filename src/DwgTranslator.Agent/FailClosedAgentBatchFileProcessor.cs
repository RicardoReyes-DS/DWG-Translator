namespace DwgTranslator.Agent;

public sealed class FailClosedAgentBatchFileProcessor : IAgentBatchFileProcessor
{
    public Task<AgentBatchFileProgress> PrepareAsync(AgentBatchPlan plan, AgentBatchManifestEntry file, CancellationToken cancellationToken) =>
        Task.FromResult(new AgentBatchFileProgress(file.RelativePath, file.Sha256, file.OutputPath,
            AgentBatchFileState.Failed, ErrorCode: "BATCH_PROCESSOR_NOT_CONFIGURED"));

    public Task<AgentBatchFileProgress> ApproveAndGenerateAsync(AgentBatchPlan plan, AgentBatchFileProgress file, CancellationToken cancellationToken) =>
        Task.FromResult(file with { State = AgentBatchFileState.Failed, ErrorCode = "BATCH_PROCESSOR_NOT_CONFIGURED" });
}
