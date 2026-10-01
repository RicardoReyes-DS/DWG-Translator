using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DwgTranslator.Application;
using DwgTranslator.Contracts;
using DwgTranslator.Domain;
using DwgTranslator.Infrastructure.Local;

namespace DwgTranslator.Shell;

public sealed record DesktopJobSummary(
    Guid JobId,
    JobState State,
    DateTimeOffset UpdatedAtUtc,
    string SourcePath,
    string OutputPath,
    string? FailureCode,
    bool CanResume);

public sealed record DesktopJobResume(DesktopReviewSession? Review, DesktopCompletion? Completion);
public sealed record DesktopRetentionPreview(int RetentionDays, IReadOnlyList<Guid> JobIds, long Bytes);

public interface IDesktopJobAdministration
{
    Task<Result<IReadOnlyList<DesktopJobSummary>>> ListAsync(CancellationToken cancellationToken);
    Task<Result<DesktopJobResume>> ResumeAsync(Guid jobId, CancellationToken cancellationToken);
    Task<Result<DesktopRetentionPreview>> PreviewRetentionAsync(int retentionDays, CancellationToken cancellationToken);
    Task<Result<int>> DeleteExpiredAsync(int retentionDays, bool explicitlyConfirmed, CancellationToken cancellationToken);
}

public sealed class LocalDesktopJobAdministration : IDesktopJobAdministration
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    private readonly WorkspacePaths _paths;
    private readonly IJobStore _jobs;
    private readonly ITranslationReviewStore _reviews;
    private readonly DwgTranslationJobCoordinator _coordinator;
    private readonly IClock _clock;

    public LocalDesktopJobAdministration(
        WorkspacePaths paths,
        IJobStore jobs,
        ITranslationReviewStore reviews,
        DwgTranslationJobCoordinator coordinator,
        IClock clock)
    {
        _paths = paths;
        _jobs = jobs;
        _reviews = reviews;
        _coordinator = coordinator;
        _clock = clock;
    }

    public async Task<Result<IReadOnlyList<DesktopJobSummary>>> ListAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!Directory.Exists(_paths.Root)) return Results.Success<IReadOnlyList<DesktopJobSummary>>([]);
            var summaries = new List<DesktopJobSummary>();
            foreach (var directory in Directory.EnumerateDirectories(_paths.Root).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 ||
                    !Guid.TryParse(Path.GetFileName(directory), out var jobId) || jobId == Guid.Empty)
                    continue;
                var loaded = await _jobs.LoadAsync(jobId, cancellationToken).ConfigureAwait(false);
                if (!loaded.IsSuccess) continue;
                var parsed = Parse(loaded.Value!);
                if (parsed is not null) summaries.Add(parsed);
            }
            return Results.Success<IReadOnlyList<DesktopJobSummary>>(summaries.OrderByDescending(item => item.UpdatedAtUtc).ToArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failure<IReadOnlyList<DesktopJobSummary>>("JOB_LIST_UNAVAILABLE", "The local job list could not be read.");
        }
    }

    public async Task<Result<DesktopJobResume>> ResumeAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var loaded = await _jobs.LoadAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess) return Results.Failure<DesktopJobResume>(loaded.Error!);
        var current = loaded.Value!;
        var data = Deserialize(current);
        if (!data.IsSuccess) return Results.Failure<DesktopJobResume>(data.Error!);
        var jobData = data.Value!;

        if (IsPartialTranslationRecovery(current))
        {
            if (!File.Exists(jobData.Specification.SourcePath) || File.Exists(jobData.Specification.OutputPath))
                return Failure<DesktopJobResume>("RECOVERY_PATH_STATE_INVALID", "The source or output path is no longer safe for translation recovery.");
            var partialSourceHash = await HashAsync(jobData.Specification.SourcePath, cancellationToken).ConfigureAwait(false);
            if (!partialSourceHash.IsSuccess) return Results.Failure<DesktopJobResume>(partialSourceHash.Error!);
            if (!string.Equals(partialSourceHash.Value, jobData.Specification.SourceHash, StringComparison.Ordinal))
                return Failure<DesktopJobResume>("RECOVERY_SOURCE_CHANGED", "The source drawing changed after extraction.");
            var resumed = await _coordinator.ResumeTranslationAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (!resumed.IsSuccess) return Results.Failure<DesktopJobResume>(resumed.Error!);
            if (resumed.Value!.State != JobState.ReviewRequired)
                return Failure<DesktopJobResume>("RECOVERY_TRANSLATION_INCOMPLETE", "Translation recovery did not reach human review.");
            var recoveredReview = await BuildReviewAsync(jobId, jobData, cancellationToken).ConfigureAwait(false);
            return recoveredReview.IsSuccess
                ? Results.Success(new DesktopJobResume(recoveredReview.Value, null))
                : Results.Failure<DesktopJobResume>(recoveredReview.Error!);
        }

        if (current.State == JobState.ReviewRequired)
        {
            var review = await BuildReviewAsync(current.JobId, jobData, cancellationToken).ConfigureAwait(false);
            return review.IsSuccess
                ? Results.Success(new DesktopJobResume(review.Value, null))
                : Results.Failure<DesktopJobResume>(review.Error!);
        }

        if (current.State == JobState.Completed && jobData.OutputHash is not null && File.Exists(jobData.Specification.OutputPath))
            return Results.Success(new DesktopJobResume(null, new DesktopCompletion(jobData.Specification.OutputPath, jobData.OutputHash, jobData.ValidationReport)));

        if (current.State == JobState.Approved)
            return await GenerateAsync(current.JobId, jobData, cancellationToken).ConfigureAwait(false);

        if (current.State != JobState.Failed || !RecoverableFailure(current))
            return Failure<DesktopJobResume>("RECOVERY_JOB_NOT_RETRYABLE", "The selected job has no safe resume point.");

        if (!File.Exists(jobData.Specification.SourcePath) || File.Exists(jobData.Specification.OutputPath))
            return Failure<DesktopJobResume>("RECOVERY_PATH_STATE_INVALID", "The source or output path is no longer safe for recovery.");
        var candidate = CandidatePath(jobData.Specification.OutputPath, jobId);
        if (File.Exists(candidate))
            return Failure<DesktopJobResume>("RECOVERY_CANDIDATE_REQUIRES_INSPECTION", "A previous candidate requires explicit inspection before retrying.");

        var actualSourceHash = await HashAsync(jobData.Specification.SourcePath, cancellationToken).ConfigureAwait(false);
        if (!actualSourceHash.IsSuccess) return Results.Failure<DesktopJobResume>(actualSourceHash.Error!);
        if (!string.Equals(actualSourceHash.Value, jobData.Specification.SourceHash, StringComparison.Ordinal))
            return Failure<DesktopJobResume>("RECOVERY_SOURCE_CHANGED", "The source drawing changed after the original review.");

        var checkpoint = await LatestApprovedCheckpointAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (!checkpoint.IsSuccess) return Results.Failure<DesktopJobResume>(checkpoint.Error!);
        var source = ContentHash.Create(actualSourceHash.Value);
        var configuration = ContentHash.Create(jobData.ConfigurationHash);
        var checkpointSource = ContentHash.Create(checkpoint.Value!.SourceHash);
        var checkpointConfiguration = ContentHash.Create(checkpoint.Value.ConfigurationHash);
        if (!source.IsSuccess || !configuration.IsSuccess || !checkpointSource.IsSuccess || !checkpointConfiguration.IsSuccess ||
            !ResumePolicy.CanResume(current.State,
                new ResumeCheckpoint(checkpoint.Value.SafeState, checkpointSource.Value, checkpoint.Value.ContractVersion, checkpointConfiguration.Value),
                source.Value, ContractV1.SchemaVersion, configuration.Value))
            return Failure<DesktopJobResume>("RECOVERY_CHECKPOINT_MISMATCH", "The approved checkpoint no longer matches this job.");

        var reviewSnapshot = await _reviews.LoadAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (!reviewSnapshot.IsSuccess) return Results.Failure<DesktopJobResume>(reviewSnapshot.Error!);
        if (reviewSnapshot.Value!.Rows.Count == 0 || reviewSnapshot.Value.Rows.Any(row => row.State is not (SegmentState.Approved or SegmentState.Excluded)))
            return Failure<DesktopJobResume>("RECOVERY_REVIEW_INCOMPLETE", "The recovered review is incomplete.");

        var restoredData = current.Data.DeepClone().AsObject();
        restoredData.Remove("failure");
        var restored = current with { State = JobState.Approved, Version = current.Version + 1, UpdatedAtUtc = _clock.UtcNow, Data = restoredData };
        var saved = await _jobs.SaveAsync(restored, current.Version, cancellationToken).ConfigureAwait(false);
        if (!saved.IsSuccess) return Results.Failure<DesktopJobResume>(saved.Error!);
        var audit = await _jobs.AppendAuditAsync(new AuditRecord(
            Guid.NewGuid(), jobId, Guid.NewGuid(), _clock.UtcNow, "JobRecoveredFromApprovedCheckpoint",
            new JsonObject { ["checkpointVersion"] = checkpoint.Value.ExpectedVersion }), cancellationToken).ConfigureAwait(false);
        if (!audit.IsSuccess) return Results.Failure<DesktopJobResume>(audit.Error!);
        return await GenerateAsync(jobId, jobData, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<DesktopRetentionPreview>> PreviewRetentionAsync(int retentionDays, CancellationToken cancellationToken)
    {
        if (retentionDays is < 1 or > 365)
            return Failure<DesktopRetentionPreview>("RETENTION_DAYS_INVALID", "Retention days must be between 1 and 365.");
        var listed = await ListAsync(cancellationToken).ConfigureAwait(false);
        if (!listed.IsSuccess) return Results.Failure<DesktopRetentionPreview>(listed.Error!);
        var cutoff = _clock.UtcNow.AddDays(-retentionDays);
        var ids = listed.Value!
            .Where(job => job.UpdatedAtUtc < cutoff && job.State is JobState.Completed or JobState.Failed or JobState.Cancelled)
            .Select(job => job.JobId)
            .Where(IsSafeRetentionDirectory)
            .ToArray();
        long bytes = 0;
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bytes += Directory.EnumerateFiles(_paths.JobRoot(id), "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
        }
        return Results.Success(new DesktopRetentionPreview(retentionDays, ids, bytes));
    }

    public async Task<Result<int>> DeleteExpiredAsync(int retentionDays, bool explicitlyConfirmed, CancellationToken cancellationToken)
    {
        if (!explicitlyConfirmed)
            return Failure<int>("RETENTION_CONFIRMATION_REQUIRED", "Explicit operator confirmation is required.");
        var preview = await PreviewRetentionAsync(retentionDays, cancellationToken).ConfigureAwait(false);
        if (!preview.IsSuccess) return Results.Failure<int>(preview.Error!);
        foreach (var id in preview.Value!.JobIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsSafeRetentionDirectory(id))
                return Failure<int>("RETENTION_TREE_CHANGED", "A job directory changed after retention preview.");
        }
        foreach (var id in preview.Value.JobIds) Directory.Delete(_paths.JobRoot(id), recursive: true);
        return Results.Success(preview.Value.JobIds.Count);
    }

    private DesktopJobSummary? Parse(JobDocument document)
    {
        var data = Deserialize(document);
        if (!data.IsSuccess) return null;
        var failure = document.Data["failure"] as JsonObject;
        return new DesktopJobSummary(document.JobId, document.State, document.UpdatedAtUtc,
            data.Value!.Specification.SourcePath, data.Value.Specification.OutputPath,
            failure?["code"]?.GetValue<string>(),
            document.State is JobState.ReviewRequired or JobState.Approved or JobState.Completed ||
            IsPartialTranslationRecovery(document) ||
            document.State == JobState.Failed && RecoverableFailure(document));
    }

    private bool IsPartialTranslationRecovery(JobDocument document)
    {
        var failure = document.Data["failure"] as JsonObject;
        if (document.State != JobState.Translating &&
            !(document.State == JobState.Failed && failure?["stage"]?.GetValue<string>() is "Translation" &&
              failure["retryable"]?.GetValue<bool>() is true))
            return false;
        return File.Exists(_paths.Resolve(document.JobId, Path.Combine("review", "session.json")));
    }

    private async Task<Result<DesktopReviewSession>> BuildReviewAsync(Guid jobId, DwgTranslationJobData data, CancellationToken cancellationToken)
    {
        if (data.Segments is null)
            return Failure<DesktopReviewSession>("JOB_SEGMENTS_MISSING", "Extracted segment context is unavailable.");
        var review = await _reviews.LoadAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (!review.IsSuccess) return Results.Failure<DesktopReviewSession>(review.Error!);
        var segments = data.Segments.ToDictionary(segment => segment.SegmentId, StringComparer.Ordinal);
        if (review.Value!.Rows.Any(row => !segments.ContainsKey(row.SegmentId)))
            return Failure<DesktopReviewSession>("RECOVERY_REVIEW_MISMATCH", "The review no longer matches the extracted segments.");
        var rows = review.Value.Rows.Select(row =>
        {
            var segment = segments[row.SegmentId];
            var context = segment.Entity.Space == "PaperSpace"
                ? $"{segment.Entity.Layout ?? "Layout"} · {segment.Entity.Layer}"
                : $"ModelSpace · {segment.Entity.Layer}";
            return new DesktopReviewRow(row.SegmentId, row.OriginalText, row.ProposedText, row.FinalText, context, row.WarningCode);
        }).ToArray();
        return Results.Success(new DesktopReviewSession(jobId, rows));
    }

    private async Task<Result<DesktopJobResume>> GenerateAsync(Guid jobId, DwgTranslationJobData data, CancellationToken cancellationToken)
    {
        var completed = await _coordinator.GenerateAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (!completed.IsSuccess) return Results.Failure<DesktopJobResume>(completed.Error!);
        if (completed.Value!.State != JobState.Completed)
            return Failure<DesktopJobResume>("RECOVERY_JOB_NOT_COMPLETED", "The recovered job did not complete.");
        var final = Deserialize(completed.Value);
        return final.IsSuccess && final.Value!.OutputHash is not null
            ? Results.Success(new DesktopJobResume(null, new DesktopCompletion(final.Value.Specification.OutputPath, final.Value.OutputHash, final.Value.ValidationReport)))
            : Failure<DesktopJobResume>("COMPLETED_OUTPUT_MISSING", "The completed output metadata is unavailable.");
    }

    private async Task<Result<JobCheckpoint>> LatestApprovedCheckpointAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var directory = _paths.Resolve(jobId, "checkpoints");
        if (!Directory.Exists(directory)) return Failure<JobCheckpoint>("RECOVERY_CHECKPOINT_MISSING", "No checkpoint is available.");
        foreach (var path in Directory.EnumerateFiles(directory, "*.json").OrderByDescending(Path.GetFileName, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
                var checkpoint = await JsonSerializer.DeserializeAsync<JobCheckpoint>(stream, Json, cancellationToken).ConfigureAwait(false);
                if (checkpoint?.JobId == jobId && checkpoint.SafeState == JobState.Approved) return Results.Success(checkpoint);
            }
            catch (JsonException) { }
        }
        return Failure<JobCheckpoint>("RECOVERY_APPROVED_CHECKPOINT_MISSING", "No approved checkpoint is available.");
    }

    private bool IsSafeRetentionDirectory(Guid jobId)
    {
        var directory = _paths.JobRoot(jobId);
        if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return false;
        foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
            if (File.Exists(path) && Path.GetExtension(path).Equals(".dwg", StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static bool RecoverableFailure(JobDocument document)
    {
        var failure = document.Data["failure"] as JsonObject;
        var code = failure?["code"]?.GetValue<string>();
        return failure?["retryable"]?.GetValue<bool>() is true || code is "IPC_TIMEOUT" or "IPC_PEER_DISCONNECTED" or "GEOMETRY_INVARIANTS_CHANGED";
    }

    private static Result<DwgTranslationJobData> Deserialize(JobDocument document)
    {
        try
        {
            var payload = document.Data.DeepClone().AsObject();
            payload.Remove("failure");
            var data = payload.Deserialize<DwgTranslationJobData>(Json);
            return data is null
                ? Failure<DwgTranslationJobData>("JOB_DATA_INVALID", "The job data is unavailable.")
                : Results.Success(data);
        }
        catch (JsonException) { return Failure<DwgTranslationJobData>("JOB_DATA_INVALID", "The job data is invalid."); }
    }

    private static async Task<Result<string>> HashAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Results.Success("sha256:" + Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failure<string>("RECOVERY_SOURCE_UNREADABLE", "The source drawing cannot be read.");
        }
    }

    private static string CandidatePath(string finalPath, Guid jobId) =>
        Path.Combine(Path.GetDirectoryName(finalPath)!, $".{Path.GetFileNameWithoutExtension(finalPath)}.candidate-{jobId:N}{Path.GetExtension(finalPath)}");

    private static Result<T> Failure<T>(string code, string message) =>
        Results.Failure<T>(new ContractError(code, ErrorCategory.Storage, message, false));
}
