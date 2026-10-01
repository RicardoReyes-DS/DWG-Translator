using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DwgTranslator.Application;
using DwgTranslator.Contracts;
using DwgTranslator.Domain;

namespace DwgTranslator.Shell;

public enum VisualReviewDecision
{
    Pass,
    Fail
}

public sealed record VisualReviewSession(Guid SessionId, Guid JobId, string OutputHash, DateTimeOffset OpenedAtUtc, int AutoCadProcessId);
public sealed record VisualReviewReceipt(Guid EventId, Guid JobId, string OutputHash, VisualReviewDecision Decision, DateTimeOffset OpenedAtUtc, DateTimeOffset RecordedAtUtc, int AutoCadProcessId);

public interface IVisualReviewWorkflow
{
    Task<Result<VisualReviewSession>> OpenAsync(Guid jobId, string outputPath, string outputHash, CancellationToken cancellationToken);
    Task<Result<VisualReviewReceipt>> RecordAsync(Guid sessionId, VisualReviewDecision decision, CancellationToken cancellationToken);
}

public interface IVisualReviewProcessLauncher
{
    bool IsAutoCadRunning();
    Result<int> Start(string autoCadExecutablePath, string outputPath);
    bool IsProcessRunning(int processId);
}

public sealed class LocalVisualReviewWorkflow : IVisualReviewWorkflow
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false };
    private readonly IJobStore _jobs;
    private readonly IClock _clock;
    private readonly string _autoCadExecutablePath;
    private readonly IVisualReviewProcessLauncher _launcher;
    private readonly ConcurrentDictionary<Guid, PendingVisualReview> _pending = new();

    public LocalVisualReviewWorkflow(IJobStore jobs, IClock clock, string autoCadExecutablePath, IVisualReviewProcessLauncher? launcher = null)
    {
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _autoCadExecutablePath = Path.GetFullPath(autoCadExecutablePath ?? throw new ArgumentNullException(nameof(autoCadExecutablePath)));
        _launcher = launcher ?? new AutoCadVisualReviewProcessLauncher();
    }

    public async Task<Result<VisualReviewSession>> OpenAsync(Guid jobId, string outputPath, string outputHash, CancellationToken cancellationToken)
    {
        if (jobId == Guid.Empty || !Path.IsPathFullyQualified(outputPath) || !File.Exists(outputPath) ||
            !string.Equals(Path.GetExtension(outputPath), ".dwg", StringComparison.OrdinalIgnoreCase) ||
            IsReparsePointOrUnreadable(outputPath) ||
            string.IsNullOrEmpty(outputHash) || !ContractPatterns.Sha256().IsMatch(outputHash) || !File.Exists(_autoCadExecutablePath))
            return Failure<VisualReviewSession>("VISUAL_REVIEW_CONFIGURATION_INVALID", ErrorCategory.Configuration, "Visual review paths and identity are invalid.");
        if (_launcher.IsAutoCadRunning())
            return Failure<VisualReviewSession>("VISUAL_REVIEW_CAD_BUSY", ErrorCategory.Concurrency, "Close other AutoCAD sessions before starting the controlled visual review.");

        var job = await _jobs.LoadAsync(jobId, cancellationToken);
        if (!job.IsSuccess) return Results.Failure<VisualReviewSession>(job.Error!);
        var validated = ValidateCompletedJob(job.Value!, outputPath, outputHash);
        if (!validated.IsSuccess) return Results.Failure<VisualReviewSession>(validated.Error!);
        var actualHash = await HashAsync(outputPath, cancellationToken);
        if (!actualHash.IsSuccess) return Results.Failure<VisualReviewSession>(actualHash.Error!);
        if (!string.Equals(actualHash.Value, outputHash, StringComparison.Ordinal))
            return Failure<VisualReviewSession>("VISUAL_REVIEW_OUTPUT_CHANGED", ErrorCategory.Integrity, "The generated DWG changed before visual review.");

        var started = _launcher.Start(_autoCadExecutablePath, outputPath);
        if (!started.IsSuccess) return Results.Failure<VisualReviewSession>(started.Error!);
        var session = new VisualReviewSession(Guid.NewGuid(), jobId, outputHash, _clock.UtcNow, started.Value);
        if (!_pending.TryAdd(session.SessionId, new PendingVisualReview(session, outputPath)))
            return Failure<VisualReviewSession>("VISUAL_REVIEW_SESSION_CONFLICT", ErrorCategory.Concurrency, "A visual review session could not be reserved.");
        return Results.Success(session);
    }

    public async Task<Result<VisualReviewReceipt>> RecordAsync(Guid sessionId, VisualReviewDecision decision, CancellationToken cancellationToken)
    {
        if (sessionId == Guid.Empty || !Enum.IsDefined(decision) || !_pending.TryRemove(sessionId, out var pending))
            return Failure<VisualReviewReceipt>("VISUAL_REVIEW_SESSION_INVALID", ErrorCategory.Input, "Open the generated DWG through this application before recording a visual decision.");
        if (!_launcher.IsProcessRunning(pending.Session.AutoCadProcessId))
            return Failure<VisualReviewReceipt>("VISUAL_REVIEW_CAD_SESSION_ENDED", ErrorCategory.Integrity, "The controlled AutoCAD visual review session ended before the decision was recorded.");

        var actualHash = await HashAsync(pending.OutputPath, cancellationToken);
        if (!actualHash.IsSuccess) return Results.Failure<VisualReviewReceipt>(actualHash.Error!);
        if (!string.Equals(actualHash.Value, pending.Session.OutputHash, StringComparison.Ordinal))
            return Failure<VisualReviewReceipt>("VISUAL_REVIEW_OUTPUT_CHANGED", ErrorCategory.Integrity, "The generated DWG changed during visual review.");
        var job = await _jobs.LoadAsync(pending.Session.JobId, cancellationToken);
        if (!job.IsSuccess) return Results.Failure<VisualReviewReceipt>(job.Error!);
        var validated = ValidateCompletedJob(job.Value!, pending.OutputPath, pending.Session.OutputHash);
        if (!validated.IsSuccess) return Results.Failure<VisualReviewReceipt>(validated.Error!);

        var recordedAt = _clock.UtcNow;
        var eventId = Guid.NewGuid();
        var audit = new AuditRecord(eventId, pending.Session.JobId, pending.Session.SessionId, recordedAt, "VisualReviewRecorded", new JsonObject
        {
            ["decision"] = decision.ToString().ToUpperInvariant(),
            ["outputHash"] = pending.Session.OutputHash,
            ["openedAtUtc"] = pending.Session.OpenedAtUtc,
            ["recordedAtUtc"] = recordedAt,
            ["autoCadProcessId"] = pending.Session.AutoCadProcessId,
            ["automaticValidation"] = "PASS",
            ["operatorConfirmed"] = true
        });
        var appended = await _jobs.AppendAuditAsync(audit, cancellationToken);
        if (!appended.IsSuccess) return Results.Failure<VisualReviewReceipt>(appended.Error!);
        if (!appended.Value)
            return Failure<VisualReviewReceipt>("VISUAL_REVIEW_RECEIPT_CONFLICT", ErrorCategory.Storage, "The visual review receipt already exists.");
        return Results.Success(new VisualReviewReceipt(eventId, pending.Session.JobId, pending.Session.OutputHash, decision,
            pending.Session.OpenedAtUtc, recordedAt, pending.Session.AutoCadProcessId));
    }

    private static Result<bool> ValidateCompletedJob(JobDocument job, string outputPath, string outputHash)
    {
        if (job.State != JobState.Completed)
            return Failure<bool>("VISUAL_REVIEW_JOB_NOT_COMPLETED", ErrorCategory.Integrity, "Automatic CAD validation must complete before visual review.");
        try
        {
            var data = job.Data.Deserialize<DwgTranslationJobData>(Json);
            if (data?.ValidationReport is null || !data.ValidationReport.AutomaticPass || !data.ValidationReport.VisualReviewRequired ||
                !string.Equals(data.OutputHash, outputHash, StringComparison.Ordinal) ||
                !string.Equals(Path.GetFullPath(data.Specification.OutputPath), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
                return Failure<bool>("VISUAL_REVIEW_JOB_EVIDENCE_INVALID", ErrorCategory.Integrity, "The completed job is not bound to this validated output.");
            return Results.Success(true);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException)
        {
            return Failure<bool>("VISUAL_REVIEW_JOB_EVIDENCE_INVALID", ErrorCategory.Integrity, "The completed job evidence is invalid.");
        }
    }

    private static async Task<Result<string>> HashAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Results.Success("sha256:" + Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Failure<string>("VISUAL_REVIEW_OUTPUT_UNREADABLE", ErrorCategory.Environment, "The generated DWG could not be verified.");
        }
    }

    private static bool IsReparsePointOrUnreadable(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return true; }
    }

    private static Result<T> Failure<T>(string code, ErrorCategory category, string message) =>
        Results.Failure<T>(new ContractError(code, category, message, false));

    private sealed record PendingVisualReview(VisualReviewSession Session, string OutputPath);
}

public sealed class AutoCadVisualReviewProcessLauncher : IVisualReviewProcessLauncher
{
    public bool IsAutoCadRunning() => Process.GetProcessesByName("acad").Length != 0 || Process.GetProcessesByName("accoreconsole").Length != 0;

    public Result<int> Start(string autoCadExecutablePath, string outputPath)
    {
        try
        {
            var start = new ProcessStartInfo(autoCadExecutablePath) { UseShellExecute = false };
            start.ArgumentList.Add("/nologo");
            start.ArgumentList.Add(outputPath);
            var process = Process.Start(start);
            return process is null
                ? Results.Failure<int>(new ContractError("VISUAL_REVIEW_CAD_START_FAILED", ErrorCategory.Environment, "AutoCAD did not start for visual review.", false))
                : Results.Success(process.Id);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Results.Failure<int>(new ContractError("VISUAL_REVIEW_CAD_START_FAILED", ErrorCategory.Environment, "AutoCAD did not start for visual review.", false));
        }
    }

    public bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited && string.Equals(process.ProcessName, "acad", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
