using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using DwgTranslator.Application;
using DwgTranslator.Contracts;

namespace DwgTranslator.Infrastructure.Local;

public sealed class LocalJobStore : IJobStore
{
    internal static readonly FileShare JobDocumentReadShare = FileShare.ReadWrite | FileShare.Delete;
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> JobLocks = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly WorkspacePaths _paths;

    public LocalJobStore(WorkspacePaths paths) => _paths = paths;

    public Task<Result<JobDocument>> CreateAsync(JobDocument document, CancellationToken cancellationToken) =>
        WithJobLockAsync(document.JobId, async () =>
        {
            if (document.JobId == Guid.Empty || document.Version != 0)
                return Fail<JobDocument>("JOB_CREATE_INVALID", "New jobs require a non-empty ID and version zero.");
            if (SensitiveFieldPolicy.ContainsForbidden(document.Data))
                return Fail<JobDocument>("JOB_SECRET_FIELD_REJECTED", "Credential-like fields cannot be persisted.");
            var path = _paths.Resolve(document.JobId, "job.json");
            if (File.Exists(path))
                return Fail<JobDocument>("JOB_ALREADY_EXISTS", "The job already exists.");
            await WriteJsonAsync(path, document, cancellationToken);
            return Results.Success(document);
        }, cancellationToken);

    public async Task<Result<JobDocument>> LoadAsync(Guid jobId, CancellationToken cancellationToken)
    {
        if (jobId == Guid.Empty)
            return Fail<JobDocument>("JOB_ID_EMPTY", "jobId is required.");
        var path = _paths.Resolve(jobId, "job.json");
        if (!File.Exists(path))
            return Fail<JobDocument>("JOB_NOT_FOUND", "The job does not exist.");
        try
        {
            // Keep status readers compatible with the atomic replacement used by SaveAsync.
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                JobDocumentReadShare, 4096, FileOptions.Asynchronous);
            var document = await JsonSerializer.DeserializeAsync<JobDocument>(stream, JsonOptions, cancellationToken);
            return document is null
                ? Fail<JobDocument>("JOB_DOCUMENT_INVALID", "The job document is empty.")
                : Results.Success(document);
        }
        catch (JsonException)
        {
            return Fail<JobDocument>("JOB_DOCUMENT_INVALID", "The job document is invalid.");
        }
    }

    public Task<Result<JobDocument>> SaveAsync(JobDocument document, long expectedVersion, CancellationToken cancellationToken) =>
        WithJobLockAsync(document.JobId, async () =>
        {
            var loaded = await LoadAsync(document.JobId, cancellationToken);
            if (!loaded.IsSuccess)
                return loaded;
            if (loaded.Value!.Version != expectedVersion || document.Version != expectedVersion + 1)
                return Fail<JobDocument>("JOB_VERSION_CONFLICT", "The job version changed concurrently.");
            if (SensitiveFieldPolicy.ContainsForbidden(document.Data))
                return Fail<JobDocument>("JOB_SECRET_FIELD_REJECTED", "Credential-like fields cannot be persisted.");
            await WriteJsonAsync(_paths.Resolve(document.JobId, "job.json"), document, cancellationToken);
            return Results.Success(document);
        }, cancellationToken);

    public Task<Result<JobCheckpoint>> SaveCheckpointAsync(JobCheckpoint checkpoint, CancellationToken cancellationToken) =>
        WithJobLockAsync(checkpoint.JobId, async () =>
        {
            var loaded = await LoadAsync(checkpoint.JobId, cancellationToken);
            if (!loaded.IsSuccess)
                return Fail<JobCheckpoint>(loaded.Error!.Code, loaded.Error.Message);
            if (loaded.Value!.Version != checkpoint.ExpectedVersion || checkpoint.SafeState is Domain.JobState.Writing or Domain.JobState.Validating)
                return Fail<JobCheckpoint>("CHECKPOINT_UNSAFE_OR_STALE", "Checkpoint state or version is unsafe.");
            if (SensitiveFieldPolicy.ContainsForbidden(checkpoint.Data))
                return Fail<JobCheckpoint>("JOB_SECRET_FIELD_REJECTED", "Credential-like fields cannot be persisted.");
            var path = _paths.Resolve(checkpoint.JobId, Path.Combine("checkpoints", $"{checkpoint.ExpectedVersion:D12}.json"));
            var bytes = JsonSerializer.SerializeToUtf8Bytes(checkpoint, JsonOptions);
            if (File.Exists(path))
            {
                var existing = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                return existing.AsSpan().SequenceEqual(bytes)
                    ? Results.Success(checkpoint)
                    : Fail<JobCheckpoint>("CHECKPOINT_CONFLICT", "Checkpoint version already exists with different evidence.");
            }
            await AtomicFileWriter.WriteAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            return Results.Success(checkpoint);
        }, cancellationToken);

    public Task<Result<bool>> AppendAuditAsync(AuditRecord record, CancellationToken cancellationToken) =>
        WithJobLockAsync(record.JobId, async () =>
        {
            if (record.EventId == Guid.Empty || record.JobId == Guid.Empty || record.CorrelationId == Guid.Empty || record.TimestampUtc.Offset != TimeSpan.Zero)
                return Fail<bool>("AUDIT_RECORD_INVALID", "Audit IDs and UTC timestamp are required.");
            var path = _paths.Resolve(record.JobId, Path.Combine("audit", $"{record.TimestampUtc.UtcTicks:D19}-{record.EventId:N}.json"));
            var safeRecord = record with { Data = DiagnosticRedactor.Redact(record.Data) };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(safeRecord, JsonOptions);
            if (File.Exists(path))
            {
                var existing = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                return existing.AsSpan().SequenceEqual(bytes)
                    ? Results.Success(false)
                    : Fail<bool>("AUDIT_RECORD_CONFLICT", "Audit identity already exists with different evidence.");
            }
            await AtomicFileWriter.WriteAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            return Results.Success(true);
        }, cancellationToken);

    public Task<Result<int>> DeleteExpiredAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken) =>
        Task.FromResult(Fail<int>("RETENTION_DELETE_REQUIRES_EXPLICIT_POLICY", "Retention deletion is not enabled in Phase 1."));

    private static ContractError Error(string code, string message) =>
        new(code, ErrorCategory.Storage, message, false);

    private static Result<T> Fail<T>(string code, string message) => Results.Failure<T>(Error(code, message));

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        await AtomicFileWriter.WriteAsync(path, bytes, cancellationToken);
    }

    private async Task<Result<T>> WithJobLockAsync<T>(Guid jobId, Func<Task<Result<T>>> action, CancellationToken cancellationToken)
    {
        if (jobId == Guid.Empty)
            return Fail<T>("JOB_ID_EMPTY", "jobId is required.");
        var gate = JobLocks.GetOrAdd(jobId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_paths.JobRoot(jobId));
            var lockPath = _paths.Resolve(jobId, ".job.lock");
            FileStream processLock;
            try
            {
                processLock = await AcquireProcessLockAsync(lockPath, cancellationToken);
            }
            catch (IOException)
            {
                return Fail<T>("JOB_LOCK_UNAVAILABLE", "Another process owns the job lock.");
            }
            await using (processLock)
            {
                return await action();
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<FileStream> AcquireProcessLockAsync(string path, CancellationToken cancellationToken)
    {
        const int attempts = 50;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous | FileOptions.WriteThrough);
            }
            catch (IOException) when (attempt < attempts - 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
            }
        }

        throw new IOException("JOB_LOCK_UNAVAILABLE");
    }
}
