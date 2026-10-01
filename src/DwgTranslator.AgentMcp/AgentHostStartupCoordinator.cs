namespace DwgTranslator.AgentMcp;

public interface IAgentHostStartupDependencies
{
    Task<string?> ReadTokenAsync(CancellationToken cancellationToken);
    Task<bool> IsReadyAsync(string token, CancellationToken cancellationToken);
    bool TryStart();
}

public sealed record AgentHostStartupResult(bool Success, string? Token, string? ErrorCode);

public sealed class AgentHostStartupCoordinator
{
    private readonly IAgentHostStartupDependencies _dependencies;
    private readonly int _attempts;
    private readonly TimeSpan _delay;

    public AgentHostStartupCoordinator(
        IAgentHostStartupDependencies dependencies,
        int attempts = 30,
        TimeSpan? delay = null)
    {
        _dependencies = dependencies ?? throw new ArgumentNullException(nameof(dependencies));
        if (attempts is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(attempts));
        _attempts = attempts;
        _delay = delay ?? TimeSpan.FromMilliseconds(200);
    }

    /// <summary>
    /// Read clients are deliberately non-lifecycle operations.  Only an explicit, separately-authorized
    /// lifecycle command may pass <paramref name="allowStart"/>; health/capabilities must never revive an
    /// older configured runtime and split ownership from the durable Beta host.
    /// </summary>
    public async Task<AgentHostStartupResult> EnsureReadyAsync(CancellationToken cancellationToken, bool allowStart = false)
    {
        var token = await _dependencies.ReadTokenAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(token) &&
            await _dependencies.IsReadyAsync(token, cancellationToken).ConfigureAwait(false))
            return new(true, token, null);

        if (!allowStart)
            return new(false, null, "AGENT_HOST_UNAVAILABLE");
        if (!_dependencies.TryStart())
            return new(false, null, "AGENT_HOST_START_FAILED");

        for (var attempt = 0; attempt < _attempts; attempt++)
        {
            await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
            token = await _dependencies.ReadTokenAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(token) &&
                await _dependencies.IsReadyAsync(token, cancellationToken).ConfigureAwait(false))
                return new(true, token, null);
        }
        return new(false, null, "AGENT_HOST_START_TIMEOUT");
    }
}
