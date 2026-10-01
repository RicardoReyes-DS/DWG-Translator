using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DwgTranslator.Application;
using DwgTranslator.Contracts;
using DwgTranslator.Translation.OpenAI;

namespace DwgTranslator.Core.Tests;

[TestClass]
public sealed class TranslationRoutingTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    [TestMethod]
    public void PoliciesSelectExactTiersWithoutImplicitFallback()
    {
        AssertPolicy(TranslationRoutingPolicyFactory.Auto(), TranslationRouting.Auto, TranslationRouting.Terra, TranslationRouting.Sol, 1);
        AssertPolicy(TranslationRoutingPolicyFactory.Economy(), TranslationRouting.Economy, TranslationRouting.Luna, null, 0);
        AssertPolicy(TranslationRoutingPolicyFactory.MaximumQuality(), TranslationRouting.MaximumQuality, TranslationRouting.Sol, null, 0);
        AssertPolicy(TranslationRoutingPolicyFactory.Manual("gpt-4.1"), TranslationRouting.Manual, "gpt-4.1", null, 0);
        Assert.IsFalse(TranslationRoutingPolicyFactory.Validate(TranslationRoutingPolicyFactory.Manual("gpt-5.6")).IsSuccess);
    }

    [TestMethod]
    public void DeterministicSignalsClassifyHighMediumAndLowWithoutModelConfidence()
    {
        var policy = TranslationRoutingPolicyFactory.Auto();
        AssertRisk("PIPE 100 mm", "ROHR 101 mm", policy, "high", "NUMBER_CHANGED");
        AssertRisk("PIPE 100 mm", "ROHR 100 cm", policy, "high", "UNIT_CHANGED");
        AssertRisk("TAG {{A}}", "KENN {{B}}", policy, "high", "PLACEHOLDER_CHANGED");
        AssertRisk("ITEM AB-100", "TEIL AB-101", policy, "high", "CAD_CODE_CHANGED");
        AssertRisk("LINE 1\nLINE 2", "ZEILE 1", policy, "high", "NUMBER_CHANGED");
        AssertRisk("NOTE", "HINWEIS;", policy, "low", "PUNCTUATION_CHANGED");

        var glossaryRequest = RequestPayload([Segment('a', "PUMP")], policy) with
        {
            Glossary = [new TranslationGlossaryEntry { Source = "PUMP", Target = "PUMPE", CaseSensitive = false }]
        };
        var glossary = TranslationRiskEvaluator.Evaluate(glossaryRequest, [Proposal('a', "AGGREGAT")], policy).Single();
        CollectionAssert.Contains(glossary.ReasonCodes.ToArray(), "GLOSSARY_MISSING");
        Assert.AreEqual("high", glossary.RiskSeverity);
    }

    [TestMethod]
    public void CandidateComparisonNeverChoosesWorseInvariants()
    {
        var terra = new TranslationRiskAssessment("s", "high", ["NUMBER_CHANGED"], "X", false);
        var solBetter = new TranslationRiskAssessment("s", "medium", ["LINE_STRUCTURE_CHANGED"], "Y", true);
        var solWorse = new TranslationRiskAssessment("s", "high", ["NUMBER_CHANGED", "UNIT_CHANGED"], "Z", false);
        Assert.IsTrue(TranslationRiskEvaluator.PreferCandidate(terra, solBetter));
        Assert.IsFalse(TranslationRiskEvaluator.PreferCandidate(terra, solWorse));
    }

    [TestMethod]
    public void SetAliasLengthAndConsistencyDetectorsAreFailClosed()
    {
        var policy = TranslationRoutingPolicyFactory.Auto();
        var aliasSegment = Segment('a', "PUMP ⟦T0⟧") with { ProtectedTokenAliases = new Dictionary<string, string> { ["T0"] = "{TAG}" } };
        AssertCode(RequestPayload([aliasSegment], policy), [Proposal('a', "PUMPE")], policy, "PROTECTED_TOKEN_CHANGED");
        AssertCode(RequestPayload([Segment('a', "NOTE")], policy), [], policy, "MISSING_ID");
        AssertCode(RequestPayload([Segment('a', "NOTE")], policy), [Proposal('a', "HINWEIS"), Proposal('a', "NOTIZ")], policy, "DUPLICATE_ID");
        AssertCode(RequestPayload([Segment('a', "NOTE")], policy), [Proposal('a', string.Empty)], policy, "EMPTY_TRANSLATION");
        AssertCode(RequestPayload([Segment('a', new string('A', 100))], policy), [Proposal('a', "B")], policy, "LENGTH_RATIO_HIGH_LOW");

        var repeated = RequestPayload([Segment('a', "VALVE"), Segment('b', "VALVE")], policy);
        var repeatedRisk = TranslationRiskEvaluator.Evaluate(repeated, [Proposal('a', "VENTIL"), Proposal('b', "ARMATUR")], policy);
        Assert.IsTrue(repeatedRisk.All(item => item.ReasonCodes.Contains("REPEATED_SOURCE_INCONSISTENT", StringComparer.Ordinal)));
    }

    [TestMethod]
    public void RoutingUsesCanonicalSlotRestorationForWhitespaceAndVisibleInjection()
    {
        var policy = TranslationRoutingPolicyFactory.Auto();
        var formatted = Segment('a', "⟦T0⟧⟦T1⟧   ⟦T2⟧") with
        {
            ProtectedTokenAliases = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["T0"] = "{",
                ["T1"] = @"\H0.7x;",
                ["T2"] = "}"
            }
        };
        var request = RequestPayload([formatted], policy);

        var normalized = TranslationRiskEvaluator.Evaluate(
            request,
            [Proposal('a', "⟦T0⟧⟦T1⟧⟦T2⟧")],
            policy).Single();
        var injected = TranslationRiskEvaluator.Evaluate(
            request,
            [Proposal('a', "⟦T0⟧⟦T1⟧VISIBLE⟦T2⟧")],
            policy).Single();

        CollectionAssert.DoesNotContain(normalized.ReasonCodes.ToArray(), "PROTECTED_TOKEN_CHANGED");
        CollectionAssert.Contains(injected.ReasonCodes.ToArray(), "PROTECTED_TOKEN_CHANGED");
        Assert.AreEqual("high", injected.RiskSeverity);
    }

    [TestMethod]
    public void RepeatedSourceConsistencyIsPartitionedBySemanticKey()
    {
        var policy = TranslationRoutingPolicyFactory.Auto();
        var first = Segment('a', "VALVE") with
        {
            Context = Segment('a', "VALVE").Context with { SemanticKey = "sha256:" + new string('a', 64) }
        };
        var second = Segment('b', "VALVE") with
        {
            Context = Segment('b', "VALVE").Context with { SemanticKey = "sha256:" + new string('b', 64) }
        };

        var risks = TranslationRiskEvaluator.Evaluate(RequestPayload([first, second], policy),
            [Proposal('a', "VENTIL"), Proposal('b', "ARMATUR")], policy);

        Assert.IsTrue(risks.All(item => !item.ReasonCodes.Contains("REPEATED_SOURCE_INCONSISTENT", StringComparer.Ordinal)));
    }

    [TestMethod]
    public async Task AutoEscalatesFullValidatedRequestAndOverlaysOnlyHighRisk()
    {
        var handler = new RoutingHandler(solAccessible: true);
        var request = RequestEnvelope(TranslationRoutingPolicyFactory.Auto());
        var gateway = Gateway(handler, new TierProvider(true));

        var result = await gateway.TranslateAsync(request, default);

        Assert.IsTrue(result.IsSuccess, result.Error?.Code);
        Assert.AreEqual(2, handler.Bodies.Count);
        var first = JsonNode.Parse(handler.Bodies[0])!.AsObject();
        var second = JsonNode.Parse(handler.Bodies[1])!.AsObject();
        Assert.AreEqual(TranslationRouting.Terra, first["model"]!.GetValue<string>());
        Assert.AreEqual(TranslationRouting.Sol, second["model"]!.GetValue<string>());
        Assert.AreEqual("none", first["reasoning"]!["effort"]!.GetValue<string>());
        Assert.AreEqual("json_schema", first["text"]!["format"]!["type"]!.GetValue<string>());
        var secondPayload = JsonNode.Parse(second["input"]![1]!["content"]!.GetValue<string>())!.AsObject();
        Assert.AreEqual(2, secondPayload["segments"]!.AsArray().Count);
        Assert.AreEqual(Id('a'), secondPayload["segments"]![0]!["segmentId"]!.GetValue<string>());
        Assert.AreEqual(Id('b'), secondPayload["segments"]![1]!["segmentId"]!.GetValue<string>());
        Assert.IsNull(secondPayload["escalation"]);

        var response = result.Value!.Payload!.Deserialize<TranslationBatchResponsePayload>(WebJson);
        Assert.IsNotNull(response?.Routing);
        Assert.AreEqual(13, response.Usage.InputTokens);
        Assert.AreEqual(7, response.Usage.OutputTokens);
        Assert.AreEqual(1, response.Routing.EscalatedSegmentCount);
        Assert.AreEqual(2, response.Routing.Calls.Count);
        Assert.IsTrue(response.Routing.Calls.All(call => call.RequestId?.StartsWith("req_", StringComparison.Ordinal) == true));
        Assert.AreEqual(TranslationRouting.Sol, response.Routing.Segments.Single(item => item.SegmentId == Id('a')).EffectiveModel);
        Assert.AreEqual(TranslationRouting.Terra, response.Routing.Segments.Single(item => item.SegmentId == Id('b')).EffectiveModel);
        Assert.AreEqual("VENTIL", response.Proposals.Single(item => item.SegmentId == Id('b')).TranslatedTextWithTokenAliases);
        Assert.IsFalse(response.Routing.Segments.Single(item => item.SegmentId == Id('b')).Escalated);
        Assert.IsTrue(TranslationResultPolicy.Validate(request, result.Value).IsSuccess);
    }

    [TestMethod]
    public async Task EscalatedTraceIsReevaluatedAgainstTheFinalFullBatch()
    {
        var policy = TranslationRoutingPolicyFactory.Auto();
        var request = RequestEnvelope(RequestPayload([Segment('a', "VALVE 1"), Segment('b', "VALVE 1")], policy));

        var result = await Gateway(new RepeatedSourceEscalationHandler(), new TierProvider(true)).TranslateAsync(request, default);

        Assert.IsTrue(result.IsSuccess, result.Error?.Code);
        var response = result.Value!.Payload!.Deserialize<TranslationBatchResponsePayload>(WebJson)!;
        var trace = response.Routing!.Segments.Single(item => item.SegmentId == Id('a'));
        Assert.AreEqual("medium", trace.RiskSeverity);
        Assert.AreEqual("review", trace.ValidatorResult);
        Assert.IsTrue(TranslationResultPolicy.Validate(request, result.Value).IsSuccess);
    }

    [TestMethod]
    public async Task AutoKeepsTerraReviewProposalWhenSolIsNotAccessible()
    {
        var handler = new RoutingHandler(solAccessible: false);
        var request = RequestEnvelope(TranslationRoutingPolicyFactory.Auto());
        var result = await Gateway(handler, new TierProvider(false)).TranslateAsync(request, default);

        Assert.IsTrue(result.IsSuccess, result.Error?.Code);
        Assert.AreEqual(1, handler.Bodies.Count);
        var response = result.Value!.Payload!.Deserialize<TranslationBatchResponsePayload>(WebJson)!;
        Assert.AreEqual(0, response.Routing!.EscalatedSegmentCount);
        var trace = response.Routing.Segments.Single(item => item.SegmentId == Id('a'));
        Assert.AreEqual(TranslationRouting.Terra, trace.EffectiveModel);
        CollectionAssert.Contains(trace.EscalationReasonCodes, "SOL_UNAVAILABLE_OR_FAILED");
        Assert.AreEqual("high", trace.RiskSeverity);
    }

    [TestMethod]
    public async Task ContextualHighRiskEscalationKeepsLowerRiskNeighborClosure()
    {
        var handler = new RoutingHandler(solAccessible: true);
        var request = ContextualRequestEnvelope(TranslationRoutingPolicyFactory.Auto());

        var result = await Gateway(handler, new TierProvider(true)).TranslateAsync(request, default);

        Assert.IsTrue(result.IsSuccess, result.Error?.Code);
        var sol = JsonNode.Parse(handler.Bodies[1])!.AsObject();
        var payload = JsonNode.Parse(sol["input"]![1]!["content"]!.GetValue<string>())!.AsObject();
        Assert.AreEqual(2, payload["segments"]!.AsArray().Count);
        var origin = payload["segments"]![0]!.AsObject();
        Assert.AreEqual(Id('b'), origin["context"]!["neighborExcerpts"]![0]!["segmentId"]!.GetValue<string>());
        Assert.AreEqual("VALVE", origin["context"]!["neighborExcerpts"]![0]!["text"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("extra")]
    [DataRow("duplicate")]
    [DataRow("wrong-id")]
    public async Task InvalidFullSolResponseIsDiscardedWithoutPartialOverlay(string fault)
    {
        var result = await Gateway(new InvalidEscalationHandler(fault), new TierProvider(true))
            .TranslateAsync(RequestEnvelope(TranslationRoutingPolicyFactory.Auto()), default);

        Assert.IsTrue(result.IsSuccess, result.Error?.Code);
        var response = result.Value!.Payload!.Deserialize<TranslationBatchResponsePayload>(WebJson)!;
        Assert.AreEqual("ROHR 101 mm", response.Proposals.Single(item => item.SegmentId == Id('a')).TranslatedTextWithTokenAliases);
        Assert.AreEqual("VENTIL", response.Proposals.Single(item => item.SegmentId == Id('b')).TranslatedTextWithTokenAliases);
        Assert.AreEqual(TranslationRouting.Terra, response.Routing!.Segments.Single(item => item.SegmentId == Id('a')).EffectiveModel);
        CollectionAssert.Contains(response.Routing.Segments.Single(item => item.SegmentId == Id('a')).EscalationReasonCodes,
            "SOL_UNAVAILABLE_OR_FAILED");
        Assert.AreEqual("failed", response.Routing.Calls[1].Outcome);
        Assert.AreEqual(3, response.Routing.Calls[1].Usage.InputTokens);
        Assert.AreEqual(2, response.Routing.Calls[1].Usage.OutputTokens);
        Assert.AreEqual("req_invalid_sol", response.Routing.Calls[1].RequestId);
        Assert.AreEqual(2, response.Routing.Calls.Count);
        Assert.AreEqual(6, response.Usage.InputTokens);
        Assert.AreEqual(4, response.Usage.OutputTokens);
    }

    [TestMethod]
    public async Task ProtectedTokenTamperFromSolIsAccountedButNeverOverlaid()
    {
        var payload = RequestPayload([Segment('a', "PIPE ⟦T0⟧ 100") with
        {
            ProtectedTokenAliases = new Dictionary<string, string>(StringComparer.Ordinal) { ["T0"] = "{TAG}" }
        }, Segment('b', "VALVE")], TranslationRoutingPolicyFactory.Auto());
        var result = await Gateway(new ProtectedTokenEscalationHandler(), new TierProvider(true))
            .TranslateAsync(RequestEnvelope(payload), default);

        Assert.IsTrue(result.IsSuccess, result.Error?.Code);
        var response = result.Value!.Payload!.Deserialize<TranslationBatchResponsePayload>(WebJson)!;
        Assert.AreEqual("ROHR ⟦T0⟧ 101", response.Proposals[0].TranslatedTextWithTokenAliases);
        Assert.AreEqual(TranslationRouting.Terra, response.Routing!.Segments[0].EffectiveModel);
        Assert.AreEqual("failed", response.Routing.Calls[1].Outcome);
        Assert.AreEqual("req_token_tamper", response.Routing.Calls[1].RequestId);
        Assert.AreEqual(6, response.Usage.InputTokens);
        Assert.AreEqual(4, response.Usage.OutputTokens);
    }

    [TestMethod]
    [DataRow(TranslationRouting.Economy, TranslationRouting.Luna)]
    [DataRow(TranslationRouting.MaximumQuality, TranslationRouting.Sol)]
    public async Task ExplicitModesSendOneExactTierRequestWithoutEscalation(string mode, string expectedModel)
    {
        var policy = mode == TranslationRouting.Economy ? TranslationRoutingPolicyFactory.Economy() : TranslationRoutingPolicyFactory.MaximumQuality();
        var handler = new RoutingHandler(solAccessible: true);
        var result = await Gateway(handler, new TierProvider(true)).TranslateAsync(RequestEnvelope(policy), default);

        Assert.IsTrue(result.IsSuccess, result.Error?.Code);
        Assert.AreEqual(1, handler.Bodies.Count);
        var body = JsonNode.Parse(handler.Bodies[0])!.AsObject();
        Assert.AreEqual(expectedModel, body["model"]!.GetValue<string>());
        Assert.AreEqual("none", body["reasoning"]!["effort"]!.GetValue<string>());
        var response = result.Value!.Payload!.Deserialize<TranslationBatchResponsePayload>(WebJson)!;
        Assert.AreEqual(0, response.Routing!.EscalatedSegmentCount);
        Assert.AreEqual(expectedModel, response.Routing.BaseModel);
    }

    [TestMethod]
    public async Task InvalidTerraSchemaGetsOneFullBatchSolRetryWithAuditedFailedCall()
    {
        var handler = new SchemaRetryHandler();
        var request = RequestEnvelope(TranslationRoutingPolicyFactory.Auto());

        var result = await Gateway(handler, new TierProvider(true)).TranslateAsync(request, default);

        Assert.IsTrue(result.IsSuccess, result.Error?.Code);
        Assert.AreEqual(2, handler.Bodies.Count);
        var retry = JsonNode.Parse(handler.Bodies[1])!.AsObject();
        Assert.AreEqual(2, JsonNode.Parse(retry["input"]![1]!["content"]!.GetValue<string>())!["segments"]!.AsArray().Count);
        var response = result.Value!.Payload!.Deserialize<TranslationBatchResponsePayload>(WebJson)!;
        Assert.AreEqual(2, response.Routing!.Calls.Count);
        Assert.AreEqual("failed", response.Routing.Calls[0].Outcome);
        Assert.AreEqual("OPENAI_STRUCTURED_OUTPUT_INVALID", response.Routing.Calls[0].ErrorCode);
        Assert.AreEqual("succeeded", response.Routing.Calls[1].Outcome);
        Assert.AreEqual(2, response.Routing.EscalatedSegmentCount);
    }

    [TestMethod]
    public async Task MissingBaseTierFailsWithoutSilentSubstitutionOrHttpCall()
    {
        var handler = new RoutingHandler(solAccessible: false);
        var request = RequestEnvelope(TranslationRoutingPolicyFactory.Economy());
        var provider = new TerraOnlyProvider();

        var result = await Gateway(handler, provider).TranslateAsync(request, default);

        Assert.AreEqual("OPENAI_BASE_MODEL_UNAVAILABLE", result.Error!.Code);
        Assert.AreEqual(0, handler.Bodies.Count);
    }

    [TestMethod]
    public void RedactedTraceContractContainsNoTextOrSecretFields()
    {
        var trace = new TranslationRoutingTrace
        {
            RequestedMode = TranslationRouting.Auto,
            BaseModel = TranslationRouting.Terra,
            RoutingVersion = TranslationRouting.PolicyVersion,
            EscalatedSegmentCount = 0,
            Calls = [new TranslationCallTrace
            {
                Tier = "base", RequestedModel = TranslationRouting.Terra, EffectiveModel = TranslationRouting.Terra,
                Usage = new TranslationUsage { InputTokens = 1, OutputTokens = 1 }, LatencyMilliseconds = 2,
                PromptVersion = "p", SchemaVersion = TranslationRouting.SchemaVersion, Outcome = "succeeded"
            }],
            Segments = []
        };
        var json = JsonSerializer.Serialize(trace);
        Assert.IsFalse(json.Contains("apiKey", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("sourceText", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("translatedText", StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertPolicy(TranslationRoutingPolicy policy, string mode, string model, string? escalation, int retries)
    {
        Assert.IsTrue(TranslationRoutingPolicyFactory.Validate(policy).IsSuccess);
        Assert.AreEqual(mode, policy.RequestedMode);
        Assert.AreEqual(model, policy.BaseModel);
        Assert.AreEqual(escalation, policy.EscalationModel);
        Assert.AreEqual(retries, policy.MaxEscalationsPerSegment);
        Assert.AreEqual("none", policy.ReasoningEffort);
    }

    private static void AssertRisk(string source, string target, TranslationRoutingPolicy policy, string severity, string code)
    {
        var result = TranslationRiskEvaluator.Evaluate(RequestPayload([Segment('a', source)], policy), [Proposal('a', target)], policy).Single();
        Assert.AreEqual(severity, result.RiskSeverity, string.Join(',', result.ReasonCodes));
        CollectionAssert.Contains(result.ReasonCodes.ToArray(), code);
    }

    private static void AssertCode(TranslationBatchRequestPayload request, IReadOnlyList<TranslationProposal> proposals, TranslationRoutingPolicy policy, string code)
    {
        var result = TranslationRiskEvaluator.Evaluate(request, proposals, policy).Single();
        CollectionAssert.Contains(result.ReasonCodes.ToArray(), code);
        Assert.AreEqual("high", result.RiskSeverity);
    }

    private static OpenAIRoutedTranslationGateway Gateway(HttpMessageHandler handler, IOpenAiModelProvider provider) => new(
        new HttpClient(handler), new SecretStore(), SecretReference.Create("credential-manager:dwg-translator/openai").Value!, provider,
        new OpenAITranslationGateway.Limits(256, 10_000, 100_000));

    private static WireEnvelope RequestEnvelope(TranslationRoutingPolicy policy)
    {
        var payload = RequestPayload([Segment('a', "PIPE 100 mm"), Segment('b', "VALVE")], policy);
        return RequestEnvelope(payload);
    }

    private static WireEnvelope RequestEnvelope(TranslationBatchRequestPayload payload)
    {
        return WireEnvelope.Request(MessageTypes.TranslationRequest, Guid.NewGuid(), Guid.NewGuid(), TranslationBatchFactory.ConfigurationHash(payload),
            DateTimeOffset.Parse("2026-08-20T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture), JsonSerializer.SerializeToNode(payload, WebJson)!.AsObject());
    }

    private static TranslationBatchRequestPayload RequestPayload(List<TranslationSegment> segments, TranslationRoutingPolicy policy) => new()
    {
        SourceLanguage = "en",
        TargetLanguage = "de",
        PromptTemplateVersion = OpenAITranslationGateway.LegacyPromptTemplateVersion,
        Glossary = [],
        Segments = segments,
        Routing = policy
    };

    private static TranslationSegment Segment(char id, string text) => new()
    {
        SegmentId = Id(id),
        TextWithTokenAliases = text,
        ProtectedTokenAliases = [],
        Context = new TranslationSegmentContext { EntityType = "TEXT", Space = "ModelSpace", Layout = null, Layer = "NOTES", BlockPath = [] }
    };
    private static TranslationProposal Proposal(char id, string text) => new() { SegmentId = Id(id), TranslatedTextWithTokenAliases = text, TokenIntegrity = "Valid" };
    private static string Id(char value) => "seg_sha256_" + new string(value, 64);

    private sealed class TierProvider(bool sol) : IOpenAiModelProvider
    {
        public string Model => TranslationRouting.Terra;
        public bool IsModelAccessible(string model) => model is TranslationRouting.Terra or TranslationRouting.Luna || sol && model == TranslationRouting.Sol;
    }

    private sealed class TerraOnlyProvider : IOpenAiModelProvider
    {
        public string Model => TranslationRouting.Terra;
        public bool IsModelAccessible(string model) => model == TranslationRouting.Terra;
    }

    private sealed class SecretStore : ISecretStore
    {
        public Task<Result<string>> GetAsync(SecretReference reference, CancellationToken cancellationToken) => Task.FromResult(Results.Success("secret"));
        public Task<Result<bool>> SetAsync(SecretReference reference, string secret, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Result<bool>> DeleteAsync(SecretReference reference, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RoutingHandler(bool solAccessible) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Bodies.Add(body);
            var api = JsonNode.Parse(body)!.AsObject();
            var model = api["model"]!.GetValue<string>();
            if (model == TranslationRouting.Sol && !solAccessible) return new HttpResponseMessage(HttpStatusCode.NotFound);
            var requestPayload = JsonNode.Parse(api["input"]![1]!["content"]!.GetValue<string>())!.AsObject();
            var isSubset = requestPayload["segments"]!.AsArray().Count == 1;
            var proposals = model == TranslationRouting.Sol && isSubset
                ? new[] { Proposal('a', "ROHR 100 mm") }
                : model == TranslationRouting.Sol
                    ? new[] { Proposal('a', "ROHR 100 mm"), Proposal('b', "VENTIL") }
                    : new[] { Proposal('a', "ROHR 101 mm"), Proposal('b', "VENTIL") };
            var structured = JsonSerializer.Serialize(new { proposals }, WebJson);
            var response = JsonSerializer.Serialize(new
            {
                status = "completed",
                model,
                output = new[] { new { content = new[] { new { type = "output_text", text = structured } } } },
                usage = model == TranslationRouting.Sol ? new { input_tokens = 3, output_tokens = 2 } : new { input_tokens = 10, output_tokens = 5 }
            });
            var message = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
            message.Headers.Add("x-request-id", $"req_{Bodies.Count}");
            return message;
        }
    }

    private sealed class RepeatedSourceEscalationHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            var model = body["model"]!.GetValue<string>();
            var payload = JsonNode.Parse(body["input"]![1]!["content"]!.GetValue<string>())!.AsObject();
            var proposals = model == TranslationRouting.Sol
                ? new[] { Proposal('a', "VENTIL 1"), Proposal('b', "VENTIL 1") }
                : new[] { Proposal('a', "VENTIL 2"), Proposal('b', "ARMATUR 1") };
            var structured = JsonSerializer.Serialize(new { proposals }, WebJson);
            var response = JsonSerializer.Serialize(new
            {
                status = "completed",
                model,
                output = new[] { new { content = new[] { new { type = "output_text", text = structured } } } },
                usage = new { input_tokens = 1, output_tokens = 1 }
            });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }

    private static WireEnvelope ContextualRequestEnvelope(TranslationRoutingPolicy policy)
    {
        var neighborText = "VALVE";
        var hash = "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(neighborText))).ToLowerInvariant();
        TranslationSegmentContext Context(char value, List<TranslationNeighborExcerpt> neighbors) => new()
        {
            EntityType = "TEXT",
            Space = "ModelSpace",
            Layout = null,
            Layer = "NOTES",
            BlockPath = [],
            ContextVersion = CadSemanticContextBuilder.PolicyVersion,
            ContextHash = "sha256:" + new string(value, 64),
            SemanticKey = "sha256:" + new string('c', 64),
            SheetRole = "Model",
            Discipline = "Controls",
            DisciplineConflict = false,
            XBand = 8,
            YBand = 8,
            Signals = [],
            NeighborExcerpts = neighbors
        };
        var payload = new TranslationBatchRequestPayload
        {
            SourceLanguage = "en",
            TargetLanguage = "de",
            PromptTemplateVersion = TranslationReviewWorkflow.ContextualPromptTemplateVersion,
            Glossary = [],
            Routing = policy,
            Segments =
            [
                new TranslationSegment
                {
                    SegmentId = Id('a'), TextWithTokenAliases = "PIPE 100 mm", ProtectedTokenAliases = [],
                    Context = Context('a', [new TranslationNeighborExcerpt
                    {
                        SegmentId = Id('b'), SourceTextHash = hash, EntityType = "TEXT", Relation = "Right",
                        DistanceBand = 1, SameLayer = true, Text = neighborText
                    }])
                },
                new TranslationSegment
                {
                    SegmentId = Id('b'), TextWithTokenAliases = neighborText, ProtectedTokenAliases = [],
                    Context = Context('b', [])
                }
            ]
        };
        return RequestEnvelope(payload);
    }

    private sealed class InvalidEscalationHandler(string fault) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            var model = body["model"]!.GetValue<string>();
            TranslationProposal[] proposals = model == TranslationRouting.Terra
                ? [Proposal('a', "ROHR 101 mm"), Proposal('b', "VENTIL")]
                : fault switch
                {
                    "missing" => [Proposal('a', "ROHR 100 mm")],
                    "extra" => [Proposal('a', "ROHR 100 mm"), Proposal('b', "VENTIL"), Proposal('c', "EXTRA")],
                    "wrong-id" => [Proposal('a', "ROHR 100 mm"), Proposal('c', "EXTRA")],
                    _ => [Proposal('a', "ROHR 100 mm"), Proposal('a', "ROHR 100 mm")]
                };
            var structured = JsonSerializer.Serialize(new { proposals }, WebJson);
            var response = JsonSerializer.Serialize(new
            {
                status = "completed",
                model,
                output = new[] { new { content = new[] { new { type = "output_text", text = structured } } } },
                usage = new { input_tokens = 3, output_tokens = 2 }
            });
            var message = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
            message.Headers.Add("x-request-id", model == TranslationRouting.Sol ? "req_invalid_sol" : "req_valid_terra");
            return message;
        }
    }

    private sealed class ProtectedTokenEscalationHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            var model = body["model"]!.GetValue<string>();
            var proposals = model == TranslationRouting.Terra
                ? new[] { Proposal('a', "ROHR ⟦T0⟧ 101"), Proposal('b', "VENTIL") }
                : new[] { Proposal('a', "ROHR 100"), Proposal('b', "VENTIL") };
            var structured = JsonSerializer.Serialize(new { proposals }, WebJson);
            var response = JsonSerializer.Serialize(new
            {
                status = "completed",
                model,
                output = new[] { new { content = new[] { new { type = "output_text", text = structured } } } },
                usage = new { input_tokens = 3, output_tokens = 2 }
            });
            var message = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
            message.Headers.Add("x-request-id", model == TranslationRouting.Sol ? "req_token_tamper" : "req_token_base");
            return message;
        }
    }

    private sealed class SchemaRetryHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Bodies.Add(body);
            var model = JsonNode.Parse(body)!["model"]!.GetValue<string>();
            var text = model == TranslationRouting.Terra
                ? "{not-json"
                : JsonSerializer.Serialize(new { proposals = new[] { Proposal('a', "ROHR 100 mm"), Proposal('b', "VENTIL") } }, WebJson);
            var response = JsonSerializer.Serialize(new
            {
                status = "completed",
                model,
                output = new[] { new { content = new[] { new { type = "output_text", text } } } },
                usage = new { input_tokens = 4, output_tokens = 2 }
            });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
