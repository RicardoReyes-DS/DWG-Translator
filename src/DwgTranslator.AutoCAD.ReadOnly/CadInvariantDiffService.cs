using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using DwgTranslator.Application;
using DwgTranslator.Contracts;

namespace DwgTranslator.AutoCAD.Adapter;

/// <summary>Opens both drawings through AutoCAD's Database API without save or document activation.</summary>
internal static class CadInvariantDiffService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    public static Result<object> Compare(WireEnvelope request, CancellationToken cancellationToken)
    {
        CadInvariantDiffRequestPayload? payload;
        try { payload = request.Payload?.Deserialize<CadInvariantDiffRequestPayload>(Json); }
        catch (JsonException) { payload = null; }
        if (payload is null || !payload.ReadOnly || !Valid(payload.SourcePath, payload.ExpectedSourceHash) ||
            !Valid(payload.CandidatePath, payload.ExpectedCandidateHash) || PathsEqual(payload.SourcePath, payload.CandidatePath))
            return Failure("IPC_PAYLOAD_INVALID", "The read-only invariant differential request is invalid.");

        var sourceBefore = Hash(payload.SourcePath);
        var candidateBefore = Hash(payload.CandidatePath);
        if (!string.Equals(sourceBefore, payload.ExpectedSourceHash, StringComparison.Ordinal) ||
            !string.Equals(candidateBefore, payload.ExpectedCandidateHash, StringComparison.Ordinal))
            return Failure("SOURCE_CHANGED", "A drawing changed before invariant differential inspection.");
        var sourceSnapshot = Snapshot(payload.SourcePath, cancellationToken);
        var candidateSnapshot = Snapshot(payload.CandidatePath, cancellationToken);
        var sourceAfter = Hash(payload.SourcePath);
        var candidateAfter = Hash(payload.CandidatePath);
        if (!string.Equals(sourceBefore, sourceAfter, StringComparison.Ordinal) ||
            !string.Equals(candidateBefore, candidateAfter, StringComparison.Ordinal))
            return Failure("SOURCE_CHANGED", "A drawing changed during invariant differential inspection.");
        var compact = CadInvariantDiffCompactPolicy.Create(sourceAfter, candidateAfter, sourceSnapshot.Entities, candidateSnapshot.Entities);
        return compact.IsSuccess
            ? Results.Success<object>(compact.Value!)
            : Results.Failure<object>(compact.Error!);
    }

    private static SnapshotResult Snapshot(Database database, CancellationToken cancellationToken)
    {
        var rows = new List<CadInvariantDiffEntity>();
        using var transaction = database.TransactionManager.StartOpenCloseTransaction();
        var table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
        foreach (ObjectId recordId in table.Cast<ObjectId>().OrderBy(id => id.Handle.Value))
        {
            var record = (BlockTableRecord)transaction.GetObject(recordId, OpenMode.ForRead);
            foreach (ObjectId entityId in record.Cast<ObjectId>().OrderBy(id => id.Handle.Value))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entity = (Entity)transaction.GetObject(entityId, OpenMode.ForRead);
                var extents = Extents(entity);
                var textHash = entity switch
                {
                    DBText text => HashText(text.TextString),
                    MText mtext => HashText(mtext.Contents),
                    _ => null
                };
                var raw = string.Join('|', entity.Handle.Value.ToString("X", CultureInfo.InvariantCulture),
                    record.Handle.Value.ToString("X", CultureInfo.InvariantCulture), entity.GetRXClass().DxfName,
                    entity.GetType().FullName ?? entity.GetRXClass().Name, entity.Layer,
                    entity.ColorIndex.ToString(CultureInfo.InvariantCulture), entity.LinetypeId.Handle.Value.ToString("X", CultureInfo.InvariantCulture),
                    ((int)entity.LineWeight).ToString(CultureInfo.InvariantCulture), extents?.Minimum ?? "NO_EXTENTS", extents?.Maximum ?? "NO_EXTENTS", textHash ?? "NO_TEXT");
                rows.Add(new CadInvariantDiffEntity
                {
                    OwnerHandle = record.Handle.Value.ToString("X", CultureInfo.InvariantCulture),
                    EntityHandle = entity.Handle.Value.ToString("X", CultureInfo.InvariantCulture),
                    DxfType = entity.GetRXClass().DxfName,
                    RuntimeClass = entity.GetType().FullName ?? entity.GetRXClass().Name,
                    IsTextEntity = entity is DBText or MText,
                    Layer = entity.Layer,
                    ColorIndex = entity.ColorIndex,
                    LinetypeHandle = entity.LinetypeId.Handle.Value.ToString("X", CultureInfo.InvariantCulture),
                    Lineweight = (int)entity.LineWeight,
                    Extents = extents,
                    TextPayloadHash = textHash,
                    Fingerprint = HashText(raw)
                });
            }
        }
        var ordered = rows.OrderBy(row => row.OwnerHandle, StringComparer.Ordinal).ThenBy(row => row.EntityHandle, StringComparer.Ordinal).ToList();
        return new(HashText(string.Join('\n', ordered.Select(row => row.Fingerprint))), ordered);
    }

    private static SnapshotResult Snapshot(string path, CancellationToken cancellationToken)
    {
        using var database = Open(path);
        return Snapshot(database, cancellationToken);
    }

    private static CadInvariantExtents? Extents(Entity entity)
    {
        try
        {
            var value = entity.GeometricExtents;
            return new CadInvariantExtents { Minimum = Point(value.MinPoint), Maximum = Point(value.MaxPoint) };
        }
        catch (Autodesk.AutoCAD.Runtime.Exception) { return null; }
    }

    private static Database Open(string path) { var database = new Database(false, true); database.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, null); database.CloseInput(true); return database; }
    private static bool Valid(string path, string hash) => Path.IsPathFullyQualified(path) && File.Exists(path) &&
        string.Equals(Path.GetExtension(path), ".dwg", StringComparison.OrdinalIgnoreCase) && ContractPatterns.Sha256().IsMatch(hash);
    private static bool PathsEqual(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    private static string Hash(string path) { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); return "sha256:" + Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    private static string HashText(string value) => "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Point(Autodesk.AutoCAD.Geometry.Point3d point) => string.Join(',', point.X.ToString("R", CultureInfo.InvariantCulture), point.Y.ToString("R", CultureInfo.InvariantCulture), point.Z.ToString("R", CultureInfo.InvariantCulture));
    private static Result<object> Failure(string code, string message) => Results.Failure<object>(new ContractError(code, ErrorCategory.Integrity, message, false));
    private sealed record SnapshotResult(string Fingerprint, List<CadInvariantDiffEntity> Entities);
}
