using System.Text.Json;
using System.Text.Json.Serialization;
using DwgTranslator.Contracts;

namespace DwgTranslator.Agent;

/// <summary>Persists only redacted CAD lifecycle evidence using write-through, replace-by-rename writes.</summary>
public sealed class AgentCadLifecycleReceiptStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    private readonly string _directory;

    public AgentCadLifecycleReceiptStore(string logRoot)
    {
        _directory = Path.Combine(Path.GetFullPath(logRoot), "workflow", "cad-receipts");
        Directory.CreateDirectory(_directory);
    }

    public void Upsert(AgentCadLifecycleReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.JobId == Guid.Empty || receipt.ProcessId <= 0 || string.IsNullOrWhiteSpace(receipt.RequestHash))
            throw new ArgumentException("CAD_RECEIPT_INVALID", nameof(receipt));
        if (receipt.ResponseTechnicalStage is { } stage && !CadWriteTechnicalStages.IsSupported(stage) ||
            receipt.ResponseNativeErrorStatus is { } nativeStatus && !CadNativeErrorStatuses.IsSupported(nativeStatus))
            throw new ArgumentException("CAD_RECEIPT_DIAGNOSTIC_INVALID", nameof(receipt));
        if (string.IsNullOrWhiteSpace(receipt.OperationId))
            receipt = receipt with { OperationId = FindLatestOperationId(receipt.JobId) };
        var path = PathFor(receipt.JobId);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                       FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, receipt, Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public AgentCadLifecycleReceipt? Load(Guid jobId)
    {
        if (jobId == Guid.Empty) return null;
        try
        {
            var path = PathFor(jobId);
            return File.Exists(path) ? JsonSerializer.Deserialize<AgentCadLifecycleReceipt>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public (AgentCadLifecycleReceipt? Receipt, string? ArtifactHash) LoadEvidence(Guid jobId)
    {
        if (jobId == Guid.Empty) return (null, null);
        try
        {
            var path = PathFor(jobId);
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return (null, null);
            var bytes = File.ReadAllBytes(path);
            return (JsonSerializer.Deserialize<AgentCadLifecycleReceipt>(bytes, Json),
                "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant());
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return (null, null);
        }
    }

    public void BindOperation(Guid jobId, string operationId)
    {
        var current = Load(jobId);
        if (current is not null && string.IsNullOrWhiteSpace(current.OperationId))
            Upsert(current with { OperationId = operationId });
    }

    private string PathFor(Guid jobId) => Path.Combine(_directory, jobId.ToString("D") + ".json");

    private string? FindLatestOperationId(Guid jobId)
    {
        var operations = Path.Combine(Path.GetDirectoryName(_directory)!, "operations");
        if (!Directory.Exists(operations)) return null;
        AgentWorkflowOperation? latest = null;
        foreach (var path in Directory.EnumerateFiles(operations, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var candidate = JsonSerializer.Deserialize<AgentWorkflowOperation>(File.ReadAllText(path), Json);
                if (candidate?.JobId == jobId && (latest is null || candidate.UpdatedAtUtc > latest.UpdatedAtUtc)) latest = candidate;
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return latest?.OperationId;
    }
}
