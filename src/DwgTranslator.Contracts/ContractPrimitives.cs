using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DwgTranslator.Contracts;

public static class ContractV1
{
    public const string SchemaVersion = "1.0.0";
    // Visual validation evidence for large drawings is bounded but can legitimately
    // exceed the historic 1 MiB frame. Keep a finite, centrally-defined ceiling.
    public const int MaxFrameBytes = 16_777_216;
    public const int MaxErrorMessageLength = 500;
}

public static partial class ContractPatterns
{
    [GeneratedRegex("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    public static partial Regex Sha256();

    [GeneratedRegex("^seg_sha256_[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    public static partial Regex SegmentId();

    [GeneratedRegex("^[a-z]+(?:[.-][a-z]+)*$", RegexOptions.CultureInvariant)]
    public static partial Regex MessageType();

}

/// <summary>
/// Closed, content-free lifecycle stages emitted by the VisualStrictV2 writer.
/// Values are safe for durable receipts: they contain neither paths nor drawing text.
/// </summary>
public static class CadWriteTechnicalStages
{
    public const string Preflight = "visual-strict-v2.preflight";
    public const string NormalizedBaselineCopy = "visual-strict-v2.normalized-baseline.copy";
    public const string NormalizedBaselineOpen = "visual-strict-v2.normalized-baseline.open";
    public const string NormalizedBaselinePrime = "visual-strict-v2.normalized-baseline.prime";
    public const string NormalizedBaselineSave = "visual-strict-v2.normalized-baseline.save";
    public const string NormalizedBaselineStabilize = "visual-strict-v2.normalized-baseline.stabilize";
    public const string SourceRevalidate = "visual-strict-v2.source.revalidate";
    public const string CandidateCopy = "visual-strict-v2.candidate.copy";
    public const string CandidateOpen = "visual-strict-v2.candidate.open";
    public const string CandidateApply = "visual-strict-v2.candidate.apply";
    public const string CandidateSave = "visual-strict-v2.candidate.save";
    public const string CandidateStabilize = "visual-strict-v2.candidate.stabilize";
    public const string InvariantCompare = "visual-strict-v2.invariant.compare";
    public const string NormalizedBaselineCleanup = "visual-strict-v2.normalized-baseline.cleanup";
    public const string CandidatePromote = "visual-strict-v2.candidate.promote";
    public const string PromotionVerify = "visual-strict-v2.promotion.verify";

    private static readonly HashSet<string> Values = new(StringComparer.Ordinal)
    {
        Preflight, NormalizedBaselineCopy, NormalizedBaselineOpen, NormalizedBaselinePrime,
        NormalizedBaselineSave, NormalizedBaselineStabilize, SourceRevalidate, CandidateCopy,
        CandidateOpen, CandidateApply, CandidateSave, CandidateStabilize, InvariantCompare,
        NormalizedBaselineCleanup, CandidatePromote, PromotionVerify
    };

    public static bool IsSupported(string? value) => value is not null && Values.Contains(value);
}

/// <summary>
/// Closed subset of Autodesk ErrorStatus names that the write boundary is allowed to persist.
/// Unknown values are intentionally omitted rather than copied into durable evidence.
/// </summary>
public static class CadNativeErrorStatuses
{
    private static readonly HashSet<string> Values = new(StringComparer.Ordinal)
    {
        "InvalidInput", "InvalidOpenState", "NotOpenForRead", "NotOpenForWrite",
        "WasNotOpenForWrite", "NoDatabase", "GeneralModelingFailure", "FilerError",
        "DwgNeedsRecovery", "FileAccessErr", "FileSystemErr", "FileInternalErr",
        "FileLockedByAutoCAD", "FileSharingViolation", "OutOfDisk", "OutOfMemory",
        "DatabaseObjectsOpen", "LockViolation", "LockConflict", "NoDocument",
        "TargetDocNotQuiescent", "InvalidContext"
    };

    public static bool IsSupported(string? value) => value is not null && Values.Contains(value);
}

public enum OperationStatus
{
    Succeeded,
    Failed,
    Accepted,
    InProgress,
    CancelPending,
    Cancelled
}

public enum ErrorCategory
{
    Input,
    Unsupported,
    Integrity,
    Environment,
    Concurrency,
    Contract,
    Transport,
    Translation,
    Storage,
    Security,
    Configuration,
    Internal
}

public sealed record ContractError(
    [property: JsonRequired] string Code,
    [property: JsonRequired] ErrorCategory Category,
    [property: JsonRequired] string Message,
    [property: JsonRequired] bool Retryable,
    string? OperatorAction = null,
    string? DiagnosticId = null,
    CadInvariantDiagnostics? InvariantDiagnostics = null,
    string? TechnicalStage = null,
    string? NativeErrorStatus = null);

public sealed record Result<T>(T? Value, ContractError? Error)
{
    public bool IsSuccess => Error is null;
}

public static class Results
{
    public static Result<T> Success<T>(T value) => new(value, null);
    public static Result<T> Failure<T>(ContractError error) => new(default, error);
}
