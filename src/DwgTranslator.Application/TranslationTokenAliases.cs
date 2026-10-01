using System.Text;
using System.Text.RegularExpressions;
using DwgTranslator.Contracts;

namespace DwgTranslator.Application;

public sealed record AliasedTranslationText(string Text, IReadOnlyDictionary<string, string> Aliases);

public static partial class TranslationTokenAliases
{
    public static Result<AliasedTranslationText> Protect(string sourceText, IReadOnlyList<CadProtectedToken> tokens)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        ArgumentNullException.ThrowIfNull(tokens);
        var output = new StringBuilder(sourceText.Length + tokens.Count * 4);
        var aliases = new Dictionary<string, string>(tokens.Count, StringComparer.Ordinal);
        var cursor = 0;
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (token.Ordinal != index || string.IsNullOrEmpty(token.Token))
                return Failure<AliasedTranslationText>("TRANSLATION_TOKEN_SOURCE_INVALID", "Protected token ordinals must be contiguous and non-empty.");
            var offset = sourceText.IndexOf(token.Token, cursor, StringComparison.Ordinal);
            if (offset < 0)
                return Failure<AliasedTranslationText>("TRANSLATION_TOKEN_SOURCE_MISMATCH", "A protected token is absent or out of order in source text.");
            output.Append(sourceText, cursor, offset - cursor);
            var key = $"T{index}";
            output.Append(Alias(key));
            aliases.Add(key, token.Token);
            cursor = offset + token.Token.Length;
        }
        output.Append(sourceText, cursor, sourceText.Length - cursor);
        return Results.Success(new AliasedTranslationText(output.ToString(), aliases));
    }

    public static Result<string> Restore(
        string translatedText,
        IReadOnlyDictionary<string, string> aliases,
        string sourceTextWithTokenAliases)
    {
        ArgumentNullException.ThrowIfNull(translatedText);
        ArgumentNullException.ThrowIfNull(aliases);
        ArgumentNullException.ThrowIfNull(sourceTextWithTokenAliases);
        var matches = AliasPattern().Matches(translatedText).Select(match => match.Groups[1].Value).ToArray();
        var expectedKeys = aliases.Keys.OrderBy(AliasOrdinal).ToArray();
        if (matches.Length != aliases.Count || matches.Distinct(StringComparer.Ordinal).Count() != matches.Length ||
            matches.Any(key => !aliases.ContainsKey(key)) || aliases.Keys.Any(key => matches.Count(item => item == key) != 1) ||
            !matches.SequenceEqual(expectedKeys, StringComparer.Ordinal))
            return Failure<string>("TOKEN_INTEGRITY_FAILED", "Every protected token alias must occur exactly once and no unknown alias is allowed.");

        var normalized = NormalizeWhitespaceOnlySourceSlots(sourceTextWithTokenAliases, translatedText, expectedKeys);
        if (!normalized.IsSuccess)
            return normalized;

        if (!TextSlotOccupancyMatches(sourceTextWithTokenAliases, normalized.Value!, expectedKeys))
            return Failure<string>("TOKEN_INTEGRITY_FAILED", "Text must remain in the same occupied slots around protected token aliases.");

        var restored = AliasPattern().Replace(normalized.Value!, match => aliases[match.Groups[1].Value]);
        var expected = expectedKeys.Select(key => aliases[key]).ToArray();
        var actual = CadProtectedTokenPolicy.Extract(restored).Select(item => item.Token).ToArray();
        return expected.SequenceEqual(actual, StringComparer.Ordinal)
            ? Results.Success(restored)
            : Failure<string>("TOKEN_INTEGRITY_FAILED", "Restored CAD tokens differ from the protected source tokens.");
    }

    public static string Alias(string key) => $"⟦{key}⟧";

    private static bool TextSlotOccupancyMatches(string source, string translated, IReadOnlyList<string> expectedKeys) =>
        TryGetTextSlotOccupancy(source, expectedKeys, out var sourceSlots) &&
        TryGetTextSlotOccupancy(translated, expectedKeys, out var translatedSlots) &&
        sourceSlots.SequenceEqual(translatedSlots);

    private static Result<string> NormalizeWhitespaceOnlySourceSlots(
        string source,
        string translated,
        string[] expectedKeys)
    {
        if (!TryGetTextSlots(source, expectedKeys, out var sourceSlots) ||
            !TryGetTextSlots(translated, expectedKeys, out var translatedSlots))
            return Failure<string>("TOKEN_INTEGRITY_FAILED", "Text slots around protected token aliases are invalid.");

        var normalized = new StringBuilder(translated.Length);
        for (var index = 0; index < sourceSlots.Length; index++)
        {
            var sourceSlot = sourceSlots[index];
            var translatedSlot = translatedSlots[index];
            if (sourceSlot.Length > 0 && string.IsNullOrWhiteSpace(sourceSlot))
            {
                if (translatedSlot.Length > 0 && !string.IsNullOrWhiteSpace(translatedSlot))
                    return Failure<string>("TOKEN_INTEGRITY_FAILED", "Whitespace-only CAD text slots cannot acquire visible text.");
                normalized.Append(sourceSlot);
            }
            else
            {
                normalized.Append(translatedSlot);
            }

            if (index < expectedKeys.Length)
                normalized.Append(Alias(expectedKeys[index]));
        }

        return Results.Success(normalized.ToString());
    }

    private static bool TryGetTextSlots(string value, IReadOnlyList<string> expectedKeys, out string[] slots)
    {
        var matches = AliasPattern().Matches(value);
        if (matches.Count != expectedKeys.Count)
        {
            slots = [];
            return false;
        }

        slots = new string[expectedKeys.Count + 1];
        var cursor = 0;
        for (var index = 0; index < matches.Count; index++)
        {
            var match = matches[index];
            if (!string.Equals(match.Groups[1].Value, expectedKeys[index], StringComparison.Ordinal))
                return false;
            slots[index] = value.Substring(cursor, match.Index - cursor);
            cursor = match.Index + match.Length;
        }

        slots[^1] = value[cursor..];
        return true;
    }

    private static bool TryGetTextSlotOccupancy(string value, IReadOnlyList<string> expectedKeys, out bool[] slots)
    {
        if (!TryGetTextSlots(value, expectedKeys, out var textSlots))
        {
            slots = [];
            return false;
        }

        slots = textSlots.Select(slot => slot.Length > 0).ToArray();
        return true;
    }

    private static int AliasOrdinal(string key) => int.TryParse(key.AsSpan(1), out var value) ? value : int.MaxValue;

    private static Result<T> Failure<T>(string code, string message) =>
        Results.Failure<T>(new ContractError(code, ErrorCategory.Integrity, message, false));

    [GeneratedRegex("⟦(T[0-9]+)⟧", RegexOptions.CultureInvariant)]
    private static partial Regex AliasPattern();
}
