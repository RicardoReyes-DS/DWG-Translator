using System.Text.Json;
using System.Text.Json.Nodes;
using DwgTranslator.Application;
using DwgTranslator.Domain;

namespace DwgTranslator.Agent;

/// <summary>Read-only response projections for a durable workflow job.</summary>
internal static class AgentWorkflowJobProjection
{
    internal static JsonObject Status(
        Guid requestedJobId,
        JobDocument job,
        AgentWorkflowOperation? operation,
        TranslationReviewSnapshot? review,
        DwgTranslationJobData? data,
        JsonSerializerOptions json)
    {
        var failure = job.Data["failure"] as JsonObject ?? job.Data["agentFailure"] as JsonObject;
        return new JsonObject
        {
            ["schemaVersion"] = AgentWorkflowContract.SchemaVersion,
            ["jobId"] = requestedJobId.ToString("D"),
            ["jobVersion"] = job.Version,
            ["state"] = job.State.ToString(),
            ["updatedAtUtc"] = job.UpdatedAtUtc.ToString("O"),
            ["progress"] = Progress(job.State),
            ["operation"] = operation is null ? null : JsonSerializer.SerializeToNode(operation, json),
            ["review"] = review is null ? null : new JsonObject
            {
                ["segments"] = review.Rows.Count,
                ["pending"] = review.Rows.Count(row => row.State is not (SegmentState.Approved or SegmentState.Excluded)),
                ["highRisk"] = review.Rows.Count(row => row.RiskSeverity == "high"),
                ["escalated"] = review.Rows.Count(row => row.Escalated),
                ["requestedMode"] = review.RequestedMode,
                ["baseModel"] = review.BaseModel,
                ["routingVersion"] = review.RoutingVersion
            },
            ["outputHash"] = data?.OutputHash,
            ["validation"] = data?.ValidationReport is null ? null : new JsonObject
            {
                ["policy"] = data.ValidationReport.Policy,
                ["automaticPass"] = data.ValidationReport.AutomaticPass,
                ["visualReviewPending"] = data.ValidationReport.VisualReviewRequired,
                ["entityCount"] = data.ValidationReport.EntityCount
            },
            ["lastError"] = failure is null ? null : new JsonObject
            {
                ["code"] = failure["code"]?.DeepClone(),
                ["retryable"] = failure["retryable"]?.DeepClone(),
                ["stage"] = failure["stage"]?.DeepClone(),
                ["technicalStage"] = failure["technicalStage"]?.DeepClone(),
                ["nativeErrorStatus"] = failure["nativeErrorStatus"]?.DeepClone(),
                ["diagnosticId"] = failure["diagnosticId"]?.DeepClone(),
                ["invariantDiagnostics"] = CadInvariantDiagnosticsView.Select(failure["invariantDiagnostics"])
            }
        };
    }

    internal static JsonObject NextAction(Guid requestedJobId, JobDocument job)
    {
        var next = job.State switch
        {
            JobState.ReviewRequired => ("dwg_translation_review_get", true, "Retrieve proposals, then apply explicit human decisions."),
            JobState.Approved => ("dwg_generation_plan", true, "Create a fresh job/version/output-bound generation plan."),
            JobState.Completed => ("none", false, "The workflow is complete; do not repeat completed operations."),
            JobState.Failed => ("dwg_job_status", FailureRetryable(job), "Inspect the redacted failure before any explicit recovery decision."),
            JobState.Cancelled => ("none", false, "The workflow was cancelled."),
            _ => ("dwg_job_status", true, "Poll durable progress; do not start a duplicate operation.")
        };
        return new JsonObject
        {
            ["schemaVersion"] = AgentWorkflowContract.SchemaVersion,
            ["jobId"] = requestedJobId.ToString("D"),
            ["jobVersion"] = job.Version,
            ["state"] = job.State.ToString(),
            ["tool"] = next.Item1,
            ["recoverable"] = next.Item2,
            ["instruction"] = next.Item3
        };
    }

    private static bool FailureRetryable(JobDocument job)
    {
        var failure = job.Data["failure"] as JsonObject ?? job.Data["agentFailure"] as JsonObject;
        return failure?["retryable"]?.GetValue<bool>() is true;
    }

    private static int Progress(JobState state) => state switch
    {
        JobState.Draft => 5,
        JobState.Inspecting => 15,
        JobState.Extracted => 30,
        JobState.Translating => 50,
        JobState.ReviewRequired => 65,
        JobState.Approved => 75,
        JobState.Writing => 85,
        JobState.Validating => 95,
        JobState.Completed => 100,
        _ => 0
    };
}
