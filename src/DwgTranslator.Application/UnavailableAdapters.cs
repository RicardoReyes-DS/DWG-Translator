using DwgTranslator.Contracts;

namespace DwgTranslator.Application;

public sealed class UnavailableCadGateway : ICadGateway
{
    private static readonly ContractError Error = new(
        "CAD_NOT_CONFIGURED", ErrorCategory.Configuration, "CAD adapter is not configured.", false);

    public Task<Result<CadCapabilities>> GetCapabilitiesAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Results.Failure<CadCapabilities>(Error));

    public Task<Result<WireEnvelope>> ExchangeAsync(WireEnvelope request, CancellationToken cancellationToken) =>
        Task.FromResult(Results.Failure<WireEnvelope>(Error));
}

public sealed class UnavailableTranslationGateway : ITranslationGateway
{
    private static readonly ContractError Error = new(
        "TRANSLATION_NOT_CONFIGURED", ErrorCategory.Configuration, "Translation adapter is not configured.", false);

    public Task<Result<WireEnvelope>> TranslateAsync(WireEnvelope request, CancellationToken cancellationToken) =>
        Task.FromResult(Results.Failure<WireEnvelope>(Error));
}

public sealed class UnavailableSecretStore : ISecretStore
{
    private static readonly ContractError Error = new(
        "SECRET_STORE_NOT_CONFIGURED", ErrorCategory.Configuration, "Secret store is not configured.", false);

    public Task<Result<string>> GetAsync(SecretReference reference, CancellationToken cancellationToken) =>
        Task.FromResult(Results.Failure<string>(Error));

    public Task<Result<bool>> SetAsync(SecretReference reference, string secret, CancellationToken cancellationToken) =>
        Task.FromResult(Results.Failure<bool>(Error));

    public Task<Result<bool>> DeleteAsync(SecretReference reference, CancellationToken cancellationToken) =>
        Task.FromResult(Results.Failure<bool>(Error));
}
