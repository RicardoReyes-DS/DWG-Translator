using DwgTranslator.Contracts;

namespace DwgTranslator.Application;

public sealed class CadWriteWorkflow
{
    private readonly ICadGatewaySessionFactory _sessions;
    private readonly IClock _clock;
    private readonly Func<string, TimeSpan>? _exchangeTimeoutForSource;

    public CadWriteWorkflow(ICadGateway cad, IClock clock)
        : this(new FixedCadGatewaySessionFactory(cad), clock)
    {
    }

    public CadWriteWorkflow(
        ICadGatewaySessionFactory sessions,
        IClock clock,
        Func<string, TimeSpan>? exchangeTimeoutForSource = null)
    {
        _sessions = sessions;
        _clock = clock;
        _exchangeTimeoutForSource = exchangeTimeoutForSource;
    }

    public async Task<Result<CadWriteResponsePayload>> WriteAsync(
        Guid jobId,
        string sourcePath,
        string finalPath,
        string expectedSourceHash,
        IReadOnlyList<CadTextSegment> segments,
        TranslationReviewSnapshot review,
        CancellationToken cancellationToken)
    {
        var request = CadWriteRequestFactory.Create(jobId, sourcePath, finalPath, expectedSourceHash, segments, review, _clock.UtcNow);
        if (!request.IsSuccess) return Results.Failure<CadWriteResponsePayload>(request.Error!);
        TimeSpan? exchangeTimeout = null;
        if (_exchangeTimeoutForSource is not null)
        {
            try
            {
                exchangeTimeout = _exchangeTimeoutForSource(sourcePath);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
            {
                return Results.Failure<CadWriteResponsePayload>(new ContractError(
                    "CAD_SESSION_TIMEOUT_POLICY_INVALID", ErrorCategory.Configuration,
                    "The bounded CAD exchange deadline could not be derived from the immutable source snapshot.", false));
            }

            if (exchangeTimeout <= TimeSpan.Zero)
                return Results.Failure<CadWriteResponsePayload>(new ContractError(
                    "CAD_SESSION_TIMEOUT_POLICY_INVALID", ErrorCategory.Configuration,
                    "The bounded CAD exchange deadline must be positive.", false));
        }

        return await CadSessionRunner.RunAsync(
            _sessions,
            CadSessionPurpose.Write,
            async cad =>
            {
                var response = await cad.ExchangeAsync(request.Value!, cancellationToken);
                return response.IsSuccess
                    ? CadWriteResultPolicy.Validate(request.Value!, response.Value!)
                    : Results.Failure<CadWriteResponsePayload>(response.Error!);
            },
            cancellationToken,
            exchangeTimeout);
    }
}
