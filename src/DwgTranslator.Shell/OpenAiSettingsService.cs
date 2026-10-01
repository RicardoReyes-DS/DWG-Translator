using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DwgTranslator.Application;
using DwgTranslator.Contracts;
using DwgTranslator.Translation.OpenAI;

namespace DwgTranslator.Shell;

public sealed record OpenAiUsageLimits(
    int MaxRequestsPerJob,
    int MaxInputTokensPerJob,
    int MaxOutputTokensPerJob,
    int MaxOutputTokensPerRequest);

public sealed record OpenAiModelDiscovery(
    IReadOnlyList<string> Models,
    string RecommendedModel,
    IReadOnlyDictionary<string, bool>? TierAccess = null);

public sealed record OpenAiRoutingModeOption(string Value, string DisplayName, string Description);

public interface IOpenAiSettingsService : IOpenAiRoutingProvider
{
    OpenAiUsageLimits Limits { get; }
    IReadOnlyList<string> ModelOptions { get; }
    string? LastRequestId { get; }
    string RoutingMode => TranslationRouting.Auto;
    IReadOnlyList<OpenAiRoutingModeOption> RoutingModes => OpenAiSettingsService.SupportedRoutingModes;
    Result<bool> SelectRouting(string mode, string manualModel) =>
        Results.Failure<bool>(new ContractError("OPENAI_ROUTING_SELECTION_UNSUPPORTED", ErrorCategory.Configuration, "Routing selection is unavailable.", false));
    TranslationRoutingPolicy IOpenAiRoutingProvider.CreatePolicyForNewJob() => TranslationRoutingPolicyFactory.Auto();
    Task<Result<bool>> SaveAsync(char[] apiKey, char[] confirmation, string model, CancellationToken cancellationToken);
    Task<Result<OpenAiModelDiscovery>> DiscoverModelsAsync(CancellationToken cancellationToken);
    Task<Result<bool>> TestAsync(string model, CancellationToken cancellationToken);
}

public sealed class OpenAiSettingsService : IOpenAiSettingsService
{
    private readonly ISecretStore _secrets;
    private readonly HttpClient _client;
    private readonly SecretReference _reference;
    private string _model;
    private string _routingMode;
    private string? _lastRequestId;
    private readonly object _accessLock = new();
    private HashSet<string> _accessibleModels;

    public static IReadOnlyList<OpenAiRoutingModeOption> SupportedRoutingModes { get; } =
    [
        new(TranslationRouting.Auto, "Auto / Calidad equilibrada", "Terra como base; solo segmentos de alto riesgo pueden escalar una vez a Sol."),
        new(TranslationRouting.Economy, "Económico", "Luna sin escalamiento automático."),
        new(TranslationRouting.MaximumQuality, "Máxima calidad", "Sol para todos los segmentos, sin escalamiento."),
        new(TranslationRouting.Manual, "Manual", "Modelo exacto accesible; nunca se sustituye silenciosamente.")
    ];

    public OpenAiSettingsService(
        ISecretStore secrets,
        HttpClient client,
        SecretReference reference,
        string initialModel,
        OpenAiUsageLimits limits,
        TranslationRoutingPolicy? initialRouting = null)
    {
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _reference = reference ?? throw new ArgumentNullException(nameof(reference));
        _model = ValidateModel(initialModel).Value ?? throw new ArgumentException("The initial OpenAI model is invalid.", nameof(initialModel));
        var routing = initialRouting ?? TranslationRoutingPolicyFactory.Auto();
        _routingMode = TranslationRoutingPolicyFactory.Validate(routing).IsSuccess ? routing.RequestedMode : throw new ArgumentException("The initial routing policy is invalid.", nameof(initialRouting));
        _accessibleModels = new HashSet<string>(StringComparer.Ordinal);
        Limits = limits;
        ModelOptions = new[] { _model, "gpt-4.1-mini", "gpt-4o-mini" }.Distinct(StringComparer.Ordinal).ToArray();
    }

    public string Model => Volatile.Read(ref _model);
    public string RoutingMode => Volatile.Read(ref _routingMode);
    public OpenAiUsageLimits Limits { get; }
    public IReadOnlyList<string> ModelOptions { get; }
    public string? LastRequestId => Volatile.Read(ref _lastRequestId);

    public bool IsModelAccessible(string model)
    {
        lock (_accessLock) return _accessibleModels.Contains(model);
    }

    public TranslationRoutingPolicy CreatePolicyForNewJob() => RoutingMode switch
    {
        TranslationRouting.Auto => TranslationRoutingPolicyFactory.Auto(),
        TranslationRouting.Economy => TranslationRoutingPolicyFactory.Economy(),
        TranslationRouting.MaximumQuality => TranslationRoutingPolicyFactory.MaximumQuality(),
        TranslationRouting.Manual => TranslationRoutingPolicyFactory.Manual(Model),
        _ => throw new InvalidOperationException("The routing mode is invalid.")
    };

    public Result<bool> SelectRouting(string mode, string manualModel)
    {
        TranslationRoutingPolicy policy;
        try
        {
            policy = mode switch
            {
                TranslationRouting.Auto => TranslationRoutingPolicyFactory.Auto(),
                TranslationRouting.Economy => TranslationRoutingPolicyFactory.Economy(),
                TranslationRouting.MaximumQuality => TranslationRoutingPolicyFactory.MaximumQuality(),
                TranslationRouting.Manual => TranslationRoutingPolicyFactory.Manual(ValidateModel(manualModel).Value ?? string.Empty),
                _ => throw new ArgumentException("The routing mode is unsupported.", nameof(mode))
            };
        }
        catch (ArgumentException)
        {
            return Failure("OPENAI_ROUTING_MODE_INVALID", "The selected routing mode or manual model is invalid.");
        }
        var valid = TranslationRoutingPolicyFactory.Validate(policy);
        if (!valid.IsSuccess) return Results.Failure<bool>(valid.Error!);
        if (mode == TranslationRouting.Manual) Volatile.Write(ref _model, policy.BaseModel);
        Volatile.Write(ref _routingMode, mode);
        return Results.Success(true);
    }

    public async Task<Result<bool>> SaveAsync(char[] apiKey, char[] confirmation, string model, CancellationToken cancellationToken)
    {
        try
        {
            var modelValidation = ValidateModel(model);
            if (!modelValidation.IsSuccess) return Results.Failure<bool>(modelValidation.Error!);
            if (!ValidSecret(apiKey) || !apiKey.AsSpan().SequenceEqual(confirmation))
                return Failure("OPENAI_CREDENTIAL_CONFIRMATION_INVALID", "The OpenAI API key is invalid or the confirmation does not match.");

            var secret = new string(apiKey);
            var stored = await _secrets.SetAsync(_reference, secret, cancellationToken).ConfigureAwait(false);
            if (!stored.IsSuccess) return Results.Failure<bool>(stored.Error!);
            Volatile.Write(ref _model, modelValidation.Value!);
            return Results.Success(true);
        }
        finally
        {
            Array.Clear(apiKey);
            Array.Clear(confirmation);
        }
    }

    public async Task<Result<bool>> TestAsync(string model, CancellationToken cancellationToken)
    {
        var modelValidation = ValidateModel(model);
        if (!modelValidation.IsSuccess) return Results.Failure<bool>(modelValidation.Error!);
        var secret = await _secrets.GetAsync(_reference, cancellationToken).ConfigureAwait(false);
        if (!secret.IsSuccess || string.IsNullOrWhiteSpace(secret.Value))
            return Failure("OPENAI_CREDENTIAL_UNAVAILABLE", "The OpenAI credential is unavailable.");

        Volatile.Write(ref _lastRequestId, null);
        using var request = new HttpRequestMessage(HttpMethod.Post, "responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret.Value);
        request.Content = JsonContent.Create(CreateCapabilityProbe(modelValidation.Value!));
        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure("OPENAI_TIMEOUT", "OpenAI did not respond within the configured timeout.", true);
        }
        catch (HttpRequestException)
        {
            return Failure("OPENAI_UNAVAILABLE", "The OpenAI endpoint is unavailable.", true);
        }

        using (response)
        {
            Volatile.Write(ref _lastRequestId, SafeRequestId(response));
            if (response.IsSuccessStatusCode)
            {
                try
                {
                    var payload = await ReadCappedAsync(response.Content, 64 * 1024, cancellationToken).ConfigureAwait(false);
                    using var document = JsonDocument.Parse(payload);
                    if (!CapabilityProbeSucceeded(document.RootElement))
                        return Failure("OPENAI_CAPABILITY_RESPONSE_INVALID", "OpenAI did not complete the required structured-output capability probe.");
                }
                catch (JsonException)
                {
                    return Failure("OPENAI_CAPABILITY_RESPONSE_INVALID", "OpenAI returned an invalid capability response.");
                }
                catch (InvalidDataException)
                {
                    return Failure("OPENAI_CAPABILITY_RESPONSE_INVALID", "OpenAI returned an oversized capability response.");
                }
                if (RoutingMode == TranslationRouting.Manual) Volatile.Write(ref _model, modelValidation.Value!);
                lock (_accessLock) _accessibleModels.Add(modelValidation.Value!);
                return Results.Success(true);
            }
            lock (_accessLock)
            {
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) _accessibleModels.Clear();
                else if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
                    _accessibleModels.Remove(modelValidation.Value!);
            }
            return response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => Failure("OPENAI_AUTHENTICATION_FAILED", "OpenAI rejected the configured credential."),
                HttpStatusCode.NotFound => Failure("OPENAI_MODEL_UNAVAILABLE", "The selected OpenAI model is unavailable for this credential."),
                HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => Failure("OPENAI_MODEL_CAPABILITY_UNSUPPORTED", "The selected model rejected Responses API or Structured Outputs."),
                HttpStatusCode.TooManyRequests => Failure("OPENAI_RATE_LIMITED", "OpenAI rate limited the connection test.", true),
                >= HttpStatusCode.InternalServerError => Failure("OPENAI_UNAVAILABLE", "OpenAI returned a transient error.", true),
                _ => Failure("OPENAI_CONNECTION_TEST_FAILED", "OpenAI rejected the connection test.")
            };
        }
    }

    public async Task<Result<OpenAiModelDiscovery>> DiscoverModelsAsync(CancellationToken cancellationToken)
    {
        var secret = await _secrets.GetAsync(_reference, cancellationToken).ConfigureAwait(false);
        if (!secret.IsSuccess || string.IsNullOrWhiteSpace(secret.Value))
            return DiscoveryFailure("OPENAI_CREDENTIAL_UNAVAILABLE", "The OpenAI credential is unavailable.");

        using var request = new HttpRequestMessage(HttpMethod.Get, "models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret.Value);
        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return DiscoveryFailure("OPENAI_TIMEOUT", "OpenAI did not respond within the configured timeout.", true);
        }
        catch (HttpRequestException)
        {
            return DiscoveryFailure("OPENAI_UNAVAILABLE", "The OpenAI endpoint is unavailable.", true);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode) return DiscoveryHttpFailure(response.StatusCode);
            try
            {
                var payload = await ReadCappedAsync(response.Content, 2 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
                using var document = JsonDocument.Parse(payload);
                if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    return DiscoveryFailure("OPENAI_MODELS_RESPONSE_INVALID", "OpenAI returned an invalid model list.");

                var models = data.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetProperty("id").GetString())
                    .Where(id => id is not null && IsTranslationCandidate(id))
                    .Select(id => id!)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(ModelRank)
                    .ThenBy(id => id, StringComparer.Ordinal)
                    .ToArray();
                lock (_accessLock) _accessibleModels = models.ToHashSet(StringComparer.Ordinal);
                return models.Length == 0
                    ? DiscoveryFailure("OPENAI_NO_TRANSLATION_MODELS", "No compatible text models are available for this credential.")
                    : Results.Success(new OpenAiModelDiscovery(models, models[0], new Dictionary<string, bool>(StringComparer.Ordinal)
                    {
                        [TranslationRouting.Terra] = models.Contains(TranslationRouting.Terra, StringComparer.Ordinal),
                        [TranslationRouting.Luna] = models.Contains(TranslationRouting.Luna, StringComparer.Ordinal),
                        [TranslationRouting.Sol] = models.Contains(TranslationRouting.Sol, StringComparer.Ordinal)
                    }));
            }
            catch (JsonException)
            {
                return DiscoveryFailure("OPENAI_MODELS_RESPONSE_INVALID", "OpenAI returned an invalid model list.");
            }
            catch (InvalidDataException)
            {
                return DiscoveryFailure("OPENAI_MODELS_RESPONSE_TOO_LARGE", "OpenAI returned an oversized model list.");
            }
        }
    }

    private static async Task<byte[]> ReadCappedAsync(HttpContent content, int maximumBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maximumBytes) throw new InvalidDataException();
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return output.ToArray();
            if (output.Length + read > maximumBytes) throw new InvalidDataException();
            output.Write(buffer, 0, read);
        }
    }

    private static object CreateCapabilityProbe(string model) => new
    {
        model,
        store = false,
        reasoning = new { effort = TranslationRouting.ReasoningNone },
        max_output_tokens = 128,
        input = new object[] { new { role = "user", content = "Return status ok using the required JSON schema." } },
        text = new
        {
            format = new
            {
                type = "json_schema",
                name = "dwg_translator_capability_probe",
                strict = true,
                schema = new
                {
                    type = "object",
                    additionalProperties = false,
                    required = new[] { "status" },
                    properties = new { status = new { type = "string", @const = "ok" } }
                }
            }
        }
    };

    private static bool CapabilityProbeSucceeded(JsonElement root)
    {
        if (!root.TryGetProperty("status", out var status) || status.GetString() != "completed" ||
            !root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array) return false;
        var texts = output.EnumerateArray()
            .Where(item => item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            .SelectMany(item => item.GetProperty("content").EnumerateArray())
            .Where(item => item.TryGetProperty("type", out var type) && type.GetString() == "output_text" && item.TryGetProperty("text", out _))
            .Select(item => item.GetProperty("text").GetString())
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToArray();
        if (texts.Length != 1) return false;
        try
        {
            using var structured = JsonDocument.Parse(texts[0]!);
            var value = structured.RootElement;
            return value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Count() == 1 &&
                value.TryGetProperty("status", out var result) && result.GetString() == "ok";
        }
        catch (JsonException) { return false; }
    }

    private static string? SafeRequestId(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("x-request-id", out var values)) return null;
        var value = values.FirstOrDefault();
        return !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_')
            ? value
            : null;
    }

    private static bool IsTranslationCandidate(string id)
    {
        if (id == "gpt-5.6") return false;
        if (!(id.StartsWith("gpt-5", StringComparison.Ordinal) || id.StartsWith("gpt-4.1", StringComparison.Ordinal) ||
            id.StartsWith("gpt-4o", StringComparison.Ordinal))) return false;
        string[] excluded = ["audio", "realtime", "transcribe", "tts", "image", "search", "chat", "codex", "moderation"];
        return !excluded.Any(fragment => id.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    private static int ModelRank(string id)
    {
        string[] preferred = [
            "gpt-5.6-terra", "gpt-5.6-sol", "gpt-5.6-luna", "gpt-5.5", "gpt-5.4", "gpt-5.2", "gpt-5.1", "gpt-5",
            "gpt-4.1", "gpt-5.4-mini", "gpt-5-mini", "gpt-4.1-mini", "gpt-4o", "gpt-4o-mini",
            "gpt-5.4-nano", "gpt-5-nano"
        ];
        for (var index = 0; index < preferred.Length; index++)
            if (id.Equals(preferred[index], StringComparison.Ordinal)) return index;
        for (var index = 0; index < preferred.Length; index++)
            if (id.StartsWith(preferred[index] + "-", StringComparison.Ordinal)) return preferred.Length + index;
        return preferred.Length * 2;
    }

    private static Result<OpenAiModelDiscovery> DiscoveryHttpFailure(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => DiscoveryFailure("OPENAI_AUTHENTICATION_FAILED", "OpenAI rejected the configured credential."),
        HttpStatusCode.TooManyRequests => DiscoveryFailure("OPENAI_RATE_LIMITED", "OpenAI rate limited model discovery.", true),
        >= HttpStatusCode.InternalServerError => DiscoveryFailure("OPENAI_UNAVAILABLE", "OpenAI returned a transient error.", true),
        _ => DiscoveryFailure("OPENAI_MODEL_DISCOVERY_FAILED", "OpenAI rejected model discovery.")
    };

    private static Result<OpenAiModelDiscovery> DiscoveryFailure(string code, string message, bool retryable = false) =>
        Results.Failure<OpenAiModelDiscovery>(new ContractError(code, ErrorCategory.Configuration, message, retryable));

    private static bool ValidSecret(char[] value) =>
        value.Length is >= 20 and <= 512 && value.All(character => !char.IsWhiteSpace(character) && !char.IsControl(character));

    private static Result<string> ValidateModel(string? model) =>
        !string.IsNullOrWhiteSpace(model) && model.Length <= 120 && !model.Any(char.IsWhiteSpace) && model != "gpt-5.6"
            ? Results.Success(model)
            : Results.Failure<string>(new ContractError("OPENAI_MODEL_INVALID", ErrorCategory.Configuration, "A valid OpenAI model is required.", false));

    private static Result<bool> Failure(string code, string message, bool retryable = false) =>
        Results.Failure<bool>(new ContractError(code, ErrorCategory.Configuration, message, retryable));
}
