using DwgTranslator.Contracts;

namespace DwgTranslator.Application;

public interface ICadGateway
{
    Task<Result<CadCapabilities>> GetCapabilitiesAsync(CancellationToken cancellationToken);
    Task<Result<WireEnvelope>> ExchangeAsync(WireEnvelope request, CancellationToken cancellationToken);
}

public enum CadSessionPurpose
{
    ReadOnly,
    Write
}

public interface ICadGatewayLease : IAsyncDisposable
{
    ICadGateway Gateway { get; }
    Task<Result<bool>> CloseAsync(CancellationToken cancellationToken);
}

public interface ICadGatewaySessionFactory
{
    Task<Result<ICadGatewayLease>> OpenAsync(CadSessionPurpose purpose, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a session with a bounded, operation-specific exchange deadline. Implementations that
    /// cannot vary the deadline fail closed to their established session contract.
    /// </summary>
    Task<Result<ICadGatewayLease>> OpenAsync(
        CadSessionPurpose purpose,
        TimeSpan exchangeTimeout,
        CancellationToken cancellationToken) => OpenAsync(purpose, cancellationToken);
}

public interface ITranslationGateway
{
    Task<Result<WireEnvelope>> TranslateAsync(WireEnvelope request, CancellationToken cancellationToken);
}

public interface ISecretStore
{
    Task<Result<string>> GetAsync(SecretReference reference, CancellationToken cancellationToken);
    Task<Result<bool>> SetAsync(SecretReference reference, string secret, CancellationToken cancellationToken);
    Task<Result<bool>> DeleteAsync(SecretReference reference, CancellationToken cancellationToken);
}

public interface IJobStore
{
    Task<Result<JobDocument>> CreateAsync(JobDocument document, CancellationToken cancellationToken);
    Task<Result<JobDocument>> LoadAsync(Guid jobId, CancellationToken cancellationToken);
    Task<Result<JobDocument>> SaveAsync(JobDocument document, long expectedVersion, CancellationToken cancellationToken);
    Task<Result<JobCheckpoint>> SaveCheckpointAsync(JobCheckpoint checkpoint, CancellationToken cancellationToken);
    Task<Result<bool>> AppendAuditAsync(AuditRecord record, CancellationToken cancellationToken);
    Task<Result<int>> DeleteExpiredAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken);
}

public interface ITranslationReviewStore
{
    Task<Result<TranslationReviewSnapshot>> SaveAsync(TranslationReviewSnapshot snapshot, CancellationToken cancellationToken);
    Task<Result<TranslationReviewSnapshot>> SaveAsync(TranslationReviewSnapshot snapshot, long expectedVersion, CancellationToken cancellationToken);
    Task<Result<TranslationReviewSnapshot>> LoadAsync(Guid jobId, CancellationToken cancellationToken);
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
