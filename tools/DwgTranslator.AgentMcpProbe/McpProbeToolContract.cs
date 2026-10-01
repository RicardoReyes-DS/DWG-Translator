namespace DwgTranslator.AgentMcpProbe;

/// <summary>
/// The complete, versioned MCP surface checked by the contract probe.
/// A candidate is accepted only when the server exposes this exact set.
/// </summary>
public static class McpProbeToolContract
{
    public static IReadOnlyList<string> ExpectedToolNames { get; } = Array.AsReadOnly(new[]
    {
        "dwg_batch_approve_and_generate", "dwg_batch_cancel", "dwg_batch_next_action", "dwg_batch_plan",
        "dwg_batch_reconcile_review", "dwg_batch_recovery_plan", "dwg_batch_recovery_start", "dwg_batch_report",
        "dwg_batch_review_summary", "dwg_batch_start", "dwg_batch_status",
        "dwg_capabilities", "dwg_generate", "dwg_generation_plan", "dwg_generation_reconcile_apply",
        "dwg_generation_reconcile_plan", "dwg_health", "dwg_inspect_dry_run", "dwg_inspection_plan",
        "dwg_invariant_diff_plan", "dwg_invariant_diff_run", "dwg_job_get", "dwg_job_next_action",
        "dwg_job_status", "dwg_jobs_list", "dwg_translation_plan", "dwg_translation_prepare",
        "dwg_translation_review_apply", "dwg_translation_review_get", "dwg_workflow_cancel"
    }.Order(StringComparer.Ordinal).ToArray());

    public static bool IsExact(IReadOnlyCollection<string> actual)
    {
        ArgumentNullException.ThrowIfNull(actual);
        return actual.Count == ExpectedToolNames.Count &&
            actual.Order(StringComparer.Ordinal).SequenceEqual(ExpectedToolNames, StringComparer.Ordinal);
    }
}
