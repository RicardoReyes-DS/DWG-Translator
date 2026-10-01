using System.Security.Cryptography;
using System.Text;

namespace DwgTranslator.Agent;

public static class AgentHostOperations
{
    public const string Health = "health.read";
    public const string Capabilities = "capabilities.read";
    public const string JobsList = "jobs.list";
    public const string JobsGet = "jobs.get";
    public const string CadInspectionPlan = "cad.inspect.plan";
    public const string CadInspect = "cad.inspect";
    public const string InvariantDiffPlan = "cad.invariant-diff.plan";
    public const string InvariantDiffRun = "cad.invariant-diff.run";
    public const string TranslationPlan = "translation.plan";
    public const string TranslationPrepare = "translation.prepare";
    public const string TranslationReviewGet = "translation.review.get";
    public const string TranslationReviewApply = "translation.review.apply";
    public const string GenerationPlan = "generation.plan";
    public const string GenerationReconcilePlan = "generation.reconcile.plan";
    public const string GenerationReconcileApply = "generation.reconcile.apply";
    public const string Generate = "generation.execute";
    public const string JobStatus = "workflow.job.status";
    public const string JobNextAction = "workflow.job.next-action";
    public const string WorkflowCancel = "workflow.cancel";
    public const string BatchPlan = "batch.plan";
    public const string BatchStart = "batch.start";
    public const string BatchStatus = "batch.status";
    public const string BatchNextAction = "batch.next-action";
    public const string BatchReviewSummary = "batch.review-summary";
    public const string BatchApproveAndGenerate = "batch.approve-generate";
    public const string BatchReport = "batch.report";
    public const string BatchCancel = "batch.cancel";
    public const string BatchRecoveryPlan = "batch.recovery-plan";
    public const string BatchRecoveryStart = "batch.recovery-start";
    public const string BatchReconcileReview = "batch.reconcile-review";

    public static IReadOnlySet<string> ReadOnly { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Health, Capabilities, JobsList, JobsGet, CadInspectionPlan, CadInspect, InvariantDiffPlan, InvariantDiffRun,
        TranslationPlan, TranslationReviewGet, GenerationPlan, GenerationReconcilePlan, JobStatus, JobNextAction,
        BatchPlan, BatchStatus, BatchNextAction, BatchReviewSummary, BatchReport, BatchRecoveryPlan
    };

    public static IReadOnlySet<string> Write { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        TranslationPrepare, TranslationReviewApply, GenerationReconcileApply, Generate, WorkflowCancel,
        BatchStart, BatchApproveAndGenerate, BatchCancel, BatchRecoveryStart, BatchReconcileReview
    };

    public static IReadOnlySet<string> All { get; } = ReadOnly.Concat(Write).ToHashSet(StringComparer.Ordinal);
}

public sealed class AgentHostPolicy
{
    private readonly IReadOnlySet<string> _allowed;

    public AgentHostPolicy(IEnumerable<string>? configuredOperations)
    {
        var requested = configuredOperations?.ToHashSet(StringComparer.Ordinal) ?? AgentHostOperations.ReadOnly;
        if (requested.Count == 0 || requested.Any(operation => !AgentHostOperations.All.Contains(operation)))
            throw new ArgumentException("AGENT_ALLOWLIST_INVALID", nameof(configuredOperations));
        _allowed = requested;
    }

    public bool Allows(string operation) => _allowed.Contains(operation);
    public IReadOnlyList<string> AllowedOperations => _allowed.Order(StringComparer.Ordinal).ToArray();
}

public static class AgentBearerAuthenticator
{
    public static bool Validate(string? presentedToken, string expectedToken)
    {
        if (string.IsNullOrEmpty(presentedToken) || string.IsNullOrEmpty(expectedToken) ||
            presentedToken.Length > 512 || expectedToken.Length > 512)
            return false;
        var presented = Encoding.UTF8.GetBytes(presentedToken);
        var expected = Encoding.UTF8.GetBytes(expectedToken);
        try
        {
            return presented.Length == expected.Length && CryptographicOperations.FixedTimeEquals(presented, expected);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(presented);
            CryptographicOperations.ZeroMemory(expected);
        }
    }
}

public enum IdempotencyClaimResult
{
    Acquired,
    Duplicate,
    Conflict
}

public sealed class FileIdempotencyRegistry
{
    private readonly string _root;

    public FileIdempotencyRegistry(string logRoot) =>
        _root = Path.Combine(Path.GetFullPath(logRoot), "idempotency");

    public async Task<IdempotencyClaimResult> ClaimAsync(string key, string requestHash, CancellationToken cancellationToken)
    {
        if (!ValidHex(requestHash) || string.IsNullOrWhiteSpace(key) || key.Length > 200)
            throw new ArgumentException("AGENT_IDEMPOTENCY_INPUT_INVALID");
        Directory.CreateDirectory(_root);
        var keyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        var path = Path.Combine(_root, keyHash + ".claim");
        try
        {
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: false);
            await writer.WriteAsync(requestHash.AsMemory(), cancellationToken);
            return IdempotencyClaimResult.Acquired;
        }
        catch (IOException) when (File.Exists(path))
        {
            var existing = await File.ReadAllTextAsync(path, cancellationToken);
            return string.Equals(existing, requestHash, StringComparison.OrdinalIgnoreCase)
                ? IdempotencyClaimResult.Duplicate
                : IdempotencyClaimResult.Conflict;
        }
    }

    private static bool ValidHex(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
}
