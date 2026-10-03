using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using DwgTranslator.Agent;
using DwgTranslator.AgentMcp;
using DwgTranslator.Application;
using DwgTranslator.Infrastructure.Windows;

var parsed = CliArguments.Parse(args);
if (parsed.Error is not null)
{
    Write(Failure(parsed.Command, parsed.Error));
    return 2;
}
var loaded = AgentConfigurationLoader.Load(parsed.Config!);
if (loaded.Error is not null)
{
    Write(Failure(parsed.Command, loaded.Error.Code));
    return 2;
}
var configuration = loaded.Configuration!;
var reference = SecretReference.Create(configuration.CredentialReference);
if (!reference.IsSuccess)
{
    Write(Failure(parsed.Command, "AGENT_CREDENTIAL_REFERENCE_INVALID"));
    return 2;
}
var token = await new WindowsCredentialSecretStore().GetAsync(reference.Value!, CancellationToken.None);
if (!token.IsSuccess || string.IsNullOrWhiteSpace(token.Value))
{
    Write(Failure(parsed.Command, "AGENT_CREDENTIAL_UNAVAILABLE"));
    return 5;
}

AgentMcpResult result;
try
{
    using var client = new AgentHostHttpReadClient(new Uri(configuration.HostUrl), token.Value);
    result = await ExecuteAsync(client, parsed);
}
catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
{
    result = Failure(parsed.Command, "AGENT_UNEXPECTED_ERROR");
}
Write(result);
return ExitCode(result);

static async Task<AgentMcpResult> ExecuteAsync(IAgentHostReadClient client, CliArguments parsed)
{
    var o = parsed.Options;
    return parsed.Command switch
    {
        "health" => await client.HealthAsync(CancellationToken.None),
        "capabilities" => await client.CapabilitiesAsync(CancellationToken.None),
        "jobs-list" => await client.ListJobsAsync(CancellationToken.None),
        "job-get" => await client.GetJobAsync(Guid.Parse(o["job-id"]), CancellationToken.None),
        "inspection-plan" => await client.CreateInspectionPlanAsync(o["source"], CancellationToken.None),
        "inspect-dry-run" => await client.InspectDwgAsync(o["source"], o["source-hash"], o["approval"], CancellationToken.None),
        "invariant-diff-plan" => await client.CreateInvariantDiffPlanAsync(new { pairs = ReadInvariantPairs(o["pairs-json"]) }, CancellationToken.None),
        "invariant-diff-run" => await client.RunInvariantDiffAsync(new { planId = o["plan-id"], planHash = o["plan-hash"], approvalId = o["approval-id"], consent = o["consent"], idempotencyKey = o["idempotency-key"] }, CancellationToken.None),
        "translation-plan" => await client.CreateTranslationPlanAsync(new
        {
            sourcePath = o["source"],
            targetLanguage = o["target-language"],
            routingMode = o.GetValueOrDefault("routing-mode", "Auto"),
            manualModel = o.GetValueOrDefault("manual-model"),
            outputPath = o.GetValueOrDefault("output")
        }, CancellationToken.None),
        "translation-prepare" => await client.PrepareTranslationAsync(new
        {
            planId = o["plan-id"],
            approvalId = o["approval-id"],
            sourceHash = o["source-hash"],
            consent = o["consent"],
            idempotencyKey = o["idempotency-key"]
        }, CancellationToken.None),
        "translation-review-get" => await client.GetTranslationReviewAsync(new
        {
            jobId = Guid.Parse(o["job-id"]),
            page = Int(o, "page", 1),
            pageSize = Int(o, "page-size", 50),
            includeText = parsed.Flags.Contains("include-text")
        }, CancellationToken.None),
        "translation-review-apply" => await client.ApplyTranslationReviewAsync(new
        {
            jobId = Guid.Parse(o["job-id"]),
            expectedJobVersion = long.Parse(o["expected-job-version"], CultureInfo.InvariantCulture),
            decisions = ReadDecisions(o.GetValueOrDefault("decisions-json")),
            bulkApprove = parsed.Flags.Contains("bulk-approve"),
            bulkApproval = o.GetValueOrDefault("bulk-approval"),
            idempotencyKey = o["idempotency-key"],
            expectedReviewVersion = NullableLong(o, "expected-review-version"),
            expectedContextHash = o.GetValueOrDefault("expected-context-hash"),
            automationAuthority = ReadReviewAutomationAuthority(o.GetValueOrDefault("automation-authority-json")),
            reviseApproved = parsed.Flags.Contains("revise-approved")
        }, CancellationToken.None),
        "generation-plan" => await client.CreateGenerationPlanAsync(new { jobId = Guid.Parse(o["job-id"]) }, CancellationToken.None),
        "generation-reconcile-plan" => await client.CreateGenerationReconciliationPlanAsync(new { jobId = Guid.Parse(o["job-id"]) }, CancellationToken.None),
        "generation-reconcile-apply" => await client.ApplyGenerationReconciliationAsync(new
        {
            reconciliationPlanId = o["reconciliation-plan-id"],
            approvalId = o["approval-id"],
            expectedJobVersion = long.Parse(o["expected-job-version"], CultureInfo.InvariantCulture),
            consent = o["consent"],
            idempotencyKey = o["idempotency-key"]
        }, CancellationToken.None),
        "generate" => await client.GenerateAsync(new
        {
            generationPlanId = o["generation-plan-id"],
            approvalId = o["approval-id"],
            expectedJobVersion = long.Parse(o["expected-job-version"], CultureInfo.InvariantCulture),
            approval = o["approval"],
            idempotencyKey = o["idempotency-key"]
        }, CancellationToken.None),
        "job-status" => await client.JobStatusAsync(Guid.Parse(o["job-id"]), CancellationToken.None),
        "job-next-action" => await client.JobNextActionAsync(Guid.Parse(o["job-id"]), CancellationToken.None),
        "workflow-cancel" => await client.CancelWorkflowAsync(new
        {
            jobId = Guid.Parse(o["job-id"]),
            expectedJobVersion = long.Parse(o["expected-job-version"], CultureInfo.InvariantCulture),
            idempotencyKey = o["idempotency-key"]
        }, CancellationToken.None),
        "batch-plan" => await client.CreateBatchPlanAsync(new
        {
            sourceDirectory = o["source-directory"],
            outputDirectory = o["output-directory"],
            targetLanguage = o["target-language"],
            routingMode = o.GetValueOrDefault("routing-mode", "Auto"),
            terminologyVersion = o["terminology-version"],
            policy = o["policy"]
        }, CancellationToken.None),
        "batch-start" => await client.StartBatchAsync(new
        {
            planId = o["plan-id"],
            manifestHash = o["manifest-hash"],
            approvalId = o["approval-id"],
            consent = o["consent"],
            idempotencyKey = o["idempotency-key"]
        }, CancellationToken.None),
        "batch-status" => await client.BatchStatusAsync(Guid.Parse(o["batch-id"]), CancellationToken.None),
        "batch-next-action" => await client.BatchNextActionAsync(Guid.Parse(o["batch-id"]), CancellationToken.None),
        "batch-review-summary" => await client.BatchReviewSummaryAsync(Guid.Parse(o["batch-id"]), CancellationToken.None),
        "batch-approve-and-generate" => await client.ApproveAndGenerateBatchAsync(new
        {
            batchId = Guid.Parse(o["batch-id"]),
            expectedBatchVersion = long.Parse(o["expected-batch-version"], CultureInfo.InvariantCulture),
            approvalId = o["approval-id"],
            approval = o["approval"],
            idempotencyKey = o["idempotency-key"]
        }, CancellationToken.None),
        "batch-report" => await client.BatchReportAsync(Guid.Parse(o["batch-id"]), CancellationToken.None),
        "batch-cancel" => await client.CancelBatchAsync(new
        {
            batchId = Guid.Parse(o["batch-id"]),
            expectedBatchVersion = long.Parse(o["expected-batch-version"], CultureInfo.InvariantCulture),
            idempotencyKey = o["idempotency-key"]
        }, CancellationToken.None),
        "batch-recovery-plan" => await client.CreateBatchRecoveryPlanAsync(new
        {
            originalBatchId = Guid.Parse(o["original-batch-id"]),
            relativePaths = ReadStringList(o["relative-paths-json"])
        }, CancellationToken.None),
        "batch-recovery-start" => await client.StartBatchRecoveryAsync(new
        {
            recoveryPlanId = o["recovery-plan-id"],
            recoveryManifestHash = o["recovery-manifest-hash"],
            approvalId = o["approval-id"],
            consent = o["consent"],
            idempotencyKey = o["idempotency-key"]
        }, CancellationToken.None),
        "batch-reconcile-review" => await client.ReconcileBatchReviewAsync(new
        {
            batchId = Guid.Parse(o["batch-id"]),
            expectedBatchVersion = long.Parse(o["expected-batch-version"], CultureInfo.InvariantCulture),
            idempotencyKey = o["idempotency-key"]
        }, CancellationToken.None),
        _ => Failure(parsed.Command, "AGENT_COMMAND_INVALID")
    };
}

static IReadOnlyList<AgentMcpReviewDecision>? ReadDecisions(string? path)
{
    if (path is null) return null;
    using var stream = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read);
    return JsonSerializer.Deserialize<AgentMcpReviewDecision[]>(stream, CliJson.Options)
        ?? throw new JsonException("REVIEW_DECISIONS_INVALID");
}

static AgentMcpReviewAutomationReceipt? ReadReviewAutomationAuthority(string? path)
{
    if (path is null) return null;
    using var stream = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read);
    return JsonSerializer.Deserialize<AgentMcpReviewAutomationReceipt>(stream, CliJson.Options)
        ?? throw new JsonException("REVIEW_AUTOMATION_AUTHORITY_INVALID");
}

static IReadOnlyList<string> ReadStringList(string value) =>
    JsonSerializer.Deserialize<string[]>(value, CliJson.Options) ?? throw new JsonException("RECOVERY_PATHS_INVALID");
static IReadOnlyList<AgentMcpInvariantDiffPair> ReadInvariantPairs(string value) =>
    JsonSerializer.Deserialize<AgentMcpInvariantDiffPair[]>(value, CliJson.Options) ?? throw new JsonException("INVARIANT_DIFF_PAIRS_INVALID");

static int Int(Dictionary<string, string> options, string key, int fallback) =>
    options.TryGetValue(key, out var value) ? int.Parse(value, CultureInfo.InvariantCulture) : fallback;
static long? NullableLong(Dictionary<string, string> options, string key) =>
    options.TryGetValue(key, out var value) ? long.Parse(value, CultureInfo.InvariantCulture) : null;

static int ExitCode(AgentMcpResult result)
{
    if (result.Success) return 0;
    return result.Error?.Code switch
    {
        "APPROVAL_REQUIRED" or "APPROVAL_EXPIRED" or "APPROVAL_REPLAYED" or "CONSENT_REQUIRED" => 3,
        "JOB_STATE_CONFLICT" or "OUTPUT_ALREADY_EXISTS" or "IDEMPOTENCY_CONFLICT" or "IDEMPOTENCY_PENDING" => 4,
        "IPC_TIMEOUT" or "AGENT_HOST_UNAVAILABLE" or "AGENT_HOST_TIMEOUT" or "OPENAI_UNAVAILABLE" => 5,
        _ => 1
    };
}

static AgentMcpResult Failure(string command, string code) =>
    new("dwg-agent-cli/1.0", false, command, DateTimeOffset.UtcNow, null,
        new(code, "The CLI request was rejected. See the stable error code."));

static void Write(AgentMcpResult result) => Console.Out.WriteLine(JsonSerializer.Serialize(result, CliJson.Options));

internal sealed record CliArguments(
    string Command,
    string? Config,
    Dictionary<string, string> Options,
    HashSet<string> Flags,
    string? Error)
{
    private static readonly HashSet<string> Commands = new(StringComparer.Ordinal)
    {
        "health", "capabilities", "jobs-list", "job-get", "inspection-plan", "inspect-dry-run", "invariant-diff-plan", "invariant-diff-run",
        "translation-plan", "translation-prepare", "translation-review-get", "translation-review-apply",
        "generation-plan", "generation-reconcile-plan", "generation-reconcile-apply", "generate", "job-status", "job-next-action", "workflow-cancel",
        "batch-plan", "batch-start", "batch-status", "batch-next-action", "batch-review-summary",
        "batch-approve-and-generate", "batch-report", "batch-cancel", "batch-recovery-plan", "batch-recovery-start",
        "batch-reconcile-review"
    };
    private static readonly HashSet<string> BooleanFlags = new(StringComparer.Ordinal)
    {
        "include-text", "bulk-approve", "revise-approved"
    };

    internal static CliArguments Parse(string[] values)
    {
        var command = values.Length == 0 ? "unknown" : values[0];
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 1; index < values.Length; index++)
        {
            if (!values[index].StartsWith("--", StringComparison.Ordinal))
                return new(command, null, options, flags, "AGENT_ARGUMENT_INVALID");
            var name = values[index][2..];
            if (BooleanFlags.Contains(name)) { flags.Add(name); continue; }
            if (++index >= values.Length || !options.TryAdd(name, values[index]))
                return new(command, null, options, flags, "AGENT_ARGUMENT_INVALID");
        }
        if (!Commands.Contains(command)) return new(command, null, options, flags, "AGENT_COMMAND_INVALID");
        if (!options.TryGetValue("config", out var config) || string.IsNullOrWhiteSpace(config))
            return new(command, null, options, flags, "AGENT_CONFIG_REQUIRED");
        return Required(command, options, flags, out var missing)
            ? new(command, config, options, flags, null)
            : new(command, config, options, flags, missing);
    }

    private static bool Required(string command, Dictionary<string, string> options, HashSet<string> flags, out string error)
    {
        string[] required = command switch
        {
            "job-get" or "job-status" or "job-next-action" or "generation-plan" or "generation-reconcile-plan" => ["job-id"],
            "generation-reconcile-apply" => ["reconciliation-plan-id", "approval-id", "expected-job-version", "consent", "idempotency-key"],
            "inspection-plan" => ["source"],
            "inspect-dry-run" => ["source", "source-hash", "approval"],
            "invariant-diff-plan" => ["pairs-json"],
            "invariant-diff-run" => ["plan-id", "plan-hash", "approval-id", "consent", "idempotency-key"],
            "translation-plan" => ["source", "target-language"],
            "translation-prepare" => ["plan-id", "approval-id", "source-hash", "consent", "idempotency-key"],
            "translation-review-get" => ["job-id"],
            "translation-review-apply" => ["job-id", "expected-job-version", "idempotency-key"],
            "generate" => ["generation-plan-id", "approval-id", "expected-job-version", "approval", "idempotency-key"],
            "workflow-cancel" => ["job-id", "expected-job-version", "idempotency-key"],
            "batch-plan" => ["source-directory", "output-directory", "target-language", "terminology-version", "policy"],
            "batch-start" => ["plan-id", "manifest-hash", "approval-id", "consent", "idempotency-key"],
            "batch-status" or "batch-next-action" or "batch-review-summary" or "batch-report" => ["batch-id"],
            "batch-approve-and-generate" => ["batch-id", "expected-batch-version", "approval-id", "approval", "idempotency-key"],
            "batch-cancel" => ["batch-id", "expected-batch-version", "idempotency-key"],
            "batch-recovery-plan" => ["original-batch-id", "relative-paths-json"],
            "batch-recovery-start" => ["recovery-plan-id", "recovery-manifest-hash", "approval-id", "consent", "idempotency-key"],
            "batch-reconcile-review" => ["batch-id", "expected-batch-version", "idempotency-key"],
            _ => []
        };
        if (required.Any(item => !options.ContainsKey(item))) { error = "AGENT_ARGUMENT_REQUIRED"; return false; }
        if (command == "translation-review-apply" && !flags.Contains("bulk-approve") && !options.ContainsKey("decisions-json"))
        { error = "REVIEW_DECISIONS_REQUIRED"; return false; }
        if (options.TryGetValue("job-id", out var jobId) && !Guid.TryParse(jobId, out _))
        { error = "AGENT_JOB_ID_INVALID"; return false; }
        if (options.TryGetValue("expected-job-version", out var version) &&
            (!long.TryParse(version, out var parsedVersion) || parsedVersion < 0))
        { error = "JOB_VERSION_INVALID"; return false; }
        if (options.TryGetValue("expected-review-version", out var reviewVersion) &&
            (!long.TryParse(reviewVersion, out var parsedReviewVersion) || parsedReviewVersion < 0))
        { error = "REVIEW_VERSION_INVALID"; return false; }
        if (options.TryGetValue("batch-id", out var batchId) && !Guid.TryParse(batchId, out _))
        { error = "BATCH_ID_INVALID"; return false; }
        if (options.TryGetValue("original-batch-id", out var originalBatchId) && !Guid.TryParse(originalBatchId, out _))
        { error = "BATCH_ID_INVALID"; return false; }
        if (options.TryGetValue("expected-batch-version", out var batchVersion) &&
            (!long.TryParse(batchVersion, out var parsedBatchVersion) || parsedBatchVersion < 0))
        { error = "BATCH_VERSION_INVALID"; return false; }
        error = string.Empty;
        return true;
    }
}

internal static class CliJson
{
    internal static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
}
