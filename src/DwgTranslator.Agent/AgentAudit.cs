using System.Text;
using System.Text.Json;

namespace DwgTranslator.Agent;

public sealed record AgentAuditRecord(
    Guid EventId,
    DateTimeOffset TimestampUtc,
    string Operation,
    string Outcome,
    int StatusCode,
    string CorrelationId,
    string? ErrorCode);

public sealed class FileAgentAuditSink
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _path;

    public FileAgentAuditSink(string logRoot) =>
        _path = Path.Combine(Path.GetFullPath(logRoot), "audit", "agent-host.jsonl");

    public async Task AppendAsync(AgentAuditRecord record, CancellationToken cancellationToken)
    {
        if (record.EventId == Guid.Empty || record.TimestampUtc.Offset != TimeSpan.Zero ||
            string.IsNullOrWhiteSpace(record.Operation) || string.IsNullOrWhiteSpace(record.Outcome) ||
            string.IsNullOrWhiteSpace(record.CorrelationId))
            throw new ArgumentException("AGENT_AUDIT_RECORD_INVALID", nameof(record));

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var line = JsonSerializer.Serialize(record, Json) + Environment.NewLine;
        await Gate.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(_path, line, new UTF8Encoding(false), cancellationToken);
        }
        finally
        {
            Gate.Release();
        }
    }
}
