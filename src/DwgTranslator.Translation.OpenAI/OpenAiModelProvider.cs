using DwgTranslator.Contracts;

namespace DwgTranslator.Translation.OpenAI;

public interface IOpenAiModelProvider
{
    string Model { get; }
    bool IsModelAccessible(string model) => string.Equals(Model, model, StringComparison.Ordinal);
}

public interface IOpenAiRoutingProvider : IOpenAiModelProvider
{
    TranslationRoutingPolicy CreatePolicyForNewJob();
}

internal sealed class FixedOpenAiModelProvider(string model) : IOpenAiModelProvider
{
    public string Model { get; } = model;
}
