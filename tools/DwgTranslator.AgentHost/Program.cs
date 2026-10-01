using System.Security.Cryptography;
using DwgTranslator.Agent;
using DwgTranslator.AgentHost;
using DwgTranslator.Application;
using DwgTranslator.Contracts;
using DwgTranslator.Infrastructure.Windows;

var arguments = HostArguments.Parse(args);
if (arguments.Error is not null)
{
    Console.Error.WriteLine(arguments.Error);
    return 2;
}

var loaded = AgentConfigurationLoader.Load(arguments.ConfigPath!);
if (loaded.Error is not null)
{
    Console.Error.WriteLine(loaded.Error.Code);
    return 2;
}

var configuration = loaded.Configuration!;
using var ownership = AgentHostOwnershipLease.TryAcquire(configuration);
if (ownership is null)
{
    Console.Error.WriteLine("AGENT_HOST_OWNERSHIP_CONFLICT");
    return 3;
}
var runtimeIdentity = AgentHostRuntimeIdentity.Create(configuration);
AgentHostPolicy policy;
try { policy = new AgentHostPolicy(configuration.AllowedOperations); }
catch (ArgumentException) { Console.Error.WriteLine("AGENT_ALLOWLIST_INVALID"); return 2; }

string expectedToken;
if (arguments.SelfTest)
{
    expectedToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
}
else
{
    var referenceResult = SecretReference.Create(configuration.CredentialReference);
    if (!referenceResult.IsSuccess)
    {
        Console.Error.WriteLine("AGENT_CREDENTIAL_REFERENCE_INVALID");
        return 2;
    }

    var secrets = new WindowsCredentialSecretStore();
    var tokenResult = await secrets.GetAsync(referenceResult.Value!, CancellationToken.None);
    if (!tokenResult.IsSuccess)
    {
        if (tokenResult.Error!.Code != "SECRET_NOT_FOUND")
        {
            Console.Error.WriteLine("AGENT_CREDENTIAL_UNAVAILABLE");
            return 3;
        }
        var generated = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        var stored = await secrets.SetAsync(referenceResult.Value!, generated, CancellationToken.None);
        if (!stored.IsSuccess)
        {
            Console.Error.WriteLine("AGENT_CREDENTIAL_PROVISION_FAILED");
            return 3;
        }
        tokenResult = await secrets.GetAsync(referenceResult.Value!, CancellationToken.None);
    }
    if (!tokenResult.IsSuccess || string.IsNullOrEmpty(tokenResult.Value))
    {
        Console.Error.WriteLine("AGENT_CREDENTIAL_UNAVAILABLE");
        return 3;
    }
    expectedToken = tokenResult.Value;
}
var query = new AgentQueryService(configuration, identity: runtimeIdentity);
var audit = new FileAgentAuditSink(configuration.LogRoot);
IAgentCadReadExecutor cadExecutor = configuration.ExecutionEnabled
    ? CreateCadExecutor(configuration)
    : new DisabledCadReadExecutor();
var cadInspection = new AgentCadInspectionService(configuration, cadExecutor);
var invariantDiff = new AgentInvariantDiffService(configuration, CreateInvariantDiffExecutor(configuration));
var workflowRuntime = AgentWorkflowRuntimeFactory.Create(configuration);
if (workflowRuntime.Error is not null)
{
    Console.Error.WriteLine(workflowRuntime.Error);
    return 3;
}
var workflow = workflowRuntime.Service;
var batch = new AgentBatchService(configuration, workflowRuntime.BatchProcessor);
if (workflow is not null)
    await workflow.ReconcileOrphanedOperationsAsync(CancellationToken.None);
if (configuration.BatchExecutionEnabled)
    batch.ReconcileRunningBatches();
var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls(configuration.HostUrl);
builder.Logging.ClearProviders();
var app = builder.Build();

app.MapGet("/v1/health", context => ExecuteAsync(context, AgentHostOperations.Health, query.Health));
app.MapGet("/v1/capabilities", context => ExecuteAsync(context, AgentHostOperations.Capabilities, query.Capabilities));
app.MapGet("/v1/jobs", context => ExecuteAsync(context, AgentHostOperations.JobsList, query.ListJobs));
app.MapGet("/v1/jobs/{jobId:guid}", (HttpContext context, Guid jobId) =>
    ExecuteAsync(context, AgentHostOperations.JobsGet, () => query.GetJob(jobId)));
app.MapPost("/v1/cad/inspection-plan", (Func<HttpContext, Task>)(context =>
    ExecuteCadAsync<AgentCadInspectionPlanRequest>(context, AgentHostOperations.CadInspectionPlan,
        request => cadInspection.CreatePlanAsync(request.SourcePath, context.RequestAborted))));
app.MapPost("/v1/cad/inspect", (Func<HttpContext, Task>)(context =>
    ExecuteCadAsync<AgentCadInspectionRequest>(context, AgentHostOperations.CadInspect,
        request => cadInspection.InspectAsync(request, context.RequestAborted))));
app.MapPost("/v1/cad/invariant-diff/plan", (Func<HttpContext, Task>)(context =>
    ExecuteCadAsync<AgentInvariantDiffPlanRequest>(context, AgentHostOperations.InvariantDiffPlan,
        request => invariantDiff.CreatePlanAsync(request, context.RequestAborted))));
app.MapPost("/v1/cad/invariant-diff/run", (Func<HttpContext, Task>)(context =>
    ExecuteCadAsync<AgentInvariantDiffRunRequest>(context, AgentHostOperations.InvariantDiffRun,
        request => invariantDiff.RunAsync(request, context.RequestAborted))));
app.MapPost("/v1/workflow/translation/plan", (Func<HttpContext, Task>)(context =>
    ExecuteCadAsync<AgentTranslationPlanRequest>(context, AgentHostOperations.TranslationPlan,
        request => WorkflowOrDisabled(service => service.CreateTranslationPlanAsync(request, context.RequestAborted)))));
app.MapPost("/v1/workflow/translation/prepare", (Func<HttpContext, Task>)(context =>
    ExecuteCadAsync<AgentTranslationPrepareRequest>(context, AgentHostOperations.TranslationPrepare,
        request => WorkflowOrDisabled(service => service.PrepareAsync(request, context.RequestAborted)))));
app.MapPost("/v1/workflow/translation/review/get", (Func<HttpContext, Task>)(context =>
    ExecuteCadAsync<AgentTranslationReviewGetRequest>(context, AgentHostOperations.TranslationReviewGet,
        request => WorkflowOrDisabled(service => service.GetReviewAsync(request, context.RequestAborted)))));
app.MapPost("/v1/workflow/translation/review/apply", (Func<HttpContext, Task>)(context =>
    ExecuteCadAsync<AgentTranslationReviewApplyRequest>(context, AgentHostOperations.TranslationReviewApply,
        request => WorkflowOrDisabled(service => service.ApplyReviewAsync(request, context.RequestAborted)))));
app.MapPost("/v1/workflow/generation/plan", (Func<HttpContext, Task>)(context =>
    ExecuteCadAsync<AgentGenerationPlanRequest>(context, AgentHostOperations.GenerationPlan,
        request => WorkflowOrDisabled(service => service.CreateGenerationPlanAsync(request, context.RequestAborted)))));
app.MapPost("/v1/workflow/generation/reconcile/plan", (Func<HttpContext, Task>)(context =>
    ExecuteCadAsync<AgentGenerationReconciliationPlanRequest>(context, AgentHostOperations.GenerationReconcilePlan,
        request => WorkflowOrDisabled(service => service.CreateGenerationReconciliationPlanAsync(request, context.RequestAborted)))));
app.MapPost("/v1/workflow/generation/reconcile/apply", (Func<HttpContext, Task>)(context =>
    ExecuteCadAsync<AgentGenerationReconciliationApplyRequest>(context, AgentHostOperations.GenerationReconcileApply,
        request => WorkflowOrDisabled(service => service.ApplyGenerationReconciliationAsync(request, context.RequestAborted)))));
app.MapPost("/v1/workflow/generation/execute", (Func<HttpContext, Task>)(context =>
    ExecuteCadAsync<AgentGenerateRequest>(context, AgentHostOperations.Generate,
        request => WorkflowOrDisabled(service => service.GenerateAsync(request, context.RequestAborted)))));
app.MapGet("/v1/workflow/jobs/{jobId:guid}/status", (HttpContext context, Guid jobId) =>
    ExecuteAgentAsync(context, AgentHostOperations.JobStatus,
        () => WorkflowOrDisabled(service => service.JobStatusAsync(jobId, context.RequestAborted))));
app.MapGet("/v1/workflow/jobs/{jobId:guid}/next-action", (HttpContext context, Guid jobId) =>
    ExecuteAgentAsync(context, AgentHostOperations.JobNextAction,
        () => WorkflowOrDisabled(service => service.JobNextActionAsync(jobId, context.RequestAborted))));
app.MapPost("/v1/workflow/cancel", (Func<HttpContext, Task>)(context =>
    ExecuteCadAsync<AgentWorkflowCancelRequest>(context, AgentHostOperations.WorkflowCancel,
        request => WorkflowOrDisabled(service => service.CancelAsync(request, context.RequestAborted)))));
app.MapPost("/v1/batches/plan", (Func<HttpContext, Task>)(context =>
    ExecuteCadAsync<AgentBatchPlanRequest>(context, AgentHostOperations.BatchPlan,
        request => Task.FromResult(batch.Plan(request)))));
app.MapPost("/v1/batches/start", (Func<HttpContext, Task>)(context =>
    ExecuteCadAsync<AgentBatchStartRequest>(context, AgentHostOperations.BatchStart,
        request => Task.FromResult(batch.Start(request)))));
app.MapGet("/v1/batches/{batchId:guid}/status", (HttpContext context, Guid batchId) =>
    ExecuteAgentAsync(context, AgentHostOperations.BatchStatus, () => Task.FromResult(batch.Status(batchId))));
app.MapGet("/v1/batches/{batchId:guid}/next-action", (HttpContext context, Guid batchId) =>
    ExecuteAgentAsync(context, AgentHostOperations.BatchNextAction, () => Task.FromResult(batch.NextAction(batchId))));
app.MapGet("/v1/batches/{batchId:guid}/review-summary", (HttpContext context, Guid batchId) =>
    ExecuteAgentAsync(context, AgentHostOperations.BatchReviewSummary, () => Task.FromResult(batch.ReviewSummary(batchId))));
app.MapPost("/v1/batches/approve-and-generate", (Func<HttpContext, Task>)(context =>
    ExecuteCadAsync<AgentBatchVersionRequest>(context, AgentHostOperations.BatchApproveAndGenerate,
        request => Task.FromResult(batch.ApproveAndGenerate(request)))));
app.MapGet("/v1/batches/{batchId:guid}/report", (HttpContext context, Guid batchId) =>
    ExecuteAgentAsync(context, AgentHostOperations.BatchReport, () => Task.FromResult(batch.Report(batchId))));
app.MapPost("/v1/batches/cancel", (Func<HttpContext, Task>)(context =>
    ExecuteCadAsync<AgentBatchCancelRequest>(context, AgentHostOperations.BatchCancel,
        request => Task.FromResult(batch.Cancel(request)))));
app.MapPost("/v1/batches/recovery-plan", (Func<HttpContext, Task>)(context =>
    ExecuteCadAsync<AgentBatchRecoveryPlanRequest>(context, AgentHostOperations.BatchRecoveryPlan,
        request => Task.FromResult(batch.RecoveryPlan(request)))));
app.MapPost("/v1/batches/recovery-start", (Func<HttpContext, Task>)(context =>
    ExecuteCadAsync<AgentBatchRecoveryStartRequest>(context, AgentHostOperations.BatchRecoveryStart,
        request => Task.FromResult(batch.StartRecovery(request)))));
app.MapPost("/v1/batches/reconcile-review", (Func<HttpContext, Task>)(context =>
    ExecuteCadAsync<AgentBatchReconcileReviewRequest>(context, AgentHostOperations.BatchReconcileReview,
        request => Task.FromResult(batch.ReconcileReview(request)))));

var mutationMethods = new[] { "POST", "PUT", "PATCH", "DELETE" };
app.MapMethods("/{**path}", mutationMethods, context =>
    WriteRejectedMutationAsync(context));
app.Map("/{**path}", context => WriteUnknownRouteAsync(context));

using var shutdown = arguments.RunSeconds is null ? null : new CancellationTokenSource(TimeSpan.FromSeconds(arguments.RunSeconds.Value));
Console.Out.WriteLine($"{{\"schemaVersion\":\"dwg-agent-host/1.0\",\"status\":\"starting\",\"readOnly\":{(!configuration.OpenAiEnabled).ToString().ToLowerInvariant()}}}");
if (arguments.SelfTest)
{
    await app.StartAsync();
    try
    {
        using var client = new HttpClient { BaseAddress = new Uri(configuration.HostUrl), Timeout = TimeSpan.FromSeconds(10) };
        using var unauthenticated = await client.GetAsync("v1/health");
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", expectedToken);
        using var health = await client.GetAsync("v1/health");
        using var capabilities = await client.GetAsync("v1/capabilities");
        using var jobs = await client.GetAsync("v1/jobs");
        using var mutation = await client.PostAsync("v1/jobs", null);
        var passed = unauthenticated.StatusCode == System.Net.HttpStatusCode.Unauthorized &&
            health.IsSuccessStatusCode && capabilities.IsSuccessStatusCode && jobs.IsSuccessStatusCode &&
            mutation.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed;
        Console.Out.WriteLine($"{{\"schemaVersion\":\"dwg-agent-host-self-test/1.0\",\"passed\":{passed.ToString().ToLowerInvariant()},\"unauthenticatedStatus\":{(int)unauthenticated.StatusCode},\"mutationStatus\":{(int)mutation.StatusCode}}}");
        return passed ? 0 : 4;
    }
    finally
    {
        await app.StopAsync();
    }
}
try
{
    await app.RunAsync(shutdown?.Token ?? CancellationToken.None);
    return 0;
}
catch (OperationCanceledException) when (shutdown?.IsCancellationRequested == true)
{
    return 0;
}

async Task ExecuteAsync(HttpContext context, string operation, Func<AgentEnvelope> action)
{
    var correlationId = CorrelationId(context);
    if (!Authenticated(context))
    {
        await CompleteAsync(context, operation, correlationId, 401,
            new(AgentQueryService.ResponseSchema, false, operation, DateTimeOffset.UtcNow, null,
                new("AGENT_UNAUTHORIZED", "A valid Agent Host bearer credential is required.")));
        return;
    }
    if (!policy.Allows(operation))
    {
        await CompleteAsync(context, operation, correlationId, 403,
            new(AgentQueryService.ResponseSchema, false, operation, DateTimeOffset.UtcNow, null,
                new("AGENT_OPERATION_FORBIDDEN", "The operation is not in the configured allowlist.")));
        return;
    }

    var response = action();
    await CompleteAsync(context, operation, correlationId, response.Success ? 200 : 404, response);
}

async Task ExecuteCadAsync<TRequest>(
    HttpContext context,
    string operation,
    Func<TRequest, Task<AgentEnvelope>> action)
    where TRequest : class
{
    var correlationId = CorrelationId(context);
    if (!Authenticated(context))
    {
        await CompleteAsync(context, operation, correlationId, 401,
            new(AgentQueryService.ResponseSchema, false, operation, DateTimeOffset.UtcNow, null,
                new("AGENT_UNAUTHORIZED", "A valid Agent Host bearer credential is required.")));
        return;
    }
    if (!policy.Allows(operation))
    {
        await CompleteAsync(context, operation, correlationId, 403,
            new(AgentQueryService.ResponseSchema, false, operation, DateTimeOffset.UtcNow, null,
                new("AGENT_OPERATION_FORBIDDEN", "The operation is not in the configured allowlist.")));
        return;
    }
    var dispatch = await AgentRequestBodyPolicy.ReadAndDispatchAsync<TRequest, AgentEnvelope>(
        context.Request, operation, action, context.RequestAborted);
    if (dispatch.Status == AgentRequestBodyReadStatus.TooLarge)
    {
        await CompleteAsync(context, operation, correlationId, AgentRequestBodyPolicy.TooLargeStatusCode,
            new(AgentQueryService.ResponseSchema, false, operation, DateTimeOffset.UtcNow, null,
                new(AgentRequestBodyPolicy.TooLargeErrorCode, AgentRequestBodyPolicy.TooLargeErrorMessage)));
        return;
    }

    if (dispatch.Status != AgentRequestBodyReadStatus.Success || dispatch.Response is null)
    {
        await CompleteAsync(context, operation, correlationId, 400,
            new(AgentQueryService.ResponseSchema, false, operation, DateTimeOffset.UtcNow, null,
                new("AGENT_REQUEST_INVALID", "A valid JSON request is required.")));
        return;
    }

    var response = dispatch.Response;
    var status = response.Error?.Code is "AGENT_CAD_BUSY" or "CAD_BUSY" ? 409 :
        response.Error?.Code is "AGENT_CAD_EXECUTION_DISABLED" ? 403 :
        response.Error?.Code is "CAD_PROCESS_START_FAILED" or "CAD_SESSION_UNAVAILABLE" ? 503 :
        StatusCode(response);
    await CompleteAsync(context, operation, correlationId, status, response);
}

async Task ExecuteAgentAsync(HttpContext context, string operation, Func<Task<AgentEnvelope>> action)
{
    var correlationId = CorrelationId(context);
    if (!Authenticated(context))
    {
        await CompleteAsync(context, operation, correlationId, 401,
            new(AgentQueryService.ResponseSchema, false, operation, DateTimeOffset.UtcNow, null,
                new("AGENT_UNAUTHORIZED", "A valid Agent Host bearer credential is required.")));
        return;
    }
    if (!policy.Allows(operation))
    {
        await CompleteAsync(context, operation, correlationId, 403,
            new(AgentQueryService.ResponseSchema, false, operation, DateTimeOffset.UtcNow, null,
                new("AGENT_OPERATION_FORBIDDEN", "The operation is not in the configured allowlist.")));
        return;
    }
    var response = await action();
    await CompleteAsync(context, operation, correlationId, StatusCode(response), response);
}

async Task WriteRejectedMutationAsync(HttpContext context)
{
    var correlationId = CorrelationId(context);
    if (!Authenticated(context))
    {
        await CompleteAsync(context, "mutation", correlationId, 401,
            new(AgentQueryService.ResponseSchema, false, "mutation", DateTimeOffset.UtcNow, null,
                new("AGENT_UNAUTHORIZED", "A valid Agent Host bearer credential is required.")));
        return;
    }
    var response = new AgentEnvelope(AgentQueryService.ResponseSchema, false, "mutation", DateTimeOffset.UtcNow, null,
        new("AGENT_MUTATION_DISABLED", "Mutation endpoints are disabled in this release."));
    await CompleteAsync(context, "mutation", correlationId, 405, response);
}

async Task WriteUnknownRouteAsync(HttpContext context)
{
    var correlationId = CorrelationId(context);
    var authenticated = Authenticated(context);
    var response = new AgentEnvelope(AgentQueryService.ResponseSchema, false, "unknown", DateTimeOffset.UtcNow, null,
        authenticated
            ? new("AGENT_ROUTE_NOT_FOUND", "The Agent Host route does not exist.")
            : new("AGENT_UNAUTHORIZED", "A valid Agent Host bearer credential is required."));
    await CompleteAsync(context, "unknown", correlationId, authenticated ? 404 : 401, response);
}

async Task CompleteAsync(HttpContext context, string operation, string correlationId, int statusCode, AgentEnvelope response)
{
    context.Response.StatusCode = statusCode;
    context.Response.ContentType = "application/json";
    context.Response.Headers["X-Correlation-Id"] = correlationId;
    await context.Response.WriteAsJsonAsync(response, cancellationToken: context.RequestAborted);
    await audit.AppendAsync(new(Guid.NewGuid(), DateTimeOffset.UtcNow, operation,
        response.Success ? "Succeeded" : "Rejected", statusCode, correlationId, response.Error?.Code), CancellationToken.None);
}

Task<AgentEnvelope> WorkflowOrDisabled(Func<AgentWorkflowService, Task<AgentEnvelope>> action) => workflow is null
    ? Task.FromResult(new AgentEnvelope(AgentQueryService.ResponseSchema, false, "workflow", DateTimeOffset.UtcNow, null,
        new("OPENAI_EXECUTION_DISABLED", "Agent Beta translation workflow is disabled.")))
    : action(workflow);

static int StatusCode(AgentEnvelope response) => response.Success ? 200 : response.Error?.Code switch
{
    "AGENT_OPERATION_FORBIDDEN" or "OPENAI_EXECUTION_DISABLED" => 403,
    "AGENT_JOB_NOT_FOUND" or "JOB_NOT_FOUND" => 404,
    "JOB_STATE_CONFLICT" or "OUTPUT_ALREADY_EXISTS" or "IDEMPOTENCY_CONFLICT" or "IDEMPOTENCY_PENDING" => 409,
    "IPC_TIMEOUT" or "OPENAI_UNAVAILABLE" => 503,
    _ => 400
};

bool Authenticated(HttpContext context)
{
    var authorization = context.Request.Headers.Authorization.ToString();
    const string prefix = "Bearer ";
    return authorization.StartsWith(prefix, StringComparison.Ordinal) &&
        AgentBearerAuthenticator.Validate(authorization[prefix.Length..], expectedToken);
}

static string CorrelationId(HttpContext context)
{
    var supplied = context.Request.Headers["X-Correlation-Id"].ToString();
    return Guid.TryParse(supplied, out var parsed) ? parsed.ToString("D") : Guid.NewGuid().ToString("D");
}

static IAgentCadReadExecutor CreateCadExecutor(AgentBetaConfiguration configuration)
{
    var sessions = new AutoCadProcessSessionFactory(
        AgentCadSessionOptionsFactory.CreateReadOnly(configuration, "inspection"));
    return new CadReadWorkflowAgentExecutor(new CadReadWorkflow(sessions, new SystemClock()));
}

static IAgentInvariantDiffExecutor CreateInvariantDiffExecutor(AgentBetaConfiguration configuration)
{
    var sessions = new AutoCadProcessSessionFactory(
        AgentCadSessionOptionsFactory.CreateReadOnly(configuration, "invariant-diff"),
        new AgentCadLifecycleObserver(configuration.LogRoot));
    return new CadInvariantDiffAgentExecutor(new CadInvariantDiffWorkflow(sessions, new SystemClock()));
}

sealed class DisabledCadReadExecutor : IAgentCadReadExecutor
{
    public Task<Result<CadReadResult>> InspectAndExtractAsync(
        Guid jobId,
        string sourcePath,
        string expectedSourceHash,
        CancellationToken cancellationToken) =>
        Task.FromResult(DwgTranslator.Contracts.Results.Failure<CadReadResult>(new ContractError(
            "AGENT_CAD_EXECUTION_DISABLED",
            ErrorCategory.Security,
            "Agent Beta CAD execution is disabled.",
            false)));
}

internal sealed record HostArguments(string? ConfigPath, int? RunSeconds, bool SelfTest, string? Error)
{
    internal static HostArguments Parse(string[] values)
    {
        string? config = null;
        int? seconds = null;
        var selfTest = false;
        for (var index = 0; index < values.Length; index++)
        {
            if (values[index] == "--config" && index + 1 < values.Length)
                config = values[++index];
            else if (values[index] == "--run-seconds" && index + 1 < values.Length &&
                     int.TryParse(values[++index], out var parsed) && parsed is >= 1 and <= 300)
                seconds = parsed;
            else if (values[index] == "--self-test")
                selfTest = true;
            else
                return new(config, seconds, selfTest, "AGENT_ARGUMENT_INVALID");
        }
        return string.IsNullOrWhiteSpace(config)
            ? new(config, seconds, selfTest, "AGENT_CONFIG_REQUIRED")
            : new(config, seconds, selfTest, null);
    }
}
