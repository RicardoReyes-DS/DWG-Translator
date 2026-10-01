using System.Text.Json;
using System.Text.Json.Nodes;
using DwgTranslator.Application;

namespace DwgTranslator.Agent;

/// <summary>Read-only projections of an already loaded batch document.</summary>
internal static class AgentBatchProjection
{
    internal static JsonNode Status(AgentBatchDocument document, bool includeFiles, JsonSerializerOptions json)
    {
        var groups = document.Files.GroupBy(file => file.State).ToDictionary(group => group.Key.ToString().ToLowerInvariant(), group => group.Count());
        return JsonSerializer.SerializeToNode(new
        {
            schemaVersion = AgentBatchPolicy.SchemaVersion,
            document.BatchId,
            batchVersion = document.Version,
            document.State,
            document.ManifestHash,
            document.CurrentFile,
            document.ErrorCode,
            document.HeartbeatAtUtc,
            totals = new
            {
                total = document.Files.Count,
                queued = Count("queued"),
                inspecting = Count("inspecting"),
                translating = Count("translating"),
                reviewing = Count("reviewing"),
                generating = Count("generating"),
                completed = Count("completed"),
                failed = Count("failed"),
                suspended = Count("suspended")
            },
            usage = new
            {
                inputTokens = document.Files.Sum(file => file.InputTokens),
                outputTokens = document.Files.Sum(file => file.OutputTokens),
                providerRequests = document.Files.Sum(file => file.ProviderRequests)
            },
            terminology = new
            {
                version = ArchitecturalMepTerminologyPolicy.Version,
                matches = document.Files.Sum(file => file.TerminologyMatches),
                ambiguities = document.Files.Sum(file => file.TerminologyAmbiguities)
            },
            approval = document.RefreshedGenerationApprovalId is null ? null : new
            {
                approvalId = document.RefreshedGenerationApprovalId,
                approval = document.RefreshedGenerationApproval,
                expiresAtUtc = document.RefreshedGenerationApprovalExpiresAtUtc,
                singleUse = true,
                purpose = "review-and-generation"
            },
            files = includeFiles ? document.Files : null
        }, json)!;
        int Count(string key) => groups.GetValueOrDefault(key);
    }

    internal static JsonNode NextAction(Guid requestedBatchId, AgentBatchDocument document, JsonSerializerOptions json) =>
        JsonSerializer.SerializeToNode(new
        {
            batchId = requestedBatchId,
            batchVersion = document.Version,
            document.State,
            tool = document.State switch { "ReviewRequired" => "dwg_batch_approve_and_generate", "RecoveryRequired" or "Suspended" => "dwg_batch_recovery_plan", "Completed" or "CompletedWithFailures" or "Failed" or "Cancelled" => "none", _ => "dwg_batch_status" },
            instruction = document.State switch { "ReviewRequired" => "Use the exact manifest-bound batch approval to apply explicit decisions and generate sequential CreateNew outputs.", "RecoveryRequired" or "Suspended" => "Create a new recovery subset plan; this batch will not auto-resume.", "Completed" => "The batch is complete; do not replay completed work.", _ => "Poll durable progress; do not start duplicate work." }
        }, json)!;
}
