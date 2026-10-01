using DwgTranslator.Application;
using DwgTranslator.Contracts;

namespace DwgTranslator.Core.Tests;

[TestClass]
public sealed class CadReadWorkflowDeadlineTests
{
    [TestMethod]
    public async Task WorkflowPropagatesBoundedSourceDeadlineToReadSession()
    {
        var sessions = new DeadlineCapturingSessions();
        var workflow = new CadReadWorkflow(sessions, new FixedClock(), _ => TimeSpan.FromSeconds(1850));

        var result = await workflow.InspectAndExtractAsync(Guid.NewGuid(), "C:\\Input\\large.dwg", TestData.HashA, CancellationToken.None);

        Assert.AreEqual("SYNTHETIC_CAPABILITIES_FAILURE", result.Error!.Code);
        Assert.AreEqual(CadSessionPurpose.ReadOnly, sessions.Purpose);
        Assert.AreEqual(TimeSpan.FromSeconds(1850), sessions.ExchangeTimeout);
    }

    [TestMethod]
    public async Task WorkflowFailsClosedBeforeOpeningReadSessionWhenDeadlineIsInvalid()
    {
        var sessions = new DeadlineCapturingSessions();
        var workflow = new CadReadWorkflow(sessions, new FixedClock(), _ => TimeSpan.Zero);

        var result = await workflow.InspectAndExtractAsync(Guid.NewGuid(), "C:\\Input\\large.dwg", TestData.HashA, CancellationToken.None);

        Assert.AreEqual("CAD_SESSION_TIMEOUT_POLICY_INVALID", result.Error!.Code);
        Assert.IsNull(sessions.ExchangeTimeout);
    }

    private sealed class FixedClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch; }

    private sealed class DeadlineCapturingSessions : ICadGatewaySessionFactory
    {
        public CadSessionPurpose? Purpose { get; private set; }
        public TimeSpan? ExchangeTimeout { get; private set; }

        public Task<Result<ICadGatewayLease>> OpenAsync(CadSessionPurpose purpose, CancellationToken cancellationToken) =>
            Task.FromResult(Results.Success<ICadGatewayLease>(new Lease()));

        public Task<Result<ICadGatewayLease>> OpenAsync(CadSessionPurpose purpose, TimeSpan exchangeTimeout, CancellationToken cancellationToken)
        {
            Purpose = purpose;
            ExchangeTimeout = exchangeTimeout;
            return OpenAsync(purpose, cancellationToken);
        }
    }

    private sealed class Lease : ICadGatewayLease
    {
        public ICadGateway Gateway { get; } = new FailingGateway();
        public Task<Result<bool>> CloseAsync(CancellationToken cancellationToken) => Task.FromResult(Results.Success(true));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailingGateway : ICadGateway
    {
        public Task<Result<CadCapabilities>> GetCapabilitiesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Results.Failure<CadCapabilities>(new ContractError(
                "SYNTHETIC_CAPABILITIES_FAILURE", ErrorCategory.Transport, "Synthetic test failure.", true)));

        public Task<Result<WireEnvelope>> ExchangeAsync(WireEnvelope request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
