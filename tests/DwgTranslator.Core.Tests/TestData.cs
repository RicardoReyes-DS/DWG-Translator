using System.Text.Json.Nodes;
using DwgTranslator.Contracts;
using DwgTranslator.Application;

namespace DwgTranslator.Core.Tests;

internal static class TestData
{
    internal const string HashA = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    internal const string HashB = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    internal static WireEnvelope Request(JsonObject? payload = null) => WireEnvelope.Request(
        MessageTypes.CapabilitiesRequest,
        Guid.NewGuid(),
        Guid.NewGuid(),
        HashA,
        DateTimeOffset.Parse("2026-08-11T20:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
        payload ?? new JsonObject());

    internal static CadVisualInvariantEvidence VisualEvidence(CadWriteMapping mapping)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["handle"] = mapping.Handle,
            ["entityType"] = mapping.ExpectedEntityType!,
            ["space"] = mapping.ExpectedSpace!,
            ["layout"] = mapping.ExpectedLayout ?? string.Empty,
            ["ownerRecord"] = "*Model_Space",
            ["layer"] = mapping.ExpectedLayer!,
            ["colorIndex"] = "256",
            ["linetypeHandle"] = "14",
            ["lineWeight"] = "-1",
            ["position"] = "10,20,0",
            ["alignmentPoint"] = "10,20,0",
            ["height"] = "2.5",
            ["rotation"] = "0",
            ["widthFactor"] = "1",
            ["oblique"] = "0",
            ["textStyleHandle"] = "11",
            ["horizontalMode"] = "TextLeft",
            ["verticalMode"] = "TextBase",
            ["normal"] = "0,0,1",
            ["thickness"] = "0",
            ["mirroredInX"] = "False",
            ["mirroredInY"] = "False"
        };
        var fingerprint = CadVisualEvidencePolicy.Fingerprint(properties);
        return new CadVisualInvariantEvidence
        {
            BeforeProperties = new(properties, StringComparer.Ordinal),
            AfterProperties = new(properties, StringComparer.Ordinal),
            BeforeFingerprint = fingerprint,
            AfterFingerprint = fingerprint,
            InvariantMatch = true,
            BeforeExtents = "10,20,0:20,22.5,0",
            AfterExtents = "10,20,0:25,22.5,0",
            BoundsChanged = true,
            VisualReviewRequired = true
        };
    }
}
