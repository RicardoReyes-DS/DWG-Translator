using System.Text.RegularExpressions;

namespace DwgTranslator.Application;

public sealed record TerminologyContext(string? Layer, string? Layout, string? Sheet, string? NeighborText, string? Discipline);
public sealed record TerminologyMatch(string ConceptId, string Source, string? Target, bool Ambiguous, string DecisionCode);
public sealed record TerminologyResult(string Text, IReadOnlyList<TerminologyMatch> Matches, IReadOnlyList<string> ErrorCodes)
{
    public bool IsValid => ErrorCodes.Count == 0;
}

public static partial class ArchitecturalMepTerminologyPolicy
{
    public const string Version = "architectural-mep-levels/en-US/v1";

    public static IReadOnlyList<Contracts.TranslationGlossaryEntry> Glossary { get; } =
    [
        Entry("N.P.T.", "FFL"), Entry("NPT", "FFL"), Entry("Nivel de Piso Terminado", "Finished Floor Level"),
        Entry("N.P.F.", "RAFL"), Entry("NPF", "RAFL"), Entry("Nivel de Piso Falso", "Raised Access Floor Level"),
        Entry("N.T.T.", "FCL"), Entry("NTT", "FCL"), Entry("Nivel de Techo Terminado", "Finished Ceiling Level"),
        Entry("Cubierta terminada", "Finished Roof Level"),
        Entry("Sobre nivel de piso terminado", "Above Finished Floor Level")
    ];

    public static TerminologyResult Apply(string source, TerminologyContext context)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);
        var matches = new List<TerminologyMatch>();
        var errors = new List<string>();
        var output = source;

        output = Replace(output, Affl(), "above-finished-floor-level", "AFFL", "ABOVE FINISHED FLOOR LEVEL", matches);
        output = Replace(output, Npt(), "finished-floor-level", "FFL", "FINISHED FLOOR LEVEL", matches);
        output = ReplaceContextual(output, Npf(), "raised-access-floor-level", "RAFL", "RAISED ACCESS FLOOR LEVEL",
            IsRaisedFloor(context), matches, errors);
        output = ReplaceContextual(output, Ntt(), IsRoof(context) ? "finished-roof-level" : "finished-ceiling-level",
            IsRoof(context) ? "FRL" : "FCL", IsRoof(context) ? "FINISHED ROOF LEVEL" : "FINISHED CEILING LEVEL",
            IsRoof(context) || IsCeiling(context), matches, errors);

        return new(output, matches, errors.Distinct(StringComparer.Ordinal).ToArray());
    }

    public static IReadOnlyList<string> Validate(string source, string target, IReadOnlyList<TerminologyMatch> matches)
    {
        var errors = new List<string>();
        if (KnownSpanish().IsMatch(target)) errors.Add("TERMINOLOGY_SPANISH_RESIDUAL");
        if (ForbiddenAliases().IsMatch(target)) errors.Add("TERMINOLOGY_ALIAS_FORBIDDEN");
        if (matches.Any(match => match.Ambiguous)) errors.Add("TERMINOLOGY_AMBIGUOUS");
        var expected = CadProtectedTokenPolicy.Extract(source).Select(token => token.Token);
        var actual = CadProtectedTokenPolicy.Extract(target).Select(token => token.Token);
        if (!expected.SequenceEqual(actual, StringComparer.Ordinal)) errors.Add("TOKEN_INTEGRITY_FAILED");
        if (!Numbers().Matches(source).Select(m => m.Value).SequenceEqual(Numbers().Matches(target).Select(m => m.Value), StringComparer.Ordinal))
            errors.Add("NUMERIC_INTEGRITY_FAILED");
        return errors.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string Replace(string input, Regex regex, string concept, string abbreviation, string longTarget,
        List<TerminologyMatch> matches) => regex.Replace(input, match =>
    {
        var target = IsLegend(match.Value, input) ? longTarget : abbreviation;
        matches.Add(new(concept, match.Value, target, false, "TERMINOLOGY_APPLIED"));
        return target;
    });

    private static string ReplaceContextual(string input, Regex regex, string concept, string abbreviation, string longTarget,
        bool contextConfirmed, List<TerminologyMatch> matches, List<string> errors) => regex.Replace(input, match =>
    {
        if (!contextConfirmed)
        {
            matches.Add(new(concept, match.Value, null, true, "TERMINOLOGY_AMBIGUOUS"));
            errors.Add("TERMINOLOGY_AMBIGUOUS");
            return match.Value;
        }
        var target = IsLegend(match.Value, input) ? longTarget : abbreviation;
        matches.Add(new(concept, match.Value, target, false, "TERMINOLOGY_APPLIED"));
        return target;
    });

    private static bool IsLegend(string value, string whole) => whole.Contains(" - ", StringComparison.Ordinal);
    private static bool Has(TerminologyContext c, params string[] terms) =>
        new[] { c.Layer, c.Layout, c.Sheet, c.NeighborText, c.Discipline }.Where(x => x is not null)
            .Any(value => terms.Any(term => value!.Contains(term, StringComparison.OrdinalIgnoreCase)));
    private static bool IsRaisedFloor(TerminologyContext c) => Has(c, "PISO FALSO", "RAISED", "ACCESS FLOOR", "PISO TÉCNICO", "PISO TECNICO");
    private static bool IsRoof(TerminologyContext c) => Has(c, "CUBIERTA", "ROOF", "AZOTEA");
    private static bool IsCeiling(TerminologyContext c) => Has(c, "TECHO", "CEILING", "CIELO", "PLAFÓN", "PLAFON");
    private static Contracts.TranslationGlossaryEntry Entry(string source, string target) =>
        new() { Source = source, Target = target, CaseSensitive = false };

    [GeneratedRegex(@"(?ix)\b(?:N\s*\.?\s*P\s*\.?\s*T\.?|NIVEL\s+DE\s+PISO\s+TERMINADO)(?=\s|$|[+-])")]
    private static partial Regex Npt();
    [GeneratedRegex(@"(?ix)\b(?:N\s*\.?\s*P\s*\.?\s*F\.?|NIVEL\s+DE\s+PISO\s+FALSO)(?=\s|$|[+-])")]
    private static partial Regex Npf();
    [GeneratedRegex(@"(?ix)\b(?:N\s*\.?\s*T\s*\.?\s*T\.?|NIVEL\s+DE\s+TECHO\s+TERMINADO|CUBIERTA\s+TERMINADA)(?=\s|$|[+-])")]
    private static partial Regex Ntt();
    [GeneratedRegex(@"(?ix)\b(?:SOBRE\s+NIVEL\s+DE\s+PISO\s+TERMINADO|A\s*\.?\s*N\s*\.?\s*P\s*\.?\s*T\.?)(?=\s|$|[+-])")]
    private static partial Regex Affl();
    [GeneratedRegex(@"(?ix)\b(?:N\s*\.?\s*P\s*\.?\s*[TFT]\s*\.?|NIVEL\s+DE\s+(?:PISO|TECHO)\s+(?:TERMINADO|FALSO)|CUBIERTA\s+TERMINADA|SOBRE\s+NIVEL\s+DE\s+PISO\s+TERMINADO)\b")]
    private static partial Regex KnownSpanish();
    [GeneratedRegex(@"(?i)\b(?:F\.F\.L\.|R\.A\.F\.L\.|F\.C\.L\.|F\.R\.L\.|A\.F\.F\.L\.)\b")]
    private static partial Regex ForbiddenAliases();
    [GeneratedRegex(@"[+-]?\d+(?:[.,]\d+)?")]
    private static partial Regex Numbers();
}
