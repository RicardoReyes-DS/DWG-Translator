using System.Text;
using System.Text.Json.Nodes;
using DwgTranslator.Application;
using DwgTranslator.Domain;
using DwgTranslator.Infrastructure.Local;

namespace DwgTranslator.Core.Tests;

[TestClass]
public sealed class InfrastructureTests
{
    private static readonly string[] ExpectedRedactedKeys = ["jobId", "reasonCode"];
    private string _temporaryRoot = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Initialize()
    {
        _temporaryRoot = Path.Combine(Path.GetTempPath(), "dwgtranslator-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temporaryRoot);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_temporaryRoot))
            Directory.Delete(_temporaryRoot, recursive: true);
    }

    [TestMethod]
    public void WorkspaceRejectsRootedTraversalAndExistingSymlinkPaths()
    {
        var paths = new WorkspacePaths(_temporaryRoot);
        var jobId = Guid.NewGuid();
        Directory.CreateDirectory(paths.JobRoot(jobId));

        Assert.ThrowsExactly<ArgumentException>(() => paths.Resolve(jobId, "../outside"));
        Assert.ThrowsExactly<ArgumentException>(() => paths.Resolve(jobId, Path.GetFullPath(Path.Combine(_temporaryRoot, "outside"))));

        var external = Path.Combine(_temporaryRoot, "external");
        Directory.CreateDirectory(external);
        var link = Path.Combine(paths.JobRoot(jobId), "linked");
        try
        {
            Directory.CreateSymbolicLink(link, external);
        }
        catch (IOException exception) when (OperatingSystem.IsWindows())
        {
            TestContext.WriteLine($"Reparse-point assertion unavailable on this Windows host: 0x{exception.HResult:X8}.");
            return;
        }

        Assert.ThrowsExactly<IOException>(() => paths.Resolve(jobId, Path.Combine("linked", "job.json")));
    }

    [TestMethod]
    public async Task AtomicWriterReplacesContentAndLeavesNoTemporaryFile()
    {
        var destination = Path.Combine(_temporaryRoot, "job.json");
        await AtomicFileWriter.WriteAsync(destination, Encoding.UTF8.GetBytes("first"), default);
        await AtomicFileWriter.WriteAsync(destination, Encoding.UTF8.GetBytes("second"), default);

        Assert.AreEqual("second", await File.ReadAllTextAsync(destination));
        Assert.AreEqual(0, Directory.GetFiles(_temporaryRoot, "*.tmp", SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    public async Task AtomicWriterReplacesWithDeleteSharingAndPreservesOpenReaderSnapshot()
    {
        var destination = Path.Combine(_temporaryRoot, "job.json");
        await AtomicFileWriter.WriteAsync(destination, Encoding.UTF8.GetBytes("old"), default);
        await using var reader = new FileStream(
            destination,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            4096,
            FileOptions.Asynchronous);

        await AtomicFileWriter.WriteAsync(destination, Encoding.UTF8.GetBytes("new"), default);

        var heldSnapshot = new byte[3];
        Assert.AreEqual(heldSnapshot.Length, await reader.ReadAsync(heldSnapshot));
        Assert.AreEqual("old", Encoding.UTF8.GetString(heldSnapshot));
        Assert.AreEqual("new", await File.ReadAllTextAsync(destination));
        Assert.AreEqual(0, Directory.GetFiles(_temporaryRoot, "*.tmp", SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    public async Task AtomicWriterRetriesRealWindowsSharingViolationAfterReaderReleases()
    {
        if (!OperatingSystem.IsWindows()) return;

        var destination = Path.Combine(_temporaryRoot, "job.json");
        var temporary = Path.Combine(_temporaryRoot, "job.tmp");
        await File.WriteAllTextAsync(destination, "old");
        await File.WriteAllTextAsync(temporary, "new");
        var reader = new FileStream(
            destination,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);
        var attempts = 0;
        int? firstWin32Code = null;

        try
        {
            await AtomicFileWriter.ReplaceAsync(temporary, destination, (source, target) =>
            {
                attempts++;
                try
                {
                    File.Replace(source, target, destinationBackupFileName: null);
                }
                catch (IOException exception) when (attempts == 1)
                {
                    firstWin32Code = exception.HResult & 0xFFFF;
                    reader.Dispose();
                    throw;
                }
            }, CancellationToken.None);
        }
        finally
        {
            reader.Dispose();
        }

        Assert.AreEqual(32, firstWin32Code);
        Assert.AreEqual(2, attempts);
        Assert.AreEqual("new", await File.ReadAllTextAsync(destination));
    }

    [TestMethod]
    public async Task AtomicWriterRetriesOnlyTransientSharingOrLockViolations()
    {
        var attempts = 0;

        await AtomicFileWriter.ReplaceAsync("temporary", "destination", (_, _) =>
        {
            attempts++;
            if (attempts < 3) throw new SyntheticIOException(32);
        }, CancellationToken.None);

        Assert.AreEqual(3, attempts);

        attempts = 0;
        await Assert.ThrowsExactlyAsync<SyntheticIOException>(() =>
            AtomicFileWriter.ReplaceAsync("temporary", "destination", (_, _) =>
            {
                attempts++;
                throw new SyntheticIOException(32);
            }, CancellationToken.None));
        Assert.AreEqual(5, attempts);
    }

    [TestMethod]
    public async Task AtomicWriterSharingRetryHonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(10));

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() =>
            AtomicFileWriter.ReplaceAsync("temporary", "destination",
                (_, _) => throw new SyntheticIOException(33), cancellation.Token));

        Assert.IsTrue(cancellation.IsCancellationRequested);
    }

    [TestMethod]
    public async Task AtomicWriterFailsClosedForNonSharingIoError()
    {
        var attempts = 0;

        var exception = await Assert.ThrowsExactlyAsync<SyntheticIOException>(() =>
            AtomicFileWriter.ReplaceAsync("temporary", "destination", (_, _) =>
            {
                attempts++;
                throw new SyntheticIOException(80);
            }, CancellationToken.None));

        Assert.AreEqual(unchecked((int)0x80070050), exception.HResult);
        Assert.AreEqual(1, attempts);
    }

    [TestMethod]
    public async Task JobStoreEnforcesCreateAndCompareAndSwap()
    {
        var store = new LocalJobStore(new WorkspacePaths(_temporaryRoot));
        var jobId = Guid.NewGuid();
        var created = new JobDocument(jobId, JobState.Draft, 0, DateTimeOffset.UtcNow, new JsonObject());

        Assert.IsTrue((await store.CreateAsync(created, default)).IsSuccess);
        Assert.AreEqual("JOB_ALREADY_EXISTS", (await store.CreateAsync(created, default)).Error!.Code);

        var updated = created with { State = JobState.Inspecting, Version = 1 };
        Assert.IsTrue((await store.SaveAsync(updated, 0, default)).IsSuccess);
        Assert.AreEqual("JOB_VERSION_CONFLICT", (await store.SaveAsync(updated with { Version = 2 }, 0, default)).Error!.Code);
        Assert.AreEqual(1L, (await store.LoadAsync(jobId, default)).Value!.Version);
    }

    [TestMethod]
    public void JobStoreUsesReadWriteAndDeleteSharingForJobReaders()
    {
        Assert.AreEqual(
            FileShare.ReadWrite | FileShare.Delete,
            LocalJobStore.JobDocumentReadShare);
    }

    [TestMethod]
    public async Task CheckpointsRejectUnsafeStateAndAuditIsIdempotent()
    {
        var store = new LocalJobStore(new WorkspacePaths(_temporaryRoot));
        var jobId = Guid.NewGuid();
        var document = new JobDocument(jobId, JobState.ReviewRequired, 0, DateTimeOffset.UtcNow, new JsonObject());
        Assert.IsTrue((await store.CreateAsync(document, default)).IsSuccess);

        var unsafeCheckpoint = new JobCheckpoint(jobId, JobState.Writing, 0, DateTimeOffset.UtcNow, TestData.HashA, TestData.HashB, "1.0.0", new JsonObject());
        Assert.AreEqual("CHECKPOINT_UNSAFE_OR_STALE", (await store.SaveCheckpointAsync(unsafeCheckpoint, default)).Error!.Code);
        var safeCheckpoint = unsafeCheckpoint with { SafeState = JobState.ReviewRequired };
        Assert.IsTrue((await store.SaveCheckpointAsync(safeCheckpoint, default)).IsSuccess);
        Assert.IsTrue((await store.SaveCheckpointAsync(safeCheckpoint, default)).IsSuccess,
            "An exact checkpoint projection replay must be idempotent.");
        Assert.AreEqual("CHECKPOINT_CONFLICT", (await store.SaveCheckpointAsync(safeCheckpoint with
        {
            Data = new JsonObject { ["state"] = "Changed" }
        }, default)).Error?.Code);

        var audit = new AuditRecord(Guid.NewGuid(), jobId, Guid.NewGuid(), DateTimeOffset.UtcNow, "JobStateChanged", new JsonObject { ["state"] = "Approved", ["text"] = "private" });
        Assert.IsTrue((await store.AppendAuditAsync(audit, default)).Value);
        Assert.IsFalse((await store.AppendAuditAsync(audit, default)).Value);
        Assert.AreEqual("AUDIT_RECORD_CONFLICT", (await store.AppendAuditAsync(audit with
        {
            Data = new JsonObject { ["state"] = "Failed" }
        }, default)).Error?.Code);
        var auditFile = Directory.GetFiles(Path.Combine(_temporaryRoot, jobId.ToString("D"), "audit"), "*.json").Single();
        var persistedAudit = await File.ReadAllTextAsync(auditFile);
        StringAssert.Contains(persistedAudit, "Approved");
        Assert.IsFalse(persistedAudit.Contains("private", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task JobStoreRejectsCredentialLikeFields()
    {
        var store = new LocalJobStore(new WorkspacePaths(_temporaryRoot));
        var document = new JobDocument(Guid.NewGuid(), JobState.Draft, 0, DateTimeOffset.UtcNow, new JsonObject
        {
            ["nested"] = new JsonObject { ["apiKey"] = "must-not-persist" }
        });

        Assert.AreEqual("JOB_SECRET_FIELD_REJECTED", (await store.CreateAsync(document, default)).Error!.Code);
        Assert.AreEqual(0, Directory.GetFiles(_temporaryRoot, "job.json", SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    public void RedactorUsesAllowlistAndDropsSensitiveFields()
    {
        var redacted = DiagnosticRedactor.Redact(new Dictionary<string, object?>
        {
            ["jobId"] = "synthetic",
            ["reasonCode"] = "TEST",
            ["text"] = "private drawing text",
            ["apiKey"] = "secret",
            ["path"] = "/private/drawing.dwg"
        });

        CollectionAssert.AreEquivalent(ExpectedRedactedKeys, redacted.Keys.ToArray());
    }

    private sealed class SyntheticIOException : IOException
    {
        public SyntheticIOException(int win32Code) =>
            HResult = unchecked((int)(0x80070000u | (uint)win32Code));
    }
}
