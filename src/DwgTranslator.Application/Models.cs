using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DwgTranslator.Contracts;
using DwgTranslator.Domain;

namespace DwgTranslator.Application;

public sealed record SecretReference
{
    private SecretReference(string value) => Value = value;
    public string Value { get; }

    public static DomainResult<SecretReference> Create(string? value) =>
        value is not null && value.StartsWith("credential-manager:dwg-translator/", StringComparison.Ordinal) && value.Length <= 160
            ? DomainResults.Success(new SecretReference(value))
            : DomainResults.Failure<SecretReference>("SECRET_REFERENCE_INVALID");
}

public sealed record JobDocument(
    Guid JobId,
    JobState State,
    long Version,
    DateTimeOffset UpdatedAtUtc,
    JsonObject Data);

public sealed record JobCheckpoint(
    Guid JobId,
    JobState SafeState,
    long ExpectedVersion,
    DateTimeOffset CreatedAtUtc,
    string SourceHash,
    string ConfigurationHash,
    string ContractVersion,
    JsonObject Data);

public sealed record AuditRecord(
    Guid EventId,
    Guid JobId,
    Guid CorrelationId,
    DateTimeOffset TimestampUtc,
    string EventType,
    JsonObject Data);

public sealed record CadCapabilities(string ProtocolVersion, string HostVersion, IReadOnlyList<string> Operations);

public sealed record TranslationReviewSnapshot(
    Guid JobId,
    long Version,
    string TargetLanguage,
    string PromptTemplateVersion,
    int CompletedBatches,
    int TotalBatches,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<ReviewRowSnapshot> Rows,
    int UsedInputTokens = 0,
    int UsedOutputTokens = 0,
    string? RequestedMode = null,
    string? BaseModel = null,
    string? RoutingVersion = null,
    IReadOnlyList<TranslationCallTrace>? RoutingCalls = null,
    IReadOnlyList<TranslationSegmentTrace>? RoutingSegments = null,
    int UsedProviderRequests = 0,
    string? ContextPolicyVersion = null,
    string? ContextHash = null,
    ReviewAutomationReceipt? ReviewAutomationReceipt = null);

public static class ReviewAutomationPolicy
{
    public const string ContextualAgentCreateNew = "contextual-agent-review-create-new/1.0";
}

/// <summary>
/// Redacted authority proving that an automated review was independently reviewed and QA-bound.
/// Only identifiers and SHA-256 fingerprints are persisted; reports and drawing text stay outside
/// the durable review snapshot.
/// </summary>
public sealed record ReviewAutomationReceipt(
    string PolicyVersion,
    Guid BatchId,
    string ManifestHash,
    string ContextHash,
    string ReviewerReportHash,
    string QaReportHash,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<SegmentContextResolutionAuthority>? SegmentContextResolutions = null);

public sealed record SegmentContextResolutionAuthority(
    string PolicyVersion,
    string SegmentId,
    string SourceTextHash,
    string ContextHash,
    string FinalTextHash,
    string Signal,
    string Resolution,
    string ReviewerReportHash,
    string QaReportHash,
    string AuthorityHash);

public sealed record TranslationReviewFingerprint(
    long Version,
    string ReviewHash,
    string? ContextHash,
    string? ReceiptHash)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static TranslationReviewFingerprint Create(TranslationReviewSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new(snapshot.Version, Hash(JsonSerializer.SerializeToUtf8Bytes(snapshot, Json)), snapshot.ContextHash,
            snapshot.ReviewAutomationReceipt is null ? null :
                Hash(JsonSerializer.SerializeToUtf8Bytes(snapshot.ReviewAutomationReceipt, Json)));
    }

    private static string Hash(byte[] value) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
}

public sealed record ReviewRowSnapshot(
    string SegmentId,
    string OriginalText,
    string ProposedText,
    string FinalText,
    SegmentState State,
    string? ExclusionReason,
    string? WarningCode,
    string RiskSeverity = "none",
    string? EffectiveModel = null,
    bool Escalated = false);

public sealed record DwgTranslationJobSpecification(
    string SourcePath,
    string OutputPath,
    string SourceHash,
    string? SourceLanguage,
    string TargetLanguage,
    string PromptTemplateVersion,
    IReadOnlyList<TranslationGlossaryEntry>? Glossary,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TranslationRoutingPolicy? Routing = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReviewAutomationScope? ReviewAutomationScope = null);

public sealed record ReviewAutomationScope(
    Guid BatchId,
    string ManifestHash,
    string PolicyVersion);

/// <summary>
/// Internal, recovery-only authority for rebinding an adopted job.  Both scopes
/// and the immutable file binding are derived from the sealed recovery plan;
/// this record is never accepted from an HTTP request.
/// </summary>
public sealed record ReviewAutomationScopeTransition(
    ReviewAutomationScope ExpectedScope,
    ReviewAutomationScope TargetScope,
    string SourcePath,
    string OutputPath,
    string SourceHash,
    string ContextPolicyVersion);

/// <summary>
/// Internal durable seal for the exact review observed by a recovery lineage CAS.
/// It is not accepted from an HTTP request and contains hashes rather than drawing text.
/// </summary>
public sealed record RecoveryReviewScopeSeal(
    ReviewAutomationScope ExpectedScope,
    ReviewAutomationScope TargetScope,
    long TransitionJobVersion,
    long TransitionReviewVersion,
    string TransitionReviewHash,
    string? TransitionContextHash,
    long BoundReviewVersion,
    string BoundReviewHash,
    string? BoundContextHash);

/// <summary>
/// Internal, redacted marker written by the generation-reconciliation CAS.  It binds the
/// original failed version and evidence without retaining paths, messages or drawing text.
/// </summary>
public sealed record GenerationFailureReconciliationReceipt(
    long FailedJobVersion,
    string FailureCode,
    string ReconciliationBindingHash,
    long ApprovedCheckpointVersion,
    string ApprovedCheckpointHash,
    long ReviewVersion,
    string ReviewHash,
    int DecisionCount,
    string EvidenceHash);

public sealed record DwgTranslationJobData(
    DwgTranslationJobSpecification Specification,
    string ConfigurationHash,
    CadInspectResponsePayload? Inspection,
    IReadOnlyList<CadTextSegment>? Segments,
    string? OutputHash,
    CadExerciseValidationReport? ValidationReport = null,
    long OutputBytes = 0,
    RecoveryReviewScopeSeal? RecoveryReviewScopeSeal = null,
    GenerationFailureReconciliationReceipt? GenerationFailureReconciliationReceipt = null,
    ApprovedContextRevalidationReceipt? ApprovedContextRevalidationReceipt = null);

public sealed record ApprovedContextRevalidationReceipt(
    string BindingHash,
    long ApprovedJobVersion,
    long ReopenedJobVersion,
    long PreviousReviewVersion,
    long ReopenedReviewVersion,
    string PreviousReviewHash,
    string PreviousReceiptHash,
    string PreviousContextHash,
    string ReopenedContextHash,
    string PreviousContextPolicyVersion,
    string ReopenedContextPolicyVersion,
    IReadOnlyList<string> RequiredResolutionSegmentIds);

public sealed record ApprovedContextRevalidationBinding(
    long ExpectedJobVersion,
    string ExpectedJobArtifactHash,
    long ExpectedReviewVersion,
    string ExpectedReviewHash,
    string ExpectedReceiptHash,
    string ExpectedContextPolicyVersion,
    string ExpectedContextHash,
    string TargetContextPolicyVersion,
    string TargetContextHash,
    string ManifestBoundBasename,
    int RowCount,
    int ExpectedFallbackCandidateCount,
    int ExpectedLocalEvidenceCount,
    IReadOnlyList<string> RequiredResolutionSegmentIds,
    string ConflictSetHash,
    string BindingHash);

public sealed record ApprovedContextRevalidationTransition(
    ReviewAutomationScope ExpectedScope,
    ReviewAutomationScope TargetScope,
    string SourcePath,
    string OutputPath,
    string SourceHash,
    ApprovedContextRevalidationBinding Binding);

public sealed record CadExerciseValidationReport(
    string Policy,
    bool AutomaticPass,
    bool VisualReviewRequired,
    int EntityCount,
    int BoundsChangedCount,
    string SourceHashAfter,
    string OutputHash,
    IReadOnlyList<CadEntityValidationSummary> Entities);

public sealed record CadEntityValidationSummary(
    string SegmentId,
    string Handle,
    string EntityType,
    string Space,
    string? Layout,
    string Layer,
    string InvariantFingerprint,
    int PreservedPropertyCount,
    bool BoundsChanged,
    string BeforeExtents,
    string AfterExtents,
    string Result);

public sealed record ReviewDecisionInput(
    string SegmentId,
    string? FinalText,
    string? ExclusionReason);

public sealed record CadReadResult(
    CadInspectResponsePayload Inspection,
    IReadOnlyList<CadTextSegment> Segments,
    int ExcludedFieldCount);
