using System.Text.Json;
using System.Text.Json.Nodes;
using DwgTranslator.Application;
using DwgTranslator.Contracts;

namespace DwgTranslator.Core.Tests;

[TestClass]
public sealed class TranslationPolicyTests
{
    private const string SegmentA = "seg_sha256_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SegmentB = "seg_sha256_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] ExpectedIds = [SegmentA, SegmentB];
    private static readonly string[] ExpectedTranslations = ["BOMBA {TAG}", "AVISO\\P"];

    [TestMethod]
    public void ProtectedTokensRoundTripInOccurrenceOrder()
    {
        const string source = @"PUMP {TAG} %%d {TAG}";
        var protectedText = TranslationTokenAliases.Protect(source, CadProtectedTokenPolicy.Extract(source));
        Assert.IsTrue(protectedText.IsSuccess, protectedText.Error?.Code);
        Assert.AreEqual("PUMP ⟦T0⟧ ⟦T1⟧ ⟦T2⟧", protectedText.Value!.Text);
        var restored = TranslationTokenAliases.Restore(
            "BOMBA ⟦T0⟧ ⟦T1⟧ ⟦T2⟧",
            protectedText.Value.Aliases,
            protectedText.Value.Text);
        Assert.IsTrue(restored.IsSuccess, restored.Error?.Code);
        Assert.AreEqual("BOMBA {TAG} %%d {TAG}", restored.Value);
    }

    [TestMethod]
    [DataRow("BOMBA ⟦T0⟧", DisplayName = "missing")]
    [DataRow("BOMBA ⟦T0⟧ ⟦T0⟧ ⟦T1⟧", DisplayName = "duplicate")]
    [DataRow("BOMBA ⟦T0⟧ ⟦T1⟧ ⟦T9⟧", DisplayName = "unknown")]
    [DataRow("BOMBA ⟦T1⟧ ⟦T0⟧", DisplayName = "reordered")]
    [DataRow("BOMBA ⟦T0⟧ ⟦T1⟧ {INJECTED}", DisplayName = "raw injection")]
    public void RestoreRejectsTokenTampering(string translated)
    {
        var aliases = new Dictionary<string, string> { ["T0"] = "{TAG}", ["T1"] = "%%d" };
        Assert.AreEqual(
            "TOKEN_INTEGRITY_FAILED",
            TranslationTokenAliases.Restore(translated, aliases, "PUMP ⟦T0⟧ ⟦T1⟧").Error!.Code);
    }

    [TestMethod]
    public void TranslationResponseIsValidatedAndReturnedInRequestOrder()
    {
        var request = Request();
        var response = Response(request, [
            new() { SegmentId = SegmentB, TranslatedTextWithTokenAliases = "AVISO⟦T0⟧", TokenIntegrity = "Valid" },
            new() { SegmentId = SegmentA, TranslatedTextWithTokenAliases = "BOMBA ⟦T0⟧", TokenIntegrity = "Valid" }
        ]);
        var result = TranslationResultPolicy.Validate(request, response);
        Assert.IsTrue(result.IsSuccess, result.Error?.Code);
        var accepted = result.Value!;
        CollectionAssert.AreEqual(ExpectedIds, accepted.Select(item => item.SegmentId).ToArray());
        CollectionAssert.AreEqual(ExpectedTranslations, accepted.Select(item => item.ProposedText).ToArray());
    }

    [TestMethod]
    public void TranslationResponseRejectsDuplicateOrMissingSegments()
    {
        var request = Request();
        var duplicated = Response(request, [
            new() { SegmentId = SegmentA, TranslatedTextWithTokenAliases = "A ⟦T0⟧", TokenIntegrity = "Valid" },
            new() { SegmentId = SegmentA, TranslatedTextWithTokenAliases = "B ⟦T0⟧", TokenIntegrity = "Valid" }
        ]);
        Assert.AreEqual("TRANSLATION_SEGMENT_SET_MISMATCH", TranslationResultPolicy.Validate(request, duplicated).Error!.Code);
    }

    [TestMethod]
    public void TranslationResponseRejectsCorrelationAndTokenClaims()
    {
        var request = Request();
        var proposals = new List<TranslationProposal>
        {
            new() { SegmentId = SegmentA, TranslatedTextWithTokenAliases = "BOMBA ⟦T0⟧", TokenIntegrity = "Valid" },
            new() { SegmentId = SegmentB, TranslatedTextWithTokenAliases = "AVISO", TokenIntegrity = "Valid" }
        };
        Assert.AreEqual("TOKEN_INTEGRITY_FAILED", TranslationResultPolicy.Validate(request, Response(request, proposals)).Error!.Code);
        Assert.AreEqual("TRANSLATION_RESPONSE_CORRELATION_INVALID", TranslationResultPolicy.Validate(request, Response(request, proposals) with { CorrelationId = Guid.NewGuid() }).Error!.Code);
    }

    [TestMethod]
    public void TranslationResponsePreservesMTextFormattingScope()
    {
        var request = RequestWithFormattedGroup();
        var valid = new List<TranslationProposal>
        {
            new() { SegmentId = SegmentA, TranslatedTextWithTokenAliases = "⟦T0⟧⟦T1⟧BOMBA⟦T2⟧", TokenIntegrity = "Valid" },
            new() { SegmentId = SegmentB, TranslatedTextWithTokenAliases = "AVISO⟦T0⟧", TokenIntegrity = "Valid" }
        };
        var accepted = TranslationResultPolicy.Validate(request, Response(request, valid));
        Assert.IsTrue(accepted.IsSuccess, accepted.Error?.Code);
        Assert.AreEqual(@"{\fArial;BOMBA}", accepted.Value![0].ProposedText);

        foreach (var moved in new[] { "BOMBA⟦T0⟧⟦T1⟧⟦T2⟧", "⟦T0⟧⟦T1⟧⟦T2⟧BOMBA", "⟦T0⟧BOMBA⟦T1⟧⟦T2⟧" })
        {
            var invalid = new List<TranslationProposal>
            {
                new() { SegmentId = SegmentA, TranslatedTextWithTokenAliases = moved, TokenIntegrity = "Valid" },
                new() { SegmentId = SegmentB, TranslatedTextWithTokenAliases = "AVISO⟦T0⟧", TokenIntegrity = "Valid" }
            };

            Assert.AreEqual("TOKEN_INTEGRITY_FAILED", TranslationResultPolicy.Validate(request, Response(request, invalid)).Error!.Code, moved);
        }
    }

    [TestMethod]
    [DataRow("BOMBA", "Valid", DisplayName = "missing alias")]
    [DataRow("BOMBA ⟦T0⟧ ⟦T0⟧ ⟦T1⟧", "Valid", DisplayName = "duplicate alias")]
    [DataRow("BOMBA ⟦T1⟧ ⟦T0⟧", "Valid", DisplayName = "alias order")]
    [DataRow("BOMBA ⟦T0⟧ ⟦T1⟧", "Invalid", DisplayName = "provider token claim")]
    public void AutoEscalatedTokenFailureFallsBackPerProposalToExactSource(string invalidTarget, string tokenIntegrity)
    {
        var request = RoutedRequest("PUMP ⟦T0⟧ ⟦T1⟧", new() { ["T0"] = "{TAG}", ["T1"] = "%%d" });
        var proposals = new List<TranslationProposal>
        {
            new() { SegmentId = SegmentA, TranslatedTextWithTokenAliases = invalidTarget, TokenIntegrity = tokenIntegrity },
            new() { SegmentId = SegmentB, TranslatedTextWithTokenAliases = "AVISO⟦T0⟧", TokenIntegrity = "Valid" }
        };

        var result = TranslationResultPolicy.Validate(request, RoutedResponse(request, proposals));

        Assert.IsTrue(result.IsSuccess, result.Error?.Code);
        var accepted = result.Value!;
        var fallback = accepted.Single(item => item.SegmentId == SegmentA);
        Assert.AreEqual("PUMP {TAG} %%d", fallback.ProposedText);
        Assert.AreEqual("high:TOKEN_INTEGRITY_FALLBACK", fallback.WarningCode);
        Assert.AreEqual("high", fallback.RiskSeverity);
        Assert.IsTrue(fallback.Escalated);
        Assert.AreEqual("AVISO", accepted.Single(item => item.SegmentId == SegmentB).ProposedText[..5]);
    }

    [TestMethod]
    public void AutoEscalatedVisibleWhitespaceSlotInjectionFallsBackToExactSource()
    {
        var request = RoutedRequest(
            "TITLE⟦T0⟧⟦T1⟧  \t ⟦T2⟧",
            new() { ["T0"] = "{", ["T1"] = @"\H0.7x;", ["T2"] = "}" });
        var proposals = new List<TranslationProposal>
        {
            new() { SegmentId = SegmentA, TranslatedTextWithTokenAliases = "TITULO⟦T0⟧⟦T1⟧VISIBLE⟦T2⟧", TokenIntegrity = "Valid" },
            new() { SegmentId = SegmentB, TranslatedTextWithTokenAliases = "AVISO⟦T0⟧", TokenIntegrity = "Valid" }
        };

        var result = TranslationResultPolicy.Validate(request, RoutedResponse(request, proposals));

        Assert.IsTrue(result.IsSuccess, result.Error?.Code);
        var fallback = result.Value!.Single(item => item.SegmentId == SegmentA);
        Assert.AreEqual("TITLE{\\H0.7x;  \t }", fallback.ProposedText);
        Assert.AreEqual("high:TOKEN_INTEGRITY_FALLBACK", fallback.WarningCode);
    }

    [TestMethod]
    public void TokenFallbackRequiresAutoEscalationEvidenceAndNeverAppliesToLegacyResponse()
    {
        var routed = RoutedRequest("PUMP ⟦T0⟧ ⟦T1⟧", new() { ["T0"] = "{TAG}", ["T1"] = "%%d" });
        var proposals = new List<TranslationProposal>
        {
            new() { SegmentId = SegmentA, TranslatedTextWithTokenAliases = "BOMBA", TokenIntegrity = "Valid" },
            new() { SegmentId = SegmentB, TranslatedTextWithTokenAliases = "AVISO⟦T0⟧", TokenIntegrity = "Valid" }
        };
        Assert.AreEqual("TOKEN_INTEGRITY_FAILED",
            TranslationResultPolicy.Validate(routed, RoutedResponse(routed, proposals, includeEscalationEvidence: false)).Error?.Code);

        var legacy = Request();
        Assert.AreEqual("TOKEN_INTEGRITY_FAILED",
            TranslationResultPolicy.Validate(legacy, Response(legacy, proposals)).Error?.Code);
    }

    [TestMethod]
    public void TranslationResponseRejectsEnvelopeAndPromptBindingTamper()
    {
        var request = Request();
        var proposals = new List<TranslationProposal>
        {
            new() { SegmentId = SegmentA, TranslatedTextWithTokenAliases = "BOMBA ⟦T0⟧", TokenIntegrity = "Valid" },
            new() { SegmentId = SegmentB, TranslatedTextWithTokenAliases = "AVISO⟦T0⟧", TokenIntegrity = "Valid" }
        };
        var valid = Response(request, proposals);
        Assert.AreEqual("TRANSLATION_RESPONSE_CORRELATION_INVALID",
            TranslationResultPolicy.Validate(request, valid with { IdempotencyKey = "sha256:" + new string('f', 64) }).Error!.Code);
        Assert.IsFalse(TranslationResultPolicy.Validate(request, valid with { SchemaVersion = "translation-response/999" }).IsSuccess);
        var promptTamper = valid with { Payload = valid.Payload!.DeepClone().AsObject() };
        promptTamper.Payload!["promptTemplateVersion"] = "translate-cad-text/999";
        Assert.AreEqual("TRANSLATION_RESPONSE_INVALID", TranslationResultPolicy.Validate(request, promptTamper).Error!.Code);
    }

    [TestMethod]
    public void TranslationResponseRejectsMissingMandatoryGlossaryOccurrence()
    {
        var request = Request();
        var proposals = new List<TranslationProposal>
        {
            new() { SegmentId = SegmentA, TranslatedTextWithTokenAliases = "EQUIPO ⟦T0⟧", TokenIntegrity = "Valid" },
            new() { SegmentId = SegmentB, TranslatedTextWithTokenAliases = "AVISO⟦T0⟧", TokenIntegrity = "Valid" }
        };

        Assert.AreEqual("GLOSSARY_INTEGRITY_FAILED", TranslationResultPolicy.Validate(request, Response(request, proposals)).Error!.Code);
    }

    [TestMethod]
    public void TranslationRequestRejectsCaseInsensitiveDuplicateGlossarySources()
    {
        var request = Request();
        request.Payload!["glossary"] = JsonSerializer.SerializeToNode(new[]
        {
            new TranslationGlossaryEntry { Source = "PUMP", Target = "BOMBA" },
            new TranslationGlossaryEntry { Source = "pump", Target = "EQUIPO", CaseSensitive = true }
        }, Json);

        Assert.AreEqual("TRANSLATION_REQUEST_INVALID", TranslationResultPolicy.ValidateRequest(request).Error!.Code);
    }

    [TestMethod]
    public void ContextualRequestValidatesExactNeighborReferenceAndRejectsTamper()
    {
        var payload = ContextualPayload(("NEIGHBOR {TAG}", "NEIGHBOR {TAG}"));
        Assert.IsTrue(TranslationResultPolicy.ValidateRequest(Request(payload)).IsSuccess);

        var origin = payload.Segments[0];
        var excerpt = origin.Context.NeighborExcerpts!.Single();
        var tampered = payload with
        {
            Segments =
            [
                origin with { Context = origin.Context with { NeighborExcerpts = [excerpt with { SourceTextHash = TestData.HashA }] } },
                .. payload.Segments.Skip(1)
            ]
        };
        Assert.AreEqual("TRANSLATION_REQUEST_INVALID", TranslationResultPolicy.ValidateRequest(Request(tampered)).Error?.Code);

        var partial = payload with
        {
            Segments = [origin with { Context = origin.Context with { ContextHash = null } }, .. payload.Segments.Skip(1)]
        };
        Assert.AreEqual("TRANSLATION_REQUEST_INVALID", TranslationResultPolicy.ValidateRequest(Request(partial)).Error?.Code);
    }

    [TestMethod]
    public void ContextualRequestRejectsPerNeighborAndAggregateScalarOverflow()
    {
        var longSource = new string('A', 200);
        var perNeighbor = ContextualPayload((longSource, new string('A', 161)));
        Assert.AreEqual("TRANSLATION_REQUEST_INVALID", TranslationResultPolicy.ValidateRequest(Request(perNeighbor)).Error?.Code);

        var aggregate = ContextualPayload(
            (longSource, new string('A', 129)), (longSource, new string('A', 129)),
            (longSource, new string('A', 129)), (longSource, new string('A', 129)));
        Assert.AreEqual("TRANSLATION_REQUEST_INVALID", TranslationResultPolicy.ValidateRequest(Request(aggregate)).Error?.Code);
    }

    [TestMethod]
    public void ContextualRequestRejectsExcerptThatCutsProtectedTokenEvenWhenBypassingFactory()
    {
        var source = new string('A', 158) + "{TAG}" + new string('B', 20);
        var payload = ContextualPayload((source, source[..160]));

        Assert.AreEqual("TRANSLATION_REQUEST_INVALID", TranslationResultPolicy.ValidateRequest(Request(payload)).Error?.Code);
    }

    [TestMethod]
    public void RequestPromptVersionMustExactlyMatchLegacyOrContextualShape()
    {
        var legacyWithContextualPrompt = Request();
        legacyWithContextualPrompt.Payload!["promptTemplateVersion"] = TranslationReviewWorkflow.ContextualPromptTemplateVersion;
        Assert.AreEqual("TRANSLATION_REQUEST_INVALID",
            TranslationResultPolicy.ValidateRequest(legacyWithContextualPrompt).Error?.Code);

        var contextualWithLegacyPrompt = ContextualPayload(("NEIGHBOR", "NEIGHBOR")) with
        {
            PromptTemplateVersion = TranslationReviewWorkflow.LegacyPromptTemplateVersion
        };
        Assert.AreEqual("TRANSLATION_REQUEST_INVALID",
            TranslationResultPolicy.ValidateRequest(Request(contextualWithLegacyPrompt)).Error?.Code);

        var unsupported = Request();
        unsupported.Payload!["promptTemplateVersion"] = "translate-cad-text/9.9";
        Assert.AreEqual("TRANSLATION_REQUEST_INVALID", TranslationResultPolicy.ValidateRequest(unsupported).Error?.Code);
    }

    private static WireEnvelope Request()
    {
        var payload = new TranslationBatchRequestPayload
        {
            SourceLanguage = "en",
            TargetLanguage = "es-MX",
            PromptTemplateVersion = TranslationReviewWorkflow.LegacyPromptTemplateVersion,
            Glossary = [new() { Source = "PUMP", Target = "BOMBA", CaseSensitive = true }],
            Segments =
            [
                Segment(SegmentA, "PUMP ⟦T0⟧", "T0", "{TAG}", "TEXT", "ModelSpace", null),
                Segment(SegmentB, "SAFETY⟦T0⟧", "T0", "\\P", "MTEXT", "PaperSpace", "Layout1")
            ]
        };
        return WireEnvelope.Request(MessageTypes.TranslationRequest, Guid.NewGuid(), Guid.NewGuid(), TestData.HashA, DateTimeOffset.Parse("2026-08-12T20:00:00Z", System.Globalization.CultureInfo.InvariantCulture), JsonSerializer.SerializeToNode(payload, Json)!.AsObject());
    }

    private static WireEnvelope RequestWithFormattedGroup()
    {
        var request = Request();
        var segment = request.Payload!["segments"]!.AsArray()[0]!.AsObject();
        segment["textWithTokenAliases"] = "⟦T0⟧⟦T1⟧PUMP⟦T2⟧";
        segment["protectedTokenAliases"] = new JsonObject
        {
            ["T0"] = "{",
            ["T1"] = @"\fArial;",
            ["T2"] = "}"
        };
        segment["context"]!["entityType"] = "MTEXT";
        return request;
    }

    private static WireEnvelope RoutedRequest(string sourceWithAliases, Dictionary<string, string> aliases)
    {
        var payload = Request().Payload!.Deserialize<TranslationBatchRequestPayload>(Json)!;
        payload = payload with
        {
            Routing = TranslationRoutingPolicyFactory.Auto(),
            Segments =
            [
                payload.Segments[0] with
                {
                    TextWithTokenAliases = sourceWithAliases,
                    ProtectedTokenAliases = aliases
                },
                payload.Segments[1]
            ]
        };
        return Request(payload);
    }

    private static WireEnvelope Request(TranslationBatchRequestPayload payload) =>
        WireEnvelope.Request(MessageTypes.TranslationRequest, Guid.NewGuid(), Guid.NewGuid(), TestData.HashA,
            DateTimeOffset.Parse("2026-08-12T20:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            JsonSerializer.SerializeToNode(payload, Json)!.AsObject());

    private static TranslationBatchRequestPayload ContextualPayload(params (string Source, string Excerpt)[] neighbors)
    {
        var segments = new List<TranslationSegment>();
        var neighborSegments = new List<TranslationSegment>();
        var excerpts = new List<TranslationNeighborExcerpt>();
        for (var index = 0; index < neighbors.Length; index++)
        {
            var idCharacter = (char)('b' + index);
            var id = "seg_sha256_" + new string(idCharacter, 64);
            var protectedText = TranslationTokenAliases.Protect(neighbors[index].Source,
                CadProtectedTokenPolicy.Extract(neighbors[index].Source));
            Assert.IsTrue(protectedText.IsSuccess, protectedText.Error?.Code);
            var sourceHash = SourceHash(neighbors[index].Source);
            neighborSegments.Add(new TranslationSegment
            {
                SegmentId = id,
                TextWithTokenAliases = protectedText.Value!.Text,
                ProtectedTokenAliases = new Dictionary<string, string>(protectedText.Value.Aliases, StringComparer.Ordinal),
                Context = Context(idCharacter, [])
            });
            excerpts.Add(new TranslationNeighborExcerpt
            {
                SegmentId = id,
                SourceTextHash = sourceHash,
                EntityType = "TEXT",
                Relation = "Right",
                DistanceBand = 1,
                SameLayer = true,
                Text = neighbors[index].Excerpt
            });
        }
        segments.Add(new TranslationSegment
        {
            SegmentId = SegmentA,
            TextWithTokenAliases = "ORIGIN",
            ProtectedTokenAliases = [],
            Context = Context('f', excerpts)
        });
        segments.AddRange(neighborSegments);
        return new TranslationBatchRequestPayload
        {
            SourceLanguage = "en",
            TargetLanguage = "es-MX",
            PromptTemplateVersion = TranslationReviewWorkflow.ContextualPromptTemplateVersion,
            Glossary = [],
            Segments = segments
        };

        static TranslationSegmentContext Context(char hashCharacter, List<TranslationNeighborExcerpt> neighborExcerpts) => new()
        {
            EntityType = "TEXT",
            Space = "ModelSpace",
            Layout = null,
            Layer = "NOTES",
            BlockPath = [],
            ContextVersion = CadSemanticContextBuilder.PolicyVersion,
            ContextHash = "sha256:" + new string(hashCharacter, 64),
            SemanticKey = "sha256:" + new string('c', 64),
            SheetRole = "Model",
            Discipline = "Controls",
            DisciplineConflict = false,
            XBand = 8,
            YBand = 8,
            Signals = [],
            NeighborExcerpts = neighborExcerpts
        };
    }

    private static string SourceHash(string value) => "sha256:" + Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static TranslationSegment Segment(string id, string text, string key, string token, string type, string space, string? layout) => new()
    {
        SegmentId = id,
        TextWithTokenAliases = text,
        ProtectedTokenAliases = new() { [key] = token },
        Context = new() { EntityType = type, Space = space, Layout = layout, Layer = "NOTES", BlockPath = [] }
    };

    private static WireEnvelope Response(WireEnvelope request, List<TranslationProposal> proposals) => new()
    {
        SchemaVersion = ContractV1.SchemaVersion,
        MessageType = MessageTypes.TranslationResponse,
        JobId = request.JobId,
        CorrelationId = request.CorrelationId,
        IdempotencyKey = request.IdempotencyKey,
        SentAtUtc = request.SentAtUtc.AddSeconds(1),
        Status = OperationStatus.Succeeded,
        Payload = JsonSerializer.SerializeToNode(new TranslationBatchResponsePayload
        {
            Model = "configured-model",
            PromptTemplateVersion = TranslationReviewWorkflow.LegacyPromptTemplateVersion,
            Proposals = proposals,
            Usage = new() { InputTokens = 10, OutputTokens = 5 }
        }, Json)!.AsObject()
    };

    private static WireEnvelope RoutedResponse(
        WireEnvelope request,
        List<TranslationProposal> proposals,
        bool includeEscalationEvidence = true)
    {
        var input = request.Payload!.Deserialize<TranslationBatchRequestPayload>(Json)!;
        var policy = input.Routing!;
        var risks = TranslationRiskEvaluator.Evaluate(input, proposals, policy);
        var traces = risks.Select(risk =>
        {
            var tokenFailure = risk.ReasonCodes.Contains("PROTECTED_TOKEN_CHANGED", StringComparer.Ordinal);
            return new TranslationSegmentTrace
            {
                SegmentId = risk.SegmentId,
                RequestedMode = policy.RequestedMode,
                BaseModel = policy.BaseModel,
                EffectiveModel = policy.BaseModel,
                Escalated = includeEscalationEvidence && tokenFailure,
                EscalationReasonCodes = includeEscalationEvidence && tokenFailure
                    ? risk.ReasonCodes.ToList()
                    : [],
                ValidatorResult = risk.ReasonCodes.Count == 0 ? "pass" : "review",
                RiskSeverity = risk.RiskSeverity,
                RoutingVersion = policy.Version
            };
        }).ToList();
        var calls = new List<TranslationCallTrace>
        {
            new()
            {
                Tier = "base", RequestedModel = TranslationRouting.Terra, EffectiveModel = TranslationRouting.Terra,
                Usage = new() { InputTokens = 5, OutputTokens = 2 }, LatencyMilliseconds = 1,
                PromptVersion = input.PromptTemplateVersion, SchemaVersion = TranslationRouting.SchemaVersion,
                Outcome = "succeeded"
            }
        };
        if (includeEscalationEvidence)
        {
            calls.Add(new TranslationCallTrace
            {
                Tier = "escalation",
                RequestedModel = TranslationRouting.Sol,
                EffectiveModel = TranslationRouting.Sol,
                Usage = new() { InputTokens = 5, OutputTokens = 3 },
                LatencyMilliseconds = 1,
                PromptVersion = input.PromptTemplateVersion,
                SchemaVersion = TranslationRouting.SchemaVersion,
                Outcome = "failed",
                ErrorCode = "TOKEN_INTEGRITY_FAILED"
            });
        }
        else
        {
            calls[0] = calls[0] with { Usage = new TranslationUsage { InputTokens = 10, OutputTokens = 5 } };
        }

        return new WireEnvelope
        {
            SchemaVersion = ContractV1.SchemaVersion,
            MessageType = MessageTypes.TranslationResponse,
            JobId = request.JobId,
            CorrelationId = request.CorrelationId,
            IdempotencyKey = request.IdempotencyKey,
            SentAtUtc = request.SentAtUtc.AddSeconds(1),
            Status = OperationStatus.Succeeded,
            Payload = JsonSerializer.SerializeToNode(new TranslationBatchResponsePayload
            {
                Model = TranslationRouting.Terra,
                PromptTemplateVersion = input.PromptTemplateVersion,
                Proposals = proposals,
                Usage = new() { InputTokens = 10, OutputTokens = 5 },
                Routing = new TranslationRoutingTrace
                {
                    RequestedMode = policy.RequestedMode,
                    BaseModel = policy.BaseModel,
                    RoutingVersion = policy.Version,
                    EscalatedSegmentCount = traces.Count(trace => trace.Escalated),
                    Calls = calls,
                    Segments = traces
                }
            }, Json)!.AsObject()
        };
    }
}
