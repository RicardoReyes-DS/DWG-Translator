using System.Net;
using System.Text.Json;
using DwgTranslator.AgentMcp;

namespace DwgTranslator.AgentMcp.Tests;

[TestClass]
public sealed class AgentHostHttpReadClientTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public void InspectionAllowsLargeDwgReadBudget()
    {
        Assert.AreEqual(TimeSpan.FromMinutes(20), AgentHostHttpReadClient.InspectionTimeout);
    }

    [TestMethod]
    public void BatchRecoveryStartAllowsSourceSnapshotBudget()
    {
        Assert.AreEqual(TimeSpan.FromMinutes(2), AgentHostHttpReadClient.BatchRecoveryStartTimeout);
    }

    [TestMethod]
    public async Task PostSerializesOnceAndSendsExactContentLength()
    {
        long? observedContentLength = null;
        var observedBodyLength = 0;
        string? observedUri = null;
        string? observedMediaType = null;
        string? observedCharset = null;
        var handler = new StubHandler(async (request, cancellationToken) =>
        {
            observedUri = request.RequestUri?.AbsoluteUri;
            observedContentLength = request.Content!.Headers.ContentLength;
            observedMediaType = request.Content.Headers.ContentType?.MediaType;
            observedCharset = request.Content.Headers.ContentType?.CharSet;
            observedBodyLength = (await request.Content.ReadAsByteArrayAsync(cancellationToken)).Length;
            return SuccessResponse();
        });
        using var client = new AgentHostHttpReadClient(new Uri("http://127.0.0.1:47831"), "test-token", handler);
        var payload = new CountingPayload("value");

        var result = await client.ApplyTranslationReviewAsync(payload, CancellationToken.None);

        Assert.IsTrue(result.Success);
        Assert.AreEqual(1, payload.ValueReads);
        Assert.IsNotNull(observedContentLength);
        Assert.AreEqual(observedBodyLength, observedContentLength.Value);
        Assert.IsGreaterThan(0, observedBodyLength);
        Assert.AreEqual("http://127.0.0.1:47831/v1/workflow/translation/review/apply", observedUri);
        Assert.AreEqual("application/json", observedMediaType);
        Assert.AreEqual("utf-8", observedCharset);
    }

    [TestMethod]
    public async Task ParsesPayloadTooLargeEnvelopeFromHttp413()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(ErrorResponse(
            HttpStatusCode.RequestEntityTooLarge,
            "AGENT_REQUEST_TOO_LARGE",
            "The Agent Host request exceeded the allowed size.")));
        using var client = new AgentHostHttpReadClient(new Uri("http://127.0.0.1:47831"), "test-token", handler);

        var result = await client.ApplyTranslationReviewAsync(new { value = "oversize" }, CancellationToken.None);

        Assert.IsFalse(result.Success);
        Assert.AreEqual("AGENT_REQUEST_TOO_LARGE", result.Error!.Code);
        Assert.IsFalse(result.Error.Retryable);
    }

    [TestMethod]
    public async Task CallerCancellationPropagatesWithoutBeingReclassifiedAsTimeout()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHandler(async (_, cancellationToken) =>
        {
            entered.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return SuccessResponse();
        });
        using var client = new AgentHostHttpReadClient(new Uri("http://127.0.0.1:47831"), "test-token", handler);
        using var cancellation = new CancellationTokenSource();

        var operation = client.HealthAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        var cancelled = false;
        try
        {
            await operation;
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        Assert.IsTrue(cancelled);
    }

    [TestMethod]
    public async Task JobsListAllowsMoreThanTheDefaultReadTimeout()
    {
        var handler = new StubHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(16), cancellationToken);
            return SuccessResponse();
        });
        using var client = new AgentHostHttpReadClient(new Uri("http://127.0.0.1:47831"), "test-token", handler);

        var result = await client.ListJobsAsync(CancellationToken.None);

        Assert.IsTrue(result.Success);
    }

    private static HttpResponseMessage SuccessResponse()
    {
        var result = new AgentMcpResult(
            "dwg-agent-cli/1.0", true, "translation review apply", DateTimeOffset.UtcNow,
            JsonSerializer.SerializeToElement(new { state = "Approved" }, WebJson), null);
        return new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(result, WebJson))
        };
    }

    private static HttpResponseMessage ErrorResponse(HttpStatusCode statusCode, string code, string message)
    {
        var result = new AgentMcpResult(
            "dwg-agent-cli/1.0", false, "translation review apply", DateTimeOffset.UtcNow,
            null, new AgentMcpError(code, message));
        return new(statusCode)
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(result, WebJson))
        };
    }

    private sealed class CountingPayload(string value)
    {
        public int ValueReads { get; private set; }
        public string Value
        {
            get
            {
                ValueReads++;
                return value;
            }
        }
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
