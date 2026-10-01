using System.Text.RegularExpressions;

namespace DwgTranslator.Domain;

public readonly record struct JobId
{
    private JobId(Guid value) => Value = value;
    public Guid Value { get; }
    public static DomainResult<JobId> Create(Guid value) => value == Guid.Empty
        ? DomainResults.Failure<JobId>("JOB_ID_EMPTY")
        : DomainResults.Success(new JobId(value));
}

public readonly record struct ContentHash
{
    private static readonly Regex Pattern = new("^sha256:[0-9a-f]{64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private ContentHash(string value) => Value = value;
    public string Value { get; }
    public static DomainResult<ContentHash> Create(string? value) => value is not null && Pattern.IsMatch(value)
        ? DomainResults.Success(new ContentHash(value))
        : DomainResults.Failure<ContentHash>("HASH_INVALID");
}

public readonly record struct LanguageTag
{
    private static readonly Regex Pattern = new(
        "^[A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8})*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private LanguageTag(string value) => Value = value;
    public string Value { get; }

    public static DomainResult<LanguageTag> Create(string? value)
    {
        if (value is null || !Pattern.IsMatch(value))
            return DomainResults.Failure<LanguageTag>("LANGUAGE_TAG_INVALID");

        var parts = value.Split('-');
        var canonical = string.Join('-', parts.Select((part, index) => index == 0
            ? part.ToLowerInvariant()
            : part.Length == 2 && part.All(char.IsLetter)
                ? part.ToUpperInvariant()
                : part.Length == 4 && part.All(char.IsLetter)
                    ? char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()
                    : part.ToLowerInvariant()));
        return DomainResults.Success(new LanguageTag(canonical));
    }
}
