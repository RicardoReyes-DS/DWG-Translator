using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace DwgTranslator.Core.Tests;

[TestClass]
public sealed class SchemaTests
{
    [TestMethod]
    public void AllSchemasDeclareDraft202012AndCompile()
    {
        var buildOptions = new BuildOptions { SchemaRegistry = new SchemaRegistry() };
        foreach (var path in SchemaPaths)
        {
            var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert.AreEqual("https://json-schema.org/draft/2020-12/schema", node["$schema"]!.GetValue<string>(), path);
            _ = JsonSchema.FromText(node.ToJsonString(), buildOptions);
        }
    }

    [TestMethod]
    public void EveryDeclaredFixtureHasExpectedValidity()
    {
        var buildOptions = new BuildOptions { SchemaRegistry = new SchemaRegistry() };
        var schemas = SchemaPaths.Select(path => JsonSchema.FromText(File.ReadAllText(path), buildOptions)).ToArray();
        var options = new EvaluationOptions { OutputFormat = OutputFormat.List };

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "fixtures", "v1", "manifest.json")));
        foreach (var fixture in manifest.RootElement.GetProperty("fixtures").EnumerateArray())
        {
            var schemaName = fixture.GetProperty("schema").GetString()!;
            var schema = schemas.Single(item => item.BaseUri.OriginalString.EndsWith(schemaName, StringComparison.Ordinal));
            var fixturePath = Path.Combine(Root, "fixtures", "v1", fixture.GetProperty("path").GetString()!);
            using var instance = JsonDocument.Parse(File.ReadAllText(fixturePath));
            var result = schema.Evaluate(instance.RootElement, options);
            Assert.AreEqual(fixture.GetProperty("valid").GetBoolean(), result.IsValid, $"Fixture: {fixturePath}\n{JsonSerializer.Serialize(result)}");
        }
    }

    [TestMethod]
    public void EveryContractFamilyHasValidAndInvalidFixtures()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "fixtures", "v1", "manifest.json")));
        var fixtures = manifest.RootElement.GetProperty("fixtures").EnumerateArray().ToArray();
        foreach (var schema in fixtures.Select(item => item.GetProperty("schema").GetString()!).Distinct(StringComparer.Ordinal))
        {
            Assert.IsTrue(fixtures.Any(item => item.GetProperty("schema").GetString() == schema && item.GetProperty("valid").GetBoolean()), schema);
            Assert.IsTrue(fixtures.Any(item => item.GetProperty("schema").GetString() == schema && !item.GetProperty("valid").GetBoolean()), schema);
        }
    }

    [TestMethod]
    public void SemanticContextSchemasAllowVersionOnePointOneResolutionButKeepOnePointZeroClosed()
    {
        var buildOptions = new BuildOptions { SchemaRegistry = new SchemaRegistry() };
        var schemas = SchemaPaths.Select(path => JsonSchema.FromText(File.ReadAllText(path), buildOptions)).ToArray();
        var evaluationOptions = new EvaluationOptions { OutputFormat = OutputFormat.List };

        var cad = JsonNode.Parse(File.ReadAllText(Path.Combine(
            Root, "fixtures", "v1", "valid", "cad-extract-response-context.json")))!.AsObject();
        var cadContext = cad["payload"]!["segments"]![0]!["semanticContext"]!.AsObject();
        cadContext["version"] = "cad-semantic-context/1.1";
        cadContext["discipline"] = "Architectural";
        cadContext["disciplineEvidence"] = new JsonArray("DRAWING_NAME_CONTROLS", "LAYER_ARCHITECTURAL");
        cadContext["disciplineResolution"] = "LOCAL_EVIDENCE_OVERRIDES_DRAWING_HINT";
        var cadSchema = schemas.Single(item => item.BaseUri.OriginalString.EndsWith(
            "cad-operation.schema.json", StringComparison.Ordinal));
        Assert.IsTrue(cadSchema.Evaluate(JsonDocument.Parse(cad.ToJsonString()).RootElement, evaluationOptions).IsValid);
        cadContext["version"] = "cad-semantic-context/1.0";
        Assert.IsFalse(cadSchema.Evaluate(JsonDocument.Parse(cad.ToJsonString()).RootElement, evaluationOptions).IsValid);

        var translation = JsonNode.Parse(File.ReadAllText(Path.Combine(
            Root, "fixtures", "v1", "valid", "translation-request-context.json")))!.AsObject();
        var outboundContext = translation["payload"]!["segments"]![0]!["context"]!.AsObject();
        outboundContext["contextVersion"] = "cad-semantic-context/1.1";
        outboundContext["discipline"] = "Architectural";
        outboundContext["disciplineResolution"] = "LOCAL_EVIDENCE_OVERRIDES_DRAWING_HINT";
        var translationSchema = schemas.Single(item => item.BaseUri.OriginalString.EndsWith(
            "translation-batch.schema.json", StringComparison.Ordinal));
        Assert.IsTrue(translationSchema.Evaluate(
            JsonDocument.Parse(translation.ToJsonString()).RootElement, evaluationOptions).IsValid);
        outboundContext["contextVersion"] = "cad-semantic-context/1.0";
        Assert.IsFalse(translationSchema.Evaluate(
            JsonDocument.Parse(translation.ToJsonString()).RootElement, evaluationOptions).IsValid);

        cadContext["version"] = "cad-semantic-context/1.2";
        cadContext["discipline"] = "Architectural";
        cadContext["disciplineEvidence"] = new JsonArray();
        cadContext["disciplineConflict"] = false;
        cadContext["disciplineResolution"] = "DRAWING_NAME_ARCHITECTURAL_FALLBACK";
        Assert.IsTrue(cadSchema.Evaluate(
            JsonDocument.Parse(cad.ToJsonString()).RootElement, evaluationOptions).IsValid);
        cadContext["disciplineEvidence"] = new JsonArray("LAYER_ARCHITECTURAL");
        Assert.IsFalse(cadSchema.Evaluate(
            JsonDocument.Parse(cad.ToJsonString()).RootElement, evaluationOptions).IsValid);

        outboundContext["contextVersion"] = "cad-semantic-context/1.2";
        outboundContext["discipline"] = "Architectural";
        outboundContext["disciplineConflict"] = false;
        outboundContext["disciplineResolution"] = "DRAWING_NAME_ARCHITECTURAL_FALLBACK";
        Assert.IsTrue(translationSchema.Evaluate(
            JsonDocument.Parse(translation.ToJsonString()).RootElement, evaluationOptions).IsValid);
        outboundContext["discipline"] = "Mechanical";
        Assert.IsFalse(translationSchema.Evaluate(
            JsonDocument.Parse(translation.ToJsonString()).RootElement, evaluationOptions).IsValid);

        cadContext["version"] = "cad-semantic-context/1.3";
        cadContext["discipline"] = "Structural";
        cadContext["disciplineEvidence"] = new JsonArray();
        cadContext["disciplineResolution"] = "DRAWING_NAME_STRUCTURAL_FALLBACK";
        Assert.IsTrue(cadSchema.Evaluate(JsonDocument.Parse(cad.ToJsonString()).RootElement, evaluationOptions).IsValid);
        cadContext["version"] = "cad-semantic-context/1.2";
        Assert.IsFalse(cadSchema.Evaluate(JsonDocument.Parse(cad.ToJsonString()).RootElement, evaluationOptions).IsValid);

        outboundContext["contextVersion"] = "cad-semantic-context/1.3";
        outboundContext["discipline"] = "Electrical";
        outboundContext["disciplineResolution"] = "DRAWING_NAME_ELECTRICAL_FALLBACK";
        Assert.IsTrue(translationSchema.Evaluate(
            JsonDocument.Parse(translation.ToJsonString()).RootElement, evaluationOptions).IsValid);
        outboundContext["contextVersion"] = "cad-semantic-context/1.2";
        Assert.IsFalse(translationSchema.Evaluate(
            JsonDocument.Parse(translation.ToJsonString()).RootElement, evaluationOptions).IsValid);
    }

    private static string[] SchemaPaths => Directory.GetFiles(Path.Combine(Root, "contracts", "v1"), "*.schema.json");

    private static string Root
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "fixtures", "v1")))
                directory = directory.Parent;
            return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
        }
    }
}
