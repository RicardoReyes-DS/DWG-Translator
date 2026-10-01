using System.Security.Cryptography;
using System.Text;
using DwgTranslator.Contracts;

namespace DwgTranslator.Application;

public static class CadDrawingFingerprintV1
{
    private const string DomainSeparator = "dwg-translator/drawing-fingerprint/v1\n";

    public static Result<string> Create(Guid databaseFingerprintGuid)
    {
        if (databaseFingerprintGuid == Guid.Empty)
        {
            return Results.Failure<string>(new ContractError(
                "CAD_DRAWING_FINGERPRINT_GUID_EMPTY",
                ErrorCategory.Integrity,
                "AutoCAD Database.FingerprintGuid must not be empty.",
                false));
        }

        var material = Encoding.UTF8.GetBytes(DomainSeparator + databaseFingerprintGuid.ToString("D"));
        var hash = Convert.ToHexString(SHA256.HashData(material)).ToLowerInvariant();
        return Results.Success($"sha256:{hash}");
    }
}
