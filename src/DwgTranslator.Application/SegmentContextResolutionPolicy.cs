using System.Security.Cryptography;
using System.Text;
using DwgTranslator.Contracts;

namespace DwgTranslator.Application;

/// <summary>
/// Context-conflict resolutions require a separately approved, portable authority.
/// This candidate has no such authority and therefore rejects every conflict.
/// </summary>
public static class SegmentContextResolutionPolicy
{
    public const string Version = "segment-context-resolution/1.0";
    public const string VerticalSignal = "VERTICAL_LEVEL_CONTEXT_CONFLICT";

    public static bool IsStructurallyValid(SegmentContextResolutionAuthority? authority) => false;

    public static bool ValidForReceipt(
        ReviewAutomationReceipt receipt,
        TranslationReviewSnapshot review,
        IReadOnlyList<CadTextSegment> segments) =>
        receipt.SegmentContextResolutions is null or { Count: 0 } &&
        !segments.Any(segment => segment.SemanticContext?.Signals.Contains(
            VerticalSignal, StringComparer.Ordinal) == true);

    public static string TextHash(string value) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
