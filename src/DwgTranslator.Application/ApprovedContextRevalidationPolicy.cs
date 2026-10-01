using System.Security.Cryptography;
using System.Text;
using DwgTranslator.Contracts;

namespace DwgTranslator.Application;

public static class ApprovedContextRevalidationPolicy
{
    public const string Version = "approved-context-revalidation/1.0";

    public static string ConflictSetHash(IEnumerable<string> segmentIds) =>
        Hash("dwg-translator/context-revalidation-conflicts/v1\n" +
             string.Join("\n", segmentIds.Order(StringComparer.Ordinal)));

    public static string BindingHash(ApprovedContextRevalidationBinding binding)
    {
        var material = string.Join("\n", new[]
        {
            Version,
            binding.ExpectedJobVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            binding.ExpectedJobArtifactHash,
            binding.ExpectedReviewVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            binding.ExpectedReviewHash,
            binding.ExpectedReceiptHash,
            binding.ExpectedContextPolicyVersion,
            binding.ExpectedContextHash,
            binding.TargetContextPolicyVersion,
            binding.TargetContextHash,
            binding.ManifestBoundBasename,
            binding.RowCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            binding.ExpectedFallbackCandidateCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            binding.ExpectedLocalEvidenceCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            string.Join("\u001f", binding.RequiredResolutionSegmentIds.Order(StringComparer.Ordinal)),
            binding.ConflictSetHash
        });
        return Hash(material);
    }

    public static bool IsValid(ApprovedContextRevalidationBinding? binding)
    {
        if (binding is null || binding.ExpectedJobVersion < 0 || binding.ExpectedReviewVersion < 0 ||
            binding.RowCount < 1 || binding.ExpectedFallbackCandidateCount < 0 ||
            binding.ExpectedLocalEvidenceCount < 0 ||
            binding.ExpectedFallbackCandidateCount + binding.ExpectedLocalEvidenceCount != binding.RowCount ||
            binding.RequiredResolutionSegmentIds is null ||
            binding.RequiredResolutionSegmentIds.Count > 1 ||
            binding.RequiredResolutionSegmentIds.Distinct(StringComparer.Ordinal).Count() != binding.RequiredResolutionSegmentIds.Count ||
            binding.RequiredResolutionSegmentIds.Any(id => !ContractPatterns.SegmentId().IsMatch(id)) ||
            binding.ExpectedContextPolicyVersion != CadSemanticContextBuilder.PolicyVersionOneOne ||
            binding.TargetContextPolicyVersion != CadSemanticContextBuilder.PolicyVersionOneTwo ||
            string.IsNullOrWhiteSpace(binding.ManifestBoundBasename) || binding.ManifestBoundBasename.Length > 255 ||
            !string.Equals(Path.GetFileName(binding.ManifestBoundBasename), binding.ManifestBoundBasename, StringComparison.Ordinal))
            return false;
        foreach (var hash in new[] { binding.ExpectedJobArtifactHash, binding.ExpectedReviewHash,
                     binding.ExpectedReceiptHash, binding.ExpectedContextHash, binding.TargetContextHash,
                     binding.ConflictSetHash, binding.BindingHash })
            if (!ContractPatterns.Sha256().IsMatch(hash ?? string.Empty)) return false;
        return string.Equals(binding.ConflictSetHash,
                   ConflictSetHash(binding.RequiredResolutionSegmentIds), StringComparison.Ordinal) &&
               string.Equals(binding.BindingHash, BindingHash(binding), StringComparison.Ordinal);
    }

    private static string Hash(string value) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
