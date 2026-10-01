using System.Collections;
using System.Security.Cryptography;
using System.Text.Json;
using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using DwgTranslator.Application;
using DwgTranslator.Contracts;

namespace DwgTranslator.AutoCAD.Adapter;

internal static class CadReadService
{
    private static readonly JsonSerializerOptions RequestOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    public static Result<object> Inspect(WireEnvelope request, CancellationToken cancellationToken)
    {
        var decoded = Decode<InspectRequest>(request); if (!decoded.IsSuccess) return Results.Failure<object>(decoded.Error!); var p = decoded.Value!;
        var preflight = ValidateRequest(p.SourcePath, p.ExpectedSourceHash, p.ReadOnly && p.ClassifyFields && !p.FollowExternalReferences && p.IncludeSpaces is not null && p.IncludeSpaces.SequenceEqual(new[] { "ModelSpace", "PaperSpace", "BlockDefinitions" }) && p.SupportedEntityTypes is not null && p.SupportedEntityTypes.SequenceEqual(new[] { "TEXT", "MTEXT" })); if (!preflight.IsSuccess) return Results.Failure<object>(preflight.Error!);
        var before = FileHash(p.SourcePath); if (before != p.ExpectedSourceHash) return Failure("SOURCE_CHANGED", ErrorCategory.Integrity, "Source hash differs before inspection.");
        using var database = OpenReadOnly(p.SourcePath); var snapshot = ReadSnapshot(database, before, cancellationToken); var after = FileHash(p.SourcePath); if (before != after) return Failure("SOURCE_CHANGED", ErrorCategory.Integrity, "Source hash changed during inspection.");
        return Results.Success<object>(new CadInspectResponsePayload { SourceHash = after, Autocad = new CadHostDescriptor { Product = "AutoCAD", Year = 2026, ApiVersion = "25.1.179.0" }, DrawingFingerprint = snapshot.DrawingFingerprint, Inventory = snapshot.Inventory, SupportedCount = snapshot.Supported.Count, Unsupported = snapshot.Unsupported, Warnings = [] });
    }
    public static Result<object> Extract(WireEnvelope request, CancellationToken cancellationToken)
    {
        var decoded = Decode<ExtractRequest>(request); if (!decoded.IsSuccess) return Results.Failure<object>(decoded.Error!); var p = decoded.Value!;
        var preflight = ValidateRequest(p.SourcePath, p.ExpectedSourceHash, p.ExcludeFieldBackedText && p.EntityTypes is not null && p.EntityTypes.SequenceEqual(new[] { "TEXT", "MTEXT" })); if (!preflight.IsSuccess) return Results.Failure<object>(preflight.Error!);
        var before = FileHash(p.SourcePath); if (before != p.ExpectedSourceHash) return Failure("SOURCE_CHANGED", ErrorCategory.Integrity, "Source hash differs before extraction.");
        using var database = OpenReadOnly(p.SourcePath); var snapshot = ReadSnapshot(database, before, cancellationToken); var after = FileHash(p.SourcePath); if (before != after) return Failure("SOURCE_CHANGED", ErrorCategory.Integrity, "Source hash changed during extraction.");
        var prepared = new List<(SupportedText Item, CadSegmentIdentity Identity)>();
        foreach (var item in snapshot.Supported)
        {
            var identity = CadSegmentIdentityV1.Create(after, snapshot.DrawingFingerprint, new CadSegmentAddress(item.EntityType, item.Handle, item.Space, item.Layout, item.Layer, 0), item.Text); if (!identity.IsSuccess) return Results.Failure<object>(identity.Error!);
            prepared.Add((item, identity.Value!));
        }
        var contexts = CadSemanticContextBuilder.Build(prepared.Select(value => new CadSemanticContextInput(
            value.Identity.SegmentId, value.Item.EntityType, value.Item.Handle, value.Item.Space, value.Item.Layout, [],
            value.Item.Layer, value.Item.Text, value.Identity.SourceTextHash, value.Item.AnchorX, value.Item.AnchorY,
            value.Item.TextHeight, value.Item.AnchorSource, Path.GetFileName(p.SourcePath))).ToArray(),
            CadSemanticContextBuilder.CurrentPolicyVersion);
        if (!contexts.IsSuccess) return Results.Failure<object>(contexts.Error!);
        var segments = prepared.Select(value => new CadTextSegment
        {
            SegmentId = value.Identity.SegmentId,
            Entity = new CadEntityReference { Type = value.Item.EntityType, Handle = value.Item.Handle, Space = value.Item.Space,
                Layout = value.Item.Layout, BlockPath = [], Layer = value.Item.Layer, SubIndex = 0 },
            SourceText = value.Item.Text,
            SourceTextHash = value.Identity.SourceTextHash,
            LineBreakStyle = CadExtractionResultPolicy.ExpectedLineBreakStyle(value.Item.EntityType, value.Item.Text),
            ProtectedTokens = CadProtectedTokenPolicy.Extract(value.Item.Text).ToList(),
            FieldClassification = "None",
            State = "Extracted",
            SemanticContext = contexts.Value![value.Identity.SegmentId]
        }).ToList();
        return Results.Success<object>(new CadExtractResponsePayload { SourceHash = after, Segments = segments, ExcludedFieldCount = snapshot.ExcludedFieldCount });
    }
    private static Database OpenReadOnly(string path) { var database = new Database(false, true); database.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, null); database.CloseInput(true); return database; }
    private static CadSnapshot ReadSnapshot(Database database, string sourceHash, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(database.FingerprintGuid, out var fingerprintGuid)) throw new InvalidDataException("CAD_DRAWING_FINGERPRINT_GUID_INVALID");
        var fingerprint = CadDrawingFingerprintV1.Create(fingerprintGuid); if (!fingerprint.IsSuccess) throw new InvalidDataException(fingerprint.Error!.Code);
        var inventory = new Dictionary<string, int>(StringComparer.Ordinal); var exclusions = new Dictionary<UnsupportedKey, int>(); var supported = new List<SupportedText>(); var excludedFields = 0;
        using var transaction = database.TransactionManager.StartOpenCloseTransaction();
        foreach (var scope in ReadLayouts(database, transaction).OrderBy(x => x.Space == "ModelSpace" ? 0 : 1).ThenBy(x => x.Layout, StringComparer.Ordinal))
        {
            var record = (BlockTableRecord)transaction.GetObject(scope.RecordId, OpenMode.ForRead);
            foreach (ObjectId id in record.Cast<ObjectId>().OrderBy(x => x.Handle.Value)) { cancellationToken.ThrowIfCancellationRequested(); var entity = (Entity)transaction.GetObject(id, OpenMode.ForRead); InventoryDirect(entity, scope, transaction, inventory, exclusions, supported, ref excludedFields); }
        }
        var table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
        foreach (ObjectId recordId in table.Cast<ObjectId>().OrderBy(x => x.Handle.Value))
        {
            var record = (BlockTableRecord)transaction.GetObject(recordId, OpenMode.ForRead); if (record.IsLayout || record.IsFromExternalReference || record.IsDependent) continue;
            foreach (ObjectId id in record.Cast<ObjectId>().OrderBy(x => x.Handle.Value)) { cancellationToken.ThrowIfCancellationRequested(); var entity = (Entity)transaction.GetObject(id, OpenMode.ForRead); var type = EntityType(entity); Increment(inventory, type); Add(exclusions, new("SharedBlockDefinition", type, "BLOCK_DEFINITION_OUT_OF_SCOPE")); }
        }
        var ordered = supported.OrderBy(x => x.Space == "ModelSpace" ? 0 : 1).ThenBy(x => x.Layout, StringComparer.Ordinal).ThenBy(x => x.Handle, StringComparer.Ordinal).ToList();
        var summaries = exclusions.OrderBy(x => x.Key.Classification, StringComparer.Ordinal).ThenBy(x => x.Key.EntityType, StringComparer.Ordinal).ThenBy(x => x.Key.ReasonCode, StringComparer.Ordinal).Select(x => new CadUnsupportedSummary { Classification = x.Key.Classification, EntityType = x.Key.EntityType, ReasonCode = x.Key.ReasonCode, Count = x.Value }).ToList();
        return new(fingerprint.Value!, inventory, ordered, summaries, excludedFields);
    }
    private static List<LayoutScope> ReadLayouts(Database database, Transaction transaction)
    {
        var result = new List<LayoutScope>(); var dictionary = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
        foreach (DBDictionaryEntry entry in dictionary) { var layout = (Layout)transaction.GetObject(entry.Value, OpenMode.ForRead); result.Add(new(layout.ModelType ? "ModelSpace" : "PaperSpace", layout.ModelType ? null : layout.LayoutName, layout.BlockTableRecordId)); } return result;
    }
    private static void InventoryDirect(Entity entity, LayoutScope scope, Transaction transaction, Dictionary<string, int> inventory, Dictionary<UnsupportedKey, int> exclusions, List<SupportedText> supported, ref int fields)
    {
        var type = EntityType(entity); Increment(inventory, type);
        if (entity is AttributeDefinition) Add(exclusions, new("PlannedEntity", "ATTDEF", "NOT_IN_MVP"));
        else if (entity is DBText text) AddText(type, text.TextString, text.HasFields, entity, scope, exclusions, supported, ref fields);
        else if (entity is MText mtext) AddText(type, mtext.Contents, mtext.HasFields, entity, scope, exclusions, supported, ref fields);
        else Add(exclusions, entity switch { Table => new("PlannedEntity", type, "NOT_IN_MVP"), BlockReference => new("SharedBlockDefinition", type, "BLOCK_REFERENCE_OUT_OF_SCOPE"), MLeader or Dimension => new("DerivedGeometry", type, "DERIVED_TEXT_OUT_OF_SCOPE"), _ => new("CustomObject", type, "NOT_IN_MVP") });
        if (entity is BlockReference block) foreach (ObjectId attributeId in block.AttributeCollection) { _ = transaction.GetObject(attributeId, OpenMode.ForRead); Increment(inventory, "ATTRIB"); Add(exclusions, new("PlannedEntity", "ATTRIB", "NOT_IN_MVP")); }
    }
    private static void AddText(string type, string text, bool hasFields, Entity entity, LayoutScope scope, Dictionary<UnsupportedKey, int> exclusions, List<SupportedText> supported, ref int fields)
    {
        if (hasFields) { Add(exclusions, new("FieldBacked", type, "FIELD_BACKED_EXCLUDED")); fields++; }
        else if (string.IsNullOrWhiteSpace(text)) Add(exclusions, new("Unknown", type, "EMPTY_TEXT_EXCLUDED"));
        else
        {
            var geometry = SemanticGeometry(entity);
            supported.Add(new(type, entity.Handle.Value.ToString("X", CultureInfo.InvariantCulture), scope.Space, scope.Layout,
                entity.Layer, text, geometry.X, geometry.Y, geometry.TextHeight, geometry.AnchorSource));
        }
    }
    private static SemanticTextGeometry SemanticGeometry(Entity entity)
    {
        var height = entity switch
        {
            DBText text => text.Height,
            MText mtext => mtext.TextHeight,
            _ => throw new InvalidDataException("CAD_SEMANTIC_GEOMETRY_ENTITY_INVALID")
        };
        if (!double.IsFinite(height) || height <= 0) throw new InvalidDataException("CAD_SEMANTIC_TEXT_HEIGHT_INVALID");
        try
        {
            var extents = entity.GeometricExtents;
            var x = (extents.MinPoint.X + extents.MaxPoint.X) / 2d;
            var y = (extents.MinPoint.Y + extents.MaxPoint.Y) / 2d;
            if (double.IsFinite(x) && double.IsFinite(y)) return new(x, y, height, "ExtentsCenter");
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            // Some valid text entities have unavailable extents until regenerated; use their native anchor only.
        }
        var anchor = entity switch
        {
            DBText text => text.Position,
            MText mtext => mtext.Location,
            _ => throw new InvalidDataException("CAD_SEMANTIC_GEOMETRY_ENTITY_INVALID")
        };
        if (!double.IsFinite(anchor.X) || !double.IsFinite(anchor.Y)) throw new InvalidDataException("CAD_SEMANTIC_ANCHOR_INVALID");
        return new(anchor.X, anchor.Y, height, "EntityPosition");
    }
    private static string EntityType(Entity entity) => entity switch { AttributeDefinition => "ATTDEF", DBText => "TEXT", MText => "MTEXT", Table => "TABLE", MLeader => "MLEADER", BlockReference => "INSERT", Dimension => "DIMENSION", _ => entity.GetRXClass().DxfName.ToUpperInvariant() };
    private static void Increment(Dictionary<string, int> values, string key) => values[key] = values.TryGetValue(key, out var count) ? count + 1 : 1; private static void Add(Dictionary<UnsupportedKey, int> values, UnsupportedKey key) => values[key] = values.TryGetValue(key, out var count) ? count + 1 : 1;
    private static string FileHash(string path) { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); return "sha256:" + Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    private static Result<bool> ValidateRequest(string path, string hash, bool flags) => !flags || string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !string.Equals(Path.GetExtension(path), ".dwg", StringComparison.OrdinalIgnoreCase) || !File.Exists(path) || !ContractPatterns.Sha256().IsMatch(hash) ? Results.Failure<bool>(new ContractError("VALIDATION_ERROR", ErrorCategory.Input, "The read-only CAD request is invalid.", false)) : Results.Success(true);
    private static Result<T> Decode<T>(WireEnvelope request) where T : class { try { var value = request.Payload?.Deserialize<T>(RequestOptions); return value is null ? Failure<T>() : Results.Success(value); } catch (JsonException) { return Failure<T>(); } }
    private static Result<T> Failure<T>() => Results.Failure<T>(new ContractError("IPC_PAYLOAD_INVALID", ErrorCategory.Contract, "CAD request payload is invalid.", false)); private static Result<object> Failure(string code, ErrorCategory category, string message) => Results.Failure<object>(new ContractError(code, category, message, false));
    private sealed record InspectRequest(string SourcePath, string ExpectedSourceHash, bool ReadOnly, string[] IncludeSpaces, string[] SupportedEntityTypes, bool ClassifyFields, bool FollowExternalReferences);
    private sealed record ExtractRequest(string SourcePath, string ExpectedSourceHash, string[] EntityTypes, bool ExcludeFieldBackedText);
    private sealed record LayoutScope(string Space, string? Layout, ObjectId RecordId);
    private sealed record SupportedText(string EntityType, string Handle, string Space, string? Layout, string Layer, string Text,
        double AnchorX, double AnchorY, double TextHeight, string AnchorSource);
    private sealed record SemanticTextGeometry(double X, double Y, double TextHeight, string AnchorSource);
    private sealed record UnsupportedKey(string Classification, string EntityType, string ReasonCode);
    private sealed record CadSnapshot(string DrawingFingerprint, Dictionary<string, int> Inventory, List<SupportedText> Supported, List<CadUnsupportedSummary> Unsupported, int ExcludedFieldCount);
}
