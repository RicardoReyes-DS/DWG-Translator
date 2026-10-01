using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DwgTranslator.Contracts;

namespace DwgTranslator.Application;

public sealed record CadSegmentAddress(
    string EntityType,
    string Handle,
    string Space,
    string? Layout,
    string Layer,
    int SubIndex);

public sealed record CadSegmentIdentity(string SegmentId, string SourceTextHash);

public static class CadSegmentIdentityV1
{
    private const string DomainSeparator = "dwg-translator/segment-id/v1";

    public static Result<CadSegmentIdentity> Create(
        string sourceHash,
        string drawingFingerprint,
        CadSegmentAddress address,
        string sourceText)
    {
        if (sourceHash is null || drawingFingerprint is null ||
            !ContractPatterns.Sha256().IsMatch(sourceHash) ||
            !ContractPatterns.Sha256().IsMatch(drawingFingerprint) ||
            address is null ||
            sourceText is null ||
            sourceText.Length > 65_535)
        {
            return Failure("CAD_SEGMENT_INPUT_INVALID", "Segment identity input is missing or outside contract limits.");
        }

        var addressValidation = ValidateAddress(address);
        if (!addressValidation.IsSuccess)
            return Results.Failure<CadSegmentIdentity>(addressValidation.Error!);

        using var identityHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(identityHash, DomainSeparator);
        Append(identityHash, ContractV1.SchemaVersion);
        Append(identityHash, sourceHash);
        Append(identityHash, drawingFingerprint);
        Append(identityHash, address.EntityType);
        Append(identityHash, address.Space);
        Append(identityHash, address.Layout ?? string.Empty);
        Append(identityHash, string.Empty); // blockPath is frozen to an empty array in MVP v1.
        Append(identityHash, address.Handle);
        Append(identityHash, address.SubIndex.ToString(CultureInfo.InvariantCulture));

        var segmentHash = LowerHex(identityHash.GetHashAndReset());
        var textHash = LowerHex(SHA256.HashData(Encoding.UTF8.GetBytes(sourceText)));
        return Results.Success(new CadSegmentIdentity($"seg_sha256_{segmentHash}", $"sha256:{textHash}"));
    }

    private static Result<bool> ValidateAddress(CadSegmentAddress address)
    {
        if (address.EntityType is not ("TEXT" or "MTEXT") ||
            address.Handle is null || address.Handle.Length is < 1 or > 32 ||
            address.Handle.Any(character => character is not (>= '0' and <= '9') and not (>= 'A' and <= 'F')) ||
            string.IsNullOrEmpty(address.Layer) || address.Layer.Length > 255 ||
            address.SubIndex != 0)
        {
            return Failure<bool>("CAD_SEGMENT_ADDRESS_INVALID", "The direct TEXT/MTEXT address is invalid for MVP v1.");
        }

        var layoutValid = address.Space switch
        {
            "ModelSpace" => address.Layout is null,
            "PaperSpace" => !string.IsNullOrEmpty(address.Layout) && address.Layout.Length <= 255,
            _ => false
        };
        return layoutValid
            ? Results.Success(true)
            : Failure<bool>("CAD_SEGMENT_LAYOUT_INVALID", "ModelSpace requires no layout; PaperSpace requires a layout name.");
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static string LowerHex(byte[] value) => Convert.ToHexString(value).ToLowerInvariant();

    private static Result<CadSegmentIdentity> Failure(string code, string message) =>
        Results.Failure<CadSegmentIdentity>(new ContractError(code, ErrorCategory.Contract, message, false));

    private static Result<T> Failure<T>(string code, string message) =>
        Results.Failure<T>(new ContractError(code, ErrorCategory.Contract, message, false));
}
