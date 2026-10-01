using DwgTranslator.Contracts;

namespace DwgTranslator.Application;

public sealed class FixedCadGatewaySessionFactory : ICadGatewaySessionFactory
{
    private readonly ICadGateway _gateway;

    public FixedCadGatewaySessionFactory(ICadGateway gateway) =>
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));

    public Task<Result<ICadGatewayLease>> OpenAsync(CadSessionPurpose purpose, CancellationToken cancellationToken) =>
        Task.FromResult(Results.Success<ICadGatewayLease>(new Lease(_gateway)));

    private sealed class Lease(ICadGateway gateway) : ICadGatewayLease
    {
        public ICadGateway Gateway { get; } = gateway;
        public Task<Result<bool>> CloseAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Results.Success(true));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
