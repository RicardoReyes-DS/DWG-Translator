namespace DwgTranslator.Domain;

public sealed record DomainResult<T>(T? Value, string? ErrorCode)
{
    public bool IsSuccess => ErrorCode is null;
}

public static class DomainResults
{
    public static DomainResult<T> Success<T>(T value) => new(value, null);
    public static DomainResult<T> Failure<T>(string code) => new(default, code);
}
