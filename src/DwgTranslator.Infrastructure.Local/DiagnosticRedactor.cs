namespace DwgTranslator.Infrastructure.Local;

public static class DiagnosticRedactor
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "jobId", "correlationId", "component", "eventType", "reasonCode", "durationMs", "state",
        "sourceHash", "configurationHash", "contractVersion", "modelId", "promptTemplateVersion",
        "from", "to", "selected", "approved", "excluded", "unsupported", "errors",
        "decision", "outputHash", "openedAtUtc", "recordedAtUtc", "autoCadProcessId",
        "automaticValidation", "operatorConfirmed", "jobVersion", "reviewVersion", "reviewHash",
        "reviewContextHash", "reviewScopeRebound", "fromBatchId", "fromManifestHash", "toBatchId",
        "toManifestHash", "policyVersion", "transitionJobVersion", "boundReviewVersion",
        "boundReviewHash", "boundContextHash"
    };

    public static IReadOnlyDictionary<string, object?> Redact(IReadOnlyDictionary<string, object?> fields) =>
        fields
            .Where(pair => Allowed.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    public static System.Text.Json.Nodes.JsonObject Redact(System.Text.Json.Nodes.JsonObject fields)
    {
        var redacted = new System.Text.Json.Nodes.JsonObject();
        foreach (var pair in fields.Where(pair => Allowed.Contains(pair.Key)))
            redacted[pair.Key] = pair.Value?.DeepClone();
        return redacted;
    }
}

public static class SensitiveFieldPolicy
{
    private static readonly HashSet<string> Forbidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "apiKey", "accessToken", "refreshToken", "authorization", "password", "clientSecret", "privateKey"
    };

    public static bool ContainsForbidden(System.Text.Json.Nodes.JsonNode? node)
    {
        if (node is System.Text.Json.Nodes.JsonObject value)
            return value.Any(pair => Forbidden.Contains(pair.Key) || ContainsForbidden(pair.Value));
        if (node is System.Text.Json.Nodes.JsonArray array)
            return array.Any(ContainsForbidden);
        return false;
    }
}
