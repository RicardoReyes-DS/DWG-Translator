using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using DwgTranslator.Application;
using DwgTranslator.Contracts;
using DwgTranslator.Domain;

namespace DwgTranslator.Infrastructure.Local;

public sealed class LocalTranslationReviewStore : ITranslationReviewStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Gates = new();
    private readonly WorkspacePaths _paths;

    public LocalTranslationReviewStore(WorkspacePaths paths) => _paths = paths;

    public Task<Result<TranslationReviewSnapshot>> SaveAsync(TranslationReviewSnapshot snapshot, CancellationToken cancellationToken) =>
        SaveAsync(snapshot, snapshot.Version - 1, cancellationToken);

    public async Task<Result<TranslationReviewSnapshot>> SaveAsync(
        TranslationReviewSnapshot snapshot,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        var validation = Validate(snapshot);
        if (!validation.IsSuccess) return validation;
        if (snapshot.Version != expectedVersion + 1)
            return Failure("REVIEW_VERSION_CONFLICT", "Review snapshot version is not the expected next durable version.");
        var path = _paths.Resolve(snapshot.JobId, Path.Combine("review", "session.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var gate = Gates.GetOrAdd(snapshot.JobId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var processLock = await AcquireLockAsync(
                _paths.Resolve(snapshot.JobId, Path.Combine("review", ".session.lock")), cancellationToken);
            if (File.Exists(path))
            {
                var current = await LoadCoreAsync(path, cancellationToken);
                if (!current.IsSuccess) return current;
                if (current.Value!.Version != expectedVersion)
                    return Failure("REVIEW_VERSION_CONFLICT", "The durable review changed since it was read.");
            }
            else if (expectedVersion != -1 || snapshot.Version != 0)
            {
                return Failure("REVIEW_VERSION_CONFLICT", "The expected durable review version does not exist.");
            }
            await AtomicFileWriter.WriteAsync(path, JsonSerializer.SerializeToUtf8Bytes(snapshot, Json), cancellationToken);
            return Results.Success(snapshot);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<Result<TranslationReviewSnapshot>> LoadAsync(Guid jobId, CancellationToken cancellationToken)
    {
        if (jobId == Guid.Empty) return Failure("JOB_ID_EMPTY", "jobId is required.");
        var path = _paths.Resolve(jobId, Path.Combine("review", "session.json"));
        if (!File.Exists(path)) return Failure("REVIEW_NOT_FOUND", "The review session does not exist.");
        return await LoadCoreAsync(path, cancellationToken);
    }

    private static async Task<Result<TranslationReviewSnapshot>> LoadCoreAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            var snapshot = await JsonSerializer.DeserializeAsync<TranslationReviewSnapshot>(stream, Json, cancellationToken);
            return snapshot is null ? Failure("REVIEW_DOCUMENT_INVALID", "Review session is empty.") : Validate(snapshot);
        }
        catch (JsonException) { return Failure("REVIEW_DOCUMENT_INVALID", "Review session is invalid."); }
    }

    private static async Task<FileStream> AcquireLockAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous | FileOptions.WriteThrough); }
            catch (IOException) when (attempt < 49) { await Task.Delay(20, cancellationToken); }
        }
        throw new IOException("REVIEW_LOCK_UNAVAILABLE");
    }

    private static Result<TranslationReviewSnapshot> Validate(TranslationReviewSnapshot snapshot)
    {
        if (snapshot.JobId == Guid.Empty || snapshot.Version < 0 || snapshot.UpdatedAtUtc.Offset != TimeSpan.Zero ||
            !LanguageTag.Create(snapshot.TargetLanguage).IsSuccess || string.IsNullOrWhiteSpace(snapshot.PromptTemplateVersion) || snapshot.PromptTemplateVersion.Length > 120 ||
            snapshot.TotalBatches < 1 || snapshot.CompletedBatches < 0 || snapshot.CompletedBatches > snapshot.TotalBatches || snapshot.Rows is null || snapshot.Rows.Count == 0 ||
            snapshot.UsedInputTokens < 0 || snapshot.UsedOutputTokens < 0 || snapshot.UsedProviderRequests < 0 ||
            ((snapshot.RequestedMode is null) != (snapshot.BaseModel is null) || (snapshot.RequestedMode is null) != (snapshot.RoutingVersion is null)) ||
            (snapshot.RequestedMode is not null && (snapshot.RoutingVersion != TranslationRouting.PolicyVersion || snapshot.BaseModel == "gpt-5.6" ||
                snapshot.RoutingCalls is null || snapshot.RoutingSegments is null || snapshot.RoutingSegments.Count != snapshot.Rows.Count)) ||
            !ValidContextMetadata(snapshot) ||
            snapshot.Rows.Select(row => row.SegmentId).Distinct(StringComparer.Ordinal).Count() != snapshot.Rows.Count)
            return Failure("REVIEW_SNAPSHOT_INVALID", "Review snapshot metadata is invalid.");
        foreach (var row in snapshot.Rows)
        {
            if (!ContractPatterns.SegmentId().IsMatch(row.SegmentId) || row.OriginalText is null || row.ProposedText is null || row.FinalText is null ||
                row.OriginalText.Length > 65_535 || row.ProposedText.Length > 65_535 || row.FinalText.Length > 65_535 ||
                row.RiskSeverity is not ("none" or "low" or "medium" or "high") || row.WarningCode is { Length: > 2_000 } ||
                row.EffectiveModel is { Length: > 120 } || row.EffectiveModel == "gpt-5.6" ||
                (row.State == SegmentState.Excluded && string.IsNullOrWhiteSpace(row.ExclusionReason)) ||
                (row.State != SegmentState.Excluded && row.ExclusionReason is not null))
                return Failure("REVIEW_ROW_INVALID", "A persisted review row is invalid.");
        }
        return Results.Success(snapshot);
    }

    private static bool ValidContextMetadata(TranslationReviewSnapshot snapshot)
    {
        if (snapshot.ContextPolicyVersion is null && snapshot.ContextHash is null)
            return snapshot.ReviewAutomationReceipt is null;
        if (!CadSemanticContextBuilder.IsSupportedPolicyVersion(snapshot.ContextPolicyVersion) ||
            !ContractPatterns.Sha256().IsMatch(snapshot.ContextHash ?? string.Empty))
            return false;
        var receipt = snapshot.ReviewAutomationReceipt;
        return receipt is null ||
            receipt.PolicyVersion == ReviewAutomationPolicy.ContextualAgentCreateNew &&
            receipt.BatchId != Guid.Empty &&
            string.Equals(receipt.ContextHash, snapshot.ContextHash, StringComparison.Ordinal) &&
            ContractPatterns.Sha256().IsMatch(receipt.ManifestHash) &&
            ContractPatterns.Sha256().IsMatch(receipt.ReviewerReportHash) &&
            ContractPatterns.Sha256().IsMatch(receipt.QaReportHash) &&
            (receipt.SegmentContextResolutions is null ||
             receipt.SegmentContextResolutions.Count <= 1 &&
             receipt.SegmentContextResolutions.All(SegmentContextResolutionPolicy.IsStructurallyValid));
    }

    private static Result<TranslationReviewSnapshot> Failure(string code, string message) =>
        Results.Failure<TranslationReviewSnapshot>(new ContractError(code, ErrorCategory.Storage, message, false));
}
