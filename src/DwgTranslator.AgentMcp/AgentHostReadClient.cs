using System.Net.Http.Headers;
using System.Text.Json;

namespace DwgTranslator.AgentMcp;

public sealed record AgentMcpError(string Code, string Message, bool Retryable = false);

public sealed record AgentMcpResult(
    string SchemaVersion,
    bool Success,
    string Command,
    DateTimeOffset TimestampUtc,
    JsonElement? Data,
    AgentMcpError? Error);

public interface IAgentHostReadClient
{
    Task<AgentMcpResult> HealthAsync(CancellationToken cancellationToken);
    Task<AgentMcpResult> CapabilitiesAsync(CancellationToken cancellationToken);
    Task<AgentMcpResult> ListJobsAsync(CancellationToken cancellationToken);
    Task<AgentMcpResult> GetJobAsync(Guid jobId, CancellationToken cancellationToken);
    Task<AgentMcpResult> CreateInspectionPlanAsync(string sourcePath, CancellationToken cancellationToken);
    Task<AgentMcpResult> InspectDwgAsync(
        string sourcePath,
        string expectedSourceHash,
        string approval,
        CancellationToken cancellationToken);
    Task<AgentMcpResult> CreateInvariantDiffPlanAsync(object request, CancellationToken cancellationToken);
    Task<AgentMcpResult> RunInvariantDiffAsync(object request, CancellationToken cancellationToken);
    Task<AgentMcpResult> CreateTranslationPlanAsync(object request, CancellationToken cancellationToken);
    Task<AgentMcpResult> PrepareTranslationAsync(object request, CancellationToken cancellationToken);
    Task<AgentMcpResult> GetTranslationReviewAsync(object request, CancellationToken cancellationToken);
    Task<AgentMcpResult> ApplyTranslationReviewAsync(object request, CancellationToken cancellationToken);
    Task<AgentMcpResult> CreateGenerationPlanAsync(object request, CancellationToken cancellationToken);
    Task<AgentMcpResult> CreateGenerationReconciliationPlanAsync(object request, CancellationToken cancellationToken);
    Task<AgentMcpResult> ApplyGenerationReconciliationAsync(object request, CancellationToken cancellationToken);
    Task<AgentMcpResult> GenerateAsync(object request, CancellationToken cancellationToken);
    Task<AgentMcpResult> JobStatusAsync(Guid jobId, CancellationToken cancellationToken);
    Task<AgentMcpResult> JobNextActionAsync(Guid jobId, CancellationToken cancellationToken);
    Task<AgentMcpResult> CancelWorkflowAsync(object request, CancellationToken cancellationToken);
    Task<AgentMcpResult> CreateBatchPlanAsync(object request, CancellationToken cancellationToken);
    Task<AgentMcpResult> StartBatchAsync(object request, CancellationToken cancellationToken);
    Task<AgentMcpResult> BatchStatusAsync(Guid batchId, CancellationToken cancellationToken);
    Task<AgentMcpResult> BatchNextActionAsync(Guid batchId, CancellationToken cancellationToken);
    Task<AgentMcpResult> BatchReviewSummaryAsync(Guid batchId, CancellationToken cancellationToken);
    Task<AgentMcpResult> ApproveAndGenerateBatchAsync(object request, CancellationToken cancellationToken);
    Task<AgentMcpResult> BatchReportAsync(Guid batchId, CancellationToken cancellationToken);
    Task<AgentMcpResult> CancelBatchAsync(object request, CancellationToken cancellationToken);
    Task<AgentMcpResult> CreateBatchRecoveryPlanAsync(object request, CancellationToken cancellationToken);
    Task<AgentMcpResult> StartBatchRecoveryAsync(object request, CancellationToken cancellationToken);
    Task<AgentMcpResult> ReconcileBatchReviewAsync(object request, CancellationToken cancellationToken);
}

public sealed class AgentHostHttpReadClient : IAgentHostReadClient, IDisposable
{
    private const int MaxResponseBytes = 4 * 1024 * 1024;
    internal static readonly TimeSpan InspectionTimeout = TimeSpan.FromMinutes(20);
    internal static readonly TimeSpan BatchRecoveryStartTimeout = TimeSpan.FromMinutes(2);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };
    private readonly HttpClient _client;

    public AgentHostHttpReadClient(Uri hostUrl, string bearerToken)
        : this(hostUrl, bearerToken, new HttpClientHandler())
    {
    }

    internal AgentHostHttpReadClient(Uri hostUrl, string bearerToken, HttpMessageHandler handler)
    {
        if (!hostUrl.IsLoopback || hostUrl.Scheme != Uri.UriSchemeHttp)
            throw new ArgumentException("AGENT_HOST_URL_INVALID", nameof(hostUrl));
        if (string.IsNullOrWhiteSpace(bearerToken))
            throw new ArgumentException("AGENT_HOST_TOKEN_REQUIRED", nameof(bearerToken));
        ArgumentNullException.ThrowIfNull(handler);
        _client = new HttpClient(handler) { BaseAddress = hostUrl, Timeout = Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
    }

    public Task<AgentMcpResult> HealthAsync(CancellationToken cancellationToken) =>
        GetAsync("v1/health", "health", cancellationToken);

    public Task<AgentMcpResult> CapabilitiesAsync(CancellationToken cancellationToken) =>
        GetAsync("v1/capabilities", "capabilities", cancellationToken);

    public Task<AgentMcpResult> ListJobsAsync(CancellationToken cancellationToken) =>
        GetAsync("v1/jobs", "jobs list", TimeSpan.FromSeconds(60), cancellationToken);

    public Task<AgentMcpResult> GetJobAsync(Guid jobId, CancellationToken cancellationToken) =>
        jobId == Guid.Empty
            ? Task.FromResult(Failure("jobs get", "AGENT_JOB_ID_INVALID", "A non-empty job ID is required."))
            : GetAsync($"v1/jobs/{jobId:D}", "jobs get", cancellationToken);

    public Task<AgentMcpResult> CreateInspectionPlanAsync(string sourcePath, CancellationToken cancellationToken) =>
        PostAsync("v1/cad/inspection-plan", "cad inspection plan", new { sourcePath }, TimeSpan.FromSeconds(30), cancellationToken);

    public Task<AgentMcpResult> InspectDwgAsync(
        string sourcePath,
        string expectedSourceHash,
        string approval,
        CancellationToken cancellationToken) =>
        PostAsync("v1/cad/inspect", "cad inspect dry-run",
            new { sourcePath, expectedSourceHash, approval }, InspectionTimeout, cancellationToken);

    public Task<AgentMcpResult> CreateInvariantDiffPlanAsync(object request, CancellationToken cancellationToken) =>
        PostAsync("v1/cad/invariant-diff/plan", "invariant diff plan", request, TimeSpan.FromMinutes(2), cancellationToken);
    public Task<AgentMcpResult> RunInvariantDiffAsync(object request, CancellationToken cancellationToken) =>
        PostAsync("v1/cad/invariant-diff/run", "invariant diff run", request, TimeSpan.FromMinutes(20), cancellationToken);

    public Task<AgentMcpResult> CreateTranslationPlanAsync(object request, CancellationToken cancellationToken) =>
        PostAsync("v1/workflow/translation/plan", "translation plan", request, TimeSpan.FromSeconds(30), cancellationToken);

    public Task<AgentMcpResult> PrepareTranslationAsync(object request, CancellationToken cancellationToken) =>
        PostAsync("v1/workflow/translation/prepare", "translation prepare", request, TimeSpan.FromSeconds(30), cancellationToken);

    public Task<AgentMcpResult> GetTranslationReviewAsync(object request, CancellationToken cancellationToken) =>
        PostAsync("v1/workflow/translation/review/get", "translation review get", request, TimeSpan.FromSeconds(30), cancellationToken);

    public Task<AgentMcpResult> ApplyTranslationReviewAsync(object request, CancellationToken cancellationToken) =>
        PostAsync("v1/workflow/translation/review/apply", "translation review apply", request, TimeSpan.FromSeconds(30), cancellationToken);

    public Task<AgentMcpResult> CreateGenerationPlanAsync(object request, CancellationToken cancellationToken) =>
        PostAsync("v1/workflow/generation/plan", "generation plan", request, TimeSpan.FromSeconds(30), cancellationToken);

    public Task<AgentMcpResult> CreateGenerationReconciliationPlanAsync(object request, CancellationToken cancellationToken) =>
        PostAsync("v1/workflow/generation/reconcile/plan", "generation reconciliation plan", request, TimeSpan.FromSeconds(30), cancellationToken);

    public Task<AgentMcpResult> ApplyGenerationReconciliationAsync(object request, CancellationToken cancellationToken) =>
        PostAsync("v1/workflow/generation/reconcile/apply", "generation reconciliation apply", request, TimeSpan.FromSeconds(30), cancellationToken);

    public Task<AgentMcpResult> GenerateAsync(object request, CancellationToken cancellationToken) =>
        PostAsync("v1/workflow/generation/execute", "generation execute", request, TimeSpan.FromSeconds(30), cancellationToken);

    public Task<AgentMcpResult> JobStatusAsync(Guid jobId, CancellationToken cancellationToken) =>
        GetAsync($"v1/workflow/jobs/{jobId:D}/status", "workflow job status", cancellationToken);

    public Task<AgentMcpResult> JobNextActionAsync(Guid jobId, CancellationToken cancellationToken) =>
        GetAsync($"v1/workflow/jobs/{jobId:D}/next-action", "workflow job next action", cancellationToken);

    public Task<AgentMcpResult> CancelWorkflowAsync(object request, CancellationToken cancellationToken) =>
        PostAsync("v1/workflow/cancel", "workflow cancel", request, TimeSpan.FromSeconds(30), cancellationToken);

    public Task<AgentMcpResult> CreateBatchPlanAsync(object request, CancellationToken cancellationToken) =>
        PostAsync("v1/batches/plan", "batch plan", request, TimeSpan.FromMinutes(2), cancellationToken);
    public Task<AgentMcpResult> StartBatchAsync(object request, CancellationToken cancellationToken) =>
        PostAsync("v1/batches/start", "batch start", request, TimeSpan.FromSeconds(30), cancellationToken);
    public Task<AgentMcpResult> BatchStatusAsync(Guid batchId, CancellationToken cancellationToken) =>
        GetAsync($"v1/batches/{batchId:D}/status", "batch status", cancellationToken);
    public Task<AgentMcpResult> BatchNextActionAsync(Guid batchId, CancellationToken cancellationToken) =>
        GetAsync($"v1/batches/{batchId:D}/next-action", "batch next action", cancellationToken);
    public Task<AgentMcpResult> BatchReviewSummaryAsync(Guid batchId, CancellationToken cancellationToken) =>
        GetAsync($"v1/batches/{batchId:D}/review-summary", "batch review summary", cancellationToken);
    public Task<AgentMcpResult> ApproveAndGenerateBatchAsync(object request, CancellationToken cancellationToken) =>
        PostAsync("v1/batches/approve-and-generate", "batch approve and generate", request, TimeSpan.FromSeconds(30), cancellationToken);
    public Task<AgentMcpResult> BatchReportAsync(Guid batchId, CancellationToken cancellationToken) =>
        GetAsync($"v1/batches/{batchId:D}/report", "batch report", cancellationToken);
    public Task<AgentMcpResult> CancelBatchAsync(object request, CancellationToken cancellationToken) =>
        PostAsync("v1/batches/cancel", "batch cancel", request, TimeSpan.FromSeconds(30), cancellationToken);
    public Task<AgentMcpResult> CreateBatchRecoveryPlanAsync(object request, CancellationToken cancellationToken) =>
        PostAsync("v1/batches/recovery-plan", "batch recovery plan", request, TimeSpan.FromSeconds(30), cancellationToken);
    public Task<AgentMcpResult> StartBatchRecoveryAsync(object request, CancellationToken cancellationToken) =>
        PostAsync("v1/batches/recovery-start", "batch recovery start", request, BatchRecoveryStartTimeout, cancellationToken);
    public Task<AgentMcpResult> ReconcileBatchReviewAsync(object request, CancellationToken cancellationToken) =>
        PostAsync("v1/batches/reconcile-review", "batch reconcile review", request, TimeSpan.FromSeconds(30), cancellationToken);

    private Task<AgentMcpResult> GetAsync(string route, string command, CancellationToken cancellationToken) =>
        GetAsync(route, command, TimeSpan.FromSeconds(15), cancellationToken);

    private Task<AgentMcpResult> GetAsync(
        string route,
        string command,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Get, route), command, timeout, cancellationToken);

    private Task<AgentMcpResult> PostAsync(
        string route,
        string command,
        object body,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), Json);
        var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        content.Headers.ContentLength = payload.Length;
        var request = new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = content
        };
        return SendAsync(request, command, timeout, cancellationToken);
    }

    private async Task<AgentMcpResult> SendAsync(
        HttpRequestMessage request,
        string command,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            using var ownedRequest = request;
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            request.Headers.Add("X-Correlation-Id", Guid.NewGuid().ToString("D"));
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token);
            var length = response.Content.Headers.ContentLength;
            if (length is > MaxResponseBytes)
                return Failure(command, "AGENT_HOST_RESPONSE_TOO_LARGE", "The Agent Host response exceeded the MCP bridge limit.");
            await using var stream = await response.Content.ReadAsStreamAsync(timeoutSource.Token);
            using var limited = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, timeoutSource.Token)) > 0)
            {
                if (limited.Length + read > MaxResponseBytes)
                    return Failure(command, "AGENT_HOST_RESPONSE_TOO_LARGE", "The Agent Host response exceeded the MCP bridge limit.");
                await limited.WriteAsync(buffer.AsMemory(0, read), timeoutSource.Token);
            }
            limited.Position = 0;
            var result = await JsonSerializer.DeserializeAsync<AgentMcpResult>(limited, Json, timeoutSource.Token);
            return result is not null && result.SchemaVersion == "dwg-agent-cli/1.0"
                ? result
                : Failure(command, "AGENT_HOST_RESPONSE_INVALID", "The Agent Host returned an invalid contract.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(command, "AGENT_HOST_TIMEOUT", "The Agent Host did not respond in time.");
        }
        catch (HttpRequestException)
        {
            return Failure(command, "AGENT_HOST_UNAVAILABLE", "The Agent Host is unavailable.");
        }
        catch (JsonException)
        {
            return Failure(command, "AGENT_HOST_RESPONSE_INVALID", "The Agent Host returned an invalid contract.");
        }
        catch (IOException)
        {
            return Failure(command, "AGENT_HOST_IO_ERROR", "The Agent Host response could not be read.");
        }
    }

    private static AgentMcpResult Failure(string command, string code, string message) =>
        new("dwg-agent-cli/1.0", false, command, DateTimeOffset.UtcNow, null, new(code, message));

    public void Dispose() => _client.Dispose();
}
