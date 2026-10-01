using System.Text.Json;
using DwgTranslator.Application;
using DwgTranslator.Contracts;
using DwgTranslator.Domain;

namespace DwgTranslator.Core.Tests;

[TestClass]
public sealed class TranslationBatchFactoryTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public void FactoryProtectsTokensAndSplitsDeterministically()
    {
        var segments = new[] { Segment('a', "PUMP {TAG}"), Segment('b', "SAFETY\\PNOTICE"), Segment('c', "VALVE") };
        var result = TranslationBatchFactory.Create(segments, "en", "es-MX", "translate-cad-text/1.0", maxSegments: 2, maxCharacters: 1_000);
        Assert.IsTrue(result.IsSuccess, result.Error?.Code);
        Assert.AreEqual(2, result.Value!.Count);
        Assert.AreEqual("PUMP ⟦T0⟧", result.Value[0].Segments[0].TextWithTokenAliases);
        Assert.AreEqual("{TAG}", result.Value[0].Segments[0].ProtectedTokenAliases["T0"]);
        Assert.AreEqual(TranslationBatchFactory.ConfigurationHash(result.Value[0]), TranslationBatchFactory.ConfigurationHash(result.Value[0]));
    }

    [TestMethod]
    public void HumanApprovalPreservesTokensAndCoverage()
    {
        var source = Segment('a', "PUMP {TAG}");
        var proposal = new AcceptedTranslation(source.SegmentId, "BOMBA {TAG}");
        var approved = HumanReviewPolicy.Approve(source, proposal, "BOMBA {TAG}");
        Assert.IsTrue(approved.IsSuccess, approved.Error?.Code);
        Assert.AreEqual("TOKEN_INTEGRITY_FAILED", HumanReviewPolicy.Approve(source, proposal, "BOMBA").Error!.Code);
        var excluded = HumanReviewPolicy.Exclude(Segment('b', "NOTE"), "NOT_TRANSLATABLE");
        Assert.IsTrue(ApprovalPolicy.Evaluate([approved.Value!, excluded.Value!]).IsSuccess);
    }

    [TestMethod]
    public void FactoryRejectsDuplicateAndOversizeSegments()
    {
        var segment = Segment('a', "PUMP");
        Assert.AreEqual("TRANSLATION_BATCH_SEGMENT_DUPLICATE", TranslationBatchFactory.Create([segment, segment], "en", "es", "v1").Error!.Code);
        Assert.AreEqual("TRANSLATION_SEGMENT_TOO_LARGE", TranslationBatchFactory.Create([Segment('b', "LONG")], "en", "es", "v1", maxCharacters: 3).Error!.Code);
    }

    [TestMethod]
    public void SerializedContractBoundaryAcceptsExactlyTwentyThousandAndRejectsTwentyThousandOne()
    {
        var seed = Segment('d', "X");
        var seedBatch = TranslationBatchFactory.Create([seed], "en", "es", "v1", maxCharacters: 100_000).Value!.Single();
        var overhead = JsonSerializer.Serialize(seedBatch.Segments.Single(), WebJson).Length - 1;
        var exact = Segment('e', new string('A', 20_000 - overhead));
        var exactResult = TranslationBatchFactory.Create([exact], "en", "es", "v1", maxCharacters: 20_000);

        Assert.IsTrue(exactResult.IsSuccess, exactResult.Error?.Code);
        Assert.AreEqual(20_000, JsonSerializer.Serialize(exactResult.Value!.Single().Segments.Single(), WebJson).Length);
        var over = exact with { SourceText = exact.SourceText + "A" };
        Assert.AreEqual("TRANSLATION_SEGMENT_TOO_LARGE",
            TranslationBatchFactory.Create([over], "en", "es", "v1", maxCharacters: 20_000).Error!.Code);
    }

    [TestMethod]
    public void ContextualFactoryUsesSegmentIdsBoundsUnicodeExcerptsAndCountsSerializedContext()
    {
        var neighborText = string.Concat(Enumerable.Repeat("😀", 200));
        var raw = Enumerable.Range(0, 6).Select(index => Segment((char)('a' + index), neighborText) with
        {
            SourceTextHash = "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(neighborText))).ToLowerInvariant(),
            Entity = Segment((char)('a' + index), "X").Entity with { Handle = (0xA1 + index).ToString("X", System.Globalization.CultureInfo.InvariantCulture) }
        }).ToArray();
        var built = CadSemanticContextBuilder.Build(raw.Select((segment, index) => new CadSemanticContextInput(
            segment.SegmentId, segment.Entity.Type, segment.Entity.Handle, segment.Entity.Space, segment.Entity.Layout,
            segment.Entity.BlockPath, segment.Entity.Layer, segment.SourceText, segment.SourceTextHash,
            index, 0, 1, "ExtentsCenter")).ToArray());
        Assert.IsTrue(built.IsSuccess, built.Error?.Code);
        var contextual = raw.Select(segment => segment with { SemanticContext = built.Value![segment.SegmentId] }).ToArray();

        var result = TranslationBatchFactory.Create(contextual, "en", "es", "v1", maxSegments: 50, maxCharacters: 1_000_000);

        Assert.IsTrue(result.IsSuccess, result.Error?.Code);
        var translated = result.Value!.SelectMany(batch => batch.Segments).ToArray();
        Assert.AreEqual(contextual.Length, translated.Length);
        foreach (var segment in translated)
        {
            Assert.AreEqual(CadSemanticContextBuilder.PolicyVersion, segment.Context.ContextVersion);
            Assert.IsNotNull(segment.Context.SemanticKey);
            Assert.IsNotNull(segment.Context.NeighborExcerpts);
            Assert.IsTrue(segment.Context.NeighborExcerpts.Count <= TranslationBatchFactory.MaximumNeighborExcerpts);
            Assert.IsTrue(segment.Context.NeighborExcerpts.All(excerpt => excerpt.Text.EnumerateRunes().Count() <= TranslationBatchFactory.MaximumNeighborExcerptScalars));
            Assert.IsTrue(segment.Context.NeighborExcerpts.Sum(excerpt => excerpt.Text.EnumerateRunes().Count()) <= TranslationBatchFactory.MaximumNeighborExcerptScalarsPerSegment);
            Assert.IsTrue(segment.Context.NeighborExcerpts.All(excerpt => excerpt.SegmentId.StartsWith("seg_sha256_", StringComparison.Ordinal)));
            Assert.IsFalse(JsonSerializer.Serialize(segment.Context.NeighborExcerpts).Contains("handle", StringComparison.OrdinalIgnoreCase));
        }

        var serializedSize = JsonSerializer.Serialize(translated[0], WebJson).Length;
        Assert.AreEqual("TRANSLATION_SEGMENT_TOO_LARGE",
            TranslationBatchFactory.Create(contextual, "en", "es", "v1", maxCharacters: serializedSize - 1).Error?.Code);
        Assert.AreEqual("TRANSLATION_CONTEXT_PARTIAL",
            TranslationBatchFactory.Create([contextual[0] with { SemanticContext = null }, .. contextual.Skip(1)], "en", "es", "v1").Error?.Code);
    }

    [TestMethod]
    public void ContextualExcerptIsOmittedWhenScalarLimitCutsProtectedToken()
    {
        var segments = ContextualPair("ORIGIN", new string('A', 158) + "{TAG}" + new string('B', 20));

        var result = TranslationBatchFactory.Create(segments, "en", "es", "v1", maxCharacters: 100_000);

        Assert.IsTrue(result.IsSuccess, result.Error?.Code);
        var origin = result.Value!.Single().Segments.Single(segment => segment.SegmentId == segments[0].SegmentId);
        Assert.IsNotNull(origin.Context.NeighborExcerpts);
        Assert.AreEqual(0, origin.Context.NeighborExcerpts.Count);
    }

    [TestMethod]
    public void ContextualExcerptKeepsWholeProtectedTokenWhenBoundaryIsAfterIt()
    {
        var segments = ContextualPair("ORIGIN", new string('A', 150) + "{TAG}" + new string('B', 20));

        var result = TranslationBatchFactory.Create(segments, "en", "es", "v1", maxCharacters: 100_000);

        Assert.IsTrue(result.IsSuccess, result.Error?.Code);
        var origin = result.Value!.Single().Segments.Single(segment => segment.SegmentId == segments[0].SegmentId);
        Assert.IsNotNull(origin.Context.NeighborExcerpts);
        var excerpt = origin.Context.NeighborExcerpts.Single();
        Assert.AreEqual(TranslationBatchFactory.MaximumNeighborExcerptScalars, excerpt.Text.EnumerateRunes().Count());
        StringAssert.Contains(excerpt.Text, "{TAG}");
    }

    [TestMethod]
    public void ContextualSplitOmitsCrossRequestNeighborsAndEveryRequestValidates()
    {
        var raw = Enumerable.Range(0, 6).Select(index => SegmentWithActualHash(
            (char)('a' + index), (0xA1 + index).ToString("X", System.Globalization.CultureInfo.InvariantCulture), "NOTE" + index)).ToArray();
        var built = CadSemanticContextBuilder.Build(raw.Select((segment, index) => new CadSemanticContextInput(
            segment.SegmentId, segment.Entity.Type, segment.Entity.Handle, segment.Entity.Space, segment.Entity.Layout,
            segment.Entity.BlockPath, segment.Entity.Layer, segment.SourceText, segment.SourceTextHash,
            index, 0, 1, "ExtentsCenter")).ToArray());
        Assert.IsTrue(built.IsSuccess, built.Error?.Code);
        var contextual = raw.Select(segment => segment with { SemanticContext = built.Value![segment.SegmentId] }).ToArray();

        var result = TranslationBatchFactory.Create(contextual, "en", "es",
            TranslationReviewWorkflow.ContextualPromptTemplateVersion, maxSegments: 2, maxCharacters: 100_000);

        Assert.IsTrue(result.IsSuccess, result.Error?.Code);
        Assert.AreEqual(3, result.Value!.Count);
        var originalIds = contextual.Select(segment => segment.SegmentId).Order(StringComparer.Ordinal).ToArray();
        var emittedIds = result.Value.SelectMany(payload => payload.Segments).Select(segment => segment.SegmentId).ToArray();
        Assert.AreEqual(contextual.Length, emittedIds.Length);
        Assert.AreEqual(contextual.Length, emittedIds.Distinct(StringComparer.Ordinal).Count());
        CollectionAssert.AreEqual(originalIds, emittedIds.Order(StringComparer.Ordinal).ToArray());
        foreach (var payload in result.Value)
        {
            Assert.IsTrue(payload.Segments.Count <= 2);
            Assert.IsTrue(payload.Segments.Sum(segment => JsonSerializer.Serialize(segment, WebJson).Length) <= 100_000);
            var ids = payload.Segments.Select(segment => segment.SegmentId).ToHashSet(StringComparer.Ordinal);
            foreach (var segment in payload.Segments)
            {
                var excerpts = segment.Context.NeighborExcerpts ?? [];
                Assert.IsTrue(excerpts.Count <= TranslationBatchFactory.MaximumNeighborExcerpts);
                Assert.IsTrue(excerpts.All(excerpt => ids.Contains(excerpt.SegmentId)));
                Assert.IsTrue(excerpts.All(excerpt => excerpt.Text.EnumerateRunes().Count() <=
                    TranslationBatchFactory.MaximumNeighborExcerptScalars));
                Assert.IsTrue(excerpts.Sum(excerpt => excerpt.Text.EnumerateRunes().Count()) <=
                    TranslationBatchFactory.MaximumNeighborExcerptScalarsPerSegment);
            }
            var envelope = WireEnvelope.Request(MessageTypes.TranslationRequest, Guid.NewGuid(), Guid.NewGuid(),
                TranslationBatchFactory.ConfigurationHash(payload), DateTimeOffset.UtcNow,
                JsonSerializer.SerializeToNode(payload, WebJson)!.AsObject());
            Assert.IsTrue(TranslationResultPolicy.ValidateRequest(envelope).IsSuccess);
        }
    }

    private static CadTextSegment[] ContextualPair(string originText, string neighborText)
    {
        var segments = new[]
        {
            SegmentWithActualHash('a', "A1", originText),
            SegmentWithActualHash('b', "A2", neighborText)
        };
        var built = CadSemanticContextBuilder.Build(segments.Select((segment, index) => new CadSemanticContextInput(
            segment.SegmentId, segment.Entity.Type, segment.Entity.Handle, segment.Entity.Space, segment.Entity.Layout,
            segment.Entity.BlockPath, segment.Entity.Layer, segment.SourceText, segment.SourceTextHash,
            index, 0, 1, "ExtentsCenter")).ToArray());
        Assert.IsTrue(built.IsSuccess, built.Error?.Code);
        return segments.Select(segment => segment with { SemanticContext = built.Value![segment.SegmentId] }).ToArray();
    }

    private static CadTextSegment SegmentWithActualHash(char id, string handle, string text) => Segment(id, text) with
    {
        SourceTextHash = "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant(),
        Entity = Segment(id, text).Entity with { Handle = handle }
    };

    private static CadTextSegment Segment(char hashCharacter, string text)
    {
        var tokens = CadProtectedTokenPolicy.Extract(text).ToList();
        return new CadTextSegment
        {
            SegmentId = "seg_sha256_" + new string(hashCharacter, 64),
            SourceText = text,
            SourceTextHash = "sha256:" + new string(hashCharacter, 64),
            LineBreakStyle = text.Contains("\\P", StringComparison.Ordinal) ? "MTextParagraph" : "None",
            ProtectedTokens = tokens,
            FieldClassification = "None",
            State = "Extracted",
            Entity = new CadEntityReference { Type = text.Contains("\\P", StringComparison.Ordinal) ? "MTEXT" : "TEXT", Handle = "A1", Space = "ModelSpace", Layout = null, BlockPath = [], Layer = "NOTES", SubIndex = 0 }
        };
    }
}
