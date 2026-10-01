using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DwgTranslator.Application;
using DwgTranslator.Contracts;

namespace DwgTranslator.Core.Tests;

[TestClass]
public sealed class CadSemanticContextTests
{
    [TestMethod]
    public void BuilderIsOrderIndependentBoundedAndAggregateHashIsStable()
    {
        var inputs = new[]
        {
            Input('a', "A1", 0, 0), Input('b', "A2", 1, 0), Input('c', "A3", 2, 0),
            Input('d', "A4", 3, 0), Input('e', "A5", 4, 0), Input('f', "A6", 5, 0),
            Input('1', "A7", 100, 0)
        };

        var first = CadSemanticContextBuilder.Build(inputs);
        var second = CadSemanticContextBuilder.Build(Enumerable.Reverse(inputs).ToArray());

        Assert.IsTrue(first.IsSuccess, first.Error?.Code);
        Assert.IsTrue(second.IsSuccess, second.Error?.Code);
        foreach (var input in inputs)
            Assert.AreEqual(JsonSerializer.Serialize(first.Value![input.SegmentId]), JsonSerializer.Serialize(second.Value![input.SegmentId]));
        Assert.AreEqual(CadSemanticContextBuilder.MaximumNeighbors, first.Value![inputs[0].SegmentId].Neighbors.Count);
        Assert.IsFalse(first.Value[inputs[0].SegmentId].Neighbors.Any(item => item.SegmentId == inputs[^1].SegmentId));

        var firstSegments = Segments(inputs, first.Value);
        var secondSegments = Segments(Enumerable.Reverse(inputs).ToArray(), second.Value!);
        var firstAggregate = CadSemanticContextBuilder.AggregateHash(firstSegments);
        var secondAggregate = CadSemanticContextBuilder.AggregateHash(secondSegments);
        Assert.IsTrue(firstAggregate.IsSuccess, firstAggregate.Error?.Code);
        Assert.AreEqual(firstAggregate.Value, secondAggregate.Value);
    }

    [TestMethod]
    public void DrawingHintClassifiesControlsWithoutPersistingFilenameAndConflictsFailClosed()
    {
        const string drawingName = "SYNTHETIC-BMS-EXAMPLE.dwg";
        var controls = Input('a', "B1", 0, 0, layer: "NOTES", drawingHint: drawingName);
        var conflicted = Input('b', "B2", 10, 0, layer: "ELEC-POWER", drawingHint: drawingName);

        var result = CadSemanticContextBuilder.Build([controls, conflicted]);

        Assert.IsTrue(result.IsSuccess, result.Error?.Code);
        var controlsContext = result.Value![controls.SegmentId];
        Assert.AreEqual("Controls", controlsContext.Discipline);
        CollectionAssert.Contains(controlsContext.DisciplineEvidence, "DRAWING_NAME_CONTROLS");
        var conflictedContext = result.Value[conflicted.SegmentId];
        Assert.AreEqual("Unknown", conflictedContext.Discipline);
        Assert.IsTrue(conflictedContext.DisciplineConflict);
        Assert.AreEqual(2, conflictedContext.DisciplineEvidence.Count);
        CollectionAssert.Contains(conflictedContext.DisciplineEvidence, "DRAWING_NAME_CONTROLS");
        CollectionAssert.Contains(conflictedContext.DisciplineEvidence, "LAYER_ELECTRICAL");
        Assert.IsFalse(JsonSerializer.Serialize(result.Value).Contains(drawingName, StringComparison.Ordinal));
        Assert.IsFalse(JsonSerializer.Serialize(result.Value).Contains("SYNTHETIC", StringComparison.Ordinal));
    }

    [TestMethod]
    public void NeighborSignalsAreDeterministicButNeighborTextIsNotInCadContract()
    {
        var origin = Input('a', "C1", 0, 0, sourceText: "LEVEL");
        var roof = Input('b', "C2", 1, 0, sourceText: "ROOF LEVEL");
        var ceiling = Input('c', "C3", 2, 0, sourceText: "CEILING PLAN");

        var result = CadSemanticContextBuilder.Build([origin, roof, ceiling]);

        Assert.IsTrue(result.IsSuccess, result.Error?.Code);
        var context = result.Value![origin.SegmentId];
        CollectionAssert.Contains(context.Signals, "ROOF_CONTEXT");
        CollectionAssert.Contains(context.Signals, "CEILING_CONTEXT");
        CollectionAssert.Contains(context.Signals, "VERTICAL_LEVEL_CONTEXT_CONFLICT");
        var serialized = JsonSerializer.Serialize(context);
        Assert.IsFalse(serialized.Contains(roof.SourceText, StringComparison.Ordinal));
        Assert.IsFalse(serialized.Contains(ceiling.SourceText, StringComparison.Ordinal));
        Assert.IsTrue(context.Neighbors.All(item => item.SegmentId.Length == 75 && item.SourceTextHash.StartsWith("sha256:", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void AggregateRejectsLegacyAndRehashedDisciplineOrSignalTampering()
    {
        var input = Input('a', "D1", 0, 0, layer: "NOTES");
        var built = CadSemanticContextBuilder.Build([input]);
        Assert.IsTrue(built.IsSuccess, built.Error?.Code);
        var segment = Segment(input, built.Value![input.SegmentId]);

        Assert.AreEqual("CAD_SEMANTIC_CONTEXT_LEGACY_MISSING",
            CadSemanticContextBuilder.AggregateHash([segment with { SemanticContext = null }]).Error?.Code);

        var disciplineTamper = Rehash(segment, segment.SemanticContext! with
        {
            Discipline = "Controls",
            DisciplineEvidence = ["LAYER_CONTROLS"]
        });
        Assert.AreEqual("CAD_SEMANTIC_CONTEXT_SET_INVALID",
            CadSemanticContextBuilder.AggregateHash([segment with { SemanticContext = disciplineTamper }]).Error?.Code);

        var signalTamper = Rehash(segment, segment.SemanticContext! with { Signals = ["ROOF_CONTEXT"] });
        Assert.AreEqual("CAD_SEMANTIC_CONTEXT_SET_INVALID",
            CadSemanticContextBuilder.AggregateHash([segment with { SemanticContext = signalTamper }]).Error?.Code);
    }

    private static CadSemanticContextInput Input(char id, string handle, double x, double y, string layer = "NOTES",
        string? drawingHint = null, string? sourceText = null) => new(
        Id(id), "TEXT", handle, "ModelSpace", null, [], layer, sourceText ?? $"SOURCE-{id}",
        SourceHash(sourceText ?? $"SOURCE-{id}"), x, y, 1, "ExtentsCenter", drawingHint);

    private static CadTextSegment[] Segments(IEnumerable<CadSemanticContextInput> inputs,
        IReadOnlyDictionary<string, CadSemanticContext> contexts) => inputs
        .Select(input => Segment(input, contexts[input.SegmentId])).ToArray();

    private static CadTextSegment Segment(CadSemanticContextInput input, CadSemanticContext? context) => new()
    {
        SegmentId = input.SegmentId,
        Entity = new CadEntityReference
        {
            Type = input.EntityType,
            Handle = input.Handle,
            Space = input.Space,
            Layout = input.Layout,
            BlockPath = [],
            Layer = input.Layer,
            SubIndex = 0
        },
        SourceText = input.SourceText,
        SourceTextHash = input.SourceTextHash,
        LineBreakStyle = "None",
        ProtectedTokens = [],
        FieldClassification = "None",
        State = "Extracted",
        SemanticContext = context
    };

    private static CadSemanticContext Rehash(CadTextSegment segment, CadSemanticContext context)
    {
        var semanticKey = CanonicalHash("dwg-translator/semantic-key/v1", new[]
        {
            context.Version, context.SheetRole, context.Discipline, context.DisciplineConflict ? "conflict" : "clear",
            string.Join("\u001f", context.DisciplineEvidence), string.Join("\u001f", context.Signals), context.NeighborhoodDigest
        });
        var contextHash = CanonicalHash("dwg-translator/semantic-context/v1", new[]
        {
            context.Version, segment.SegmentId, segment.Entity.Type, segment.Entity.Handle, segment.Entity.Space,
            segment.Entity.Layout ?? string.Empty, segment.Entity.Layer, context.AnchorSource,
            context.XBand.ToString(CultureInfo.InvariantCulture), context.YBand.ToString(CultureInfo.InvariantCulture),
            context.ReadingOrder.ToString(CultureInfo.InvariantCulture), semanticKey, context.NeighborhoodDigest,
            string.Join("\u001f", context.Neighbors.Select(neighbor =>
                $"{neighbor.SegmentId}|{neighbor.SourceTextHash}|{neighbor.EntityType}|{neighbor.Relation}|{neighbor.DistanceBand}|{neighbor.SameLayer}"))
        });
        return context with { SemanticKey = semanticKey, ContextHash = contextHash };
    }

    private static string CanonicalHash(string domain, IEnumerable<string> values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, domain);
        foreach (var value in values) Append(hash, value);
        return "sha256:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static string Id(char value) => "seg_sha256_" + new string(value, 64);
    private static string SourceHash(string value) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
