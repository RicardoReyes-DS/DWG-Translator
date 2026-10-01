using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DwgTranslator.Contracts;

namespace DwgTranslator.Application;

public sealed record CadSemanticContextInput(
    string SegmentId,
    string EntityType,
    string Handle,
    string Space,
    string? Layout,
    IReadOnlyList<string> BlockPath,
    string Layer,
    string SourceText,
    string SourceTextHash,
    double AnchorX,
    double AnchorY,
    double TextHeight,
    string AnchorSource,
    string? DrawingHint = null);

public static class CadSemanticContextBuilder
{
    public const string PolicyVersionOneZero = "cad-semantic-context/1.0";
    public const string PolicyVersionOneOne = "cad-semantic-context/1.1";
    public const string PolicyVersionOneTwo = "cad-semantic-context/1.2";
    public const string PolicyVersion = PolicyVersionOneZero;
    public const string CurrentPolicyVersion = PolicyVersionOneTwo;
    public const string LocalEvidenceOverridesDrawingHint = "LOCAL_EVIDENCE_OVERRIDES_DRAWING_HINT";
    public const string DrawingNameArchitecturalFallback = "DRAWING_NAME_ARCHITECTURAL_FALLBACK";
    public const int MaximumNeighbors = 4;
    public const double MaximumNeighborDistanceInTextHeights = 40d;

    private static readonly string[] DisciplineOrder =
        ["Architectural", "Mechanical", "Electrical", "Plumbing", "Fire", "Controls"];

    public static Result<IReadOnlyDictionary<string, CadSemanticContext>> Build(
        IReadOnlyList<CadSemanticContextInput> inputs) => Build(inputs, PolicyVersionOneZero);

    public static Result<IReadOnlyDictionary<string, CadSemanticContext>> Build(
        IReadOnlyList<CadSemanticContextInput> inputs,
        string policyVersion)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (!IsSupportedPolicyVersion(policyVersion))
            return Failure<IReadOnlyDictionary<string, CadSemanticContext>>(
                "CAD_SEMANTIC_CONTEXT_VERSION_UNSUPPORTED", "The requested semantic context policy version is not supported.");
        if (inputs.Count > 100_000 || inputs.Any(input => !Valid(input)) ||
            inputs.Select(input => input.SegmentId).Distinct(StringComparer.Ordinal).Count() != inputs.Count ||
            inputs.Select(input => input.Handle).Distinct(StringComparer.Ordinal).Count() != inputs.Count)
        {
            return Failure<IReadOnlyDictionary<string, CadSemanticContext>>(
                "CAD_SEMANTIC_CONTEXT_INPUT_INVALID", "Semantic context inputs are invalid or duplicate an identity.");
        }

        var result = new Dictionary<string, CadSemanticContext>(inputs.Count, StringComparer.Ordinal);
        foreach (var scope in inputs.GroupBy(input => new Scope(input.Space, input.Layout), ScopeComparer.Instance))
        {
            var scoped = scope.ToArray();
            var ordered = scoped.OrderByDescending(input => input.AnchorY)
                .ThenBy(input => input.AnchorX)
                .ThenBy(input => input.Handle, CadHandleComparer.Instance)
                .ToArray();
            var readingOrder = ordered.Select((input, index) => (input.SegmentId, Index: index))
                .ToDictionary(item => item.SegmentId, item => item.Index, StringComparer.Ordinal);
            var minX = scoped.Min(input => input.AnchorX);
            var maxX = scoped.Max(input => input.AnchorX);
            var minY = scoped.Min(input => input.AnchorY);
            var maxY = scoped.Max(input => input.AnchorY);

            foreach (var input in scoped)
            {
                var candidates = scoped.Where(candidate => candidate.SegmentId != input.SegmentId)
                    .Select(candidate => NeighborCandidate.Create(input, candidate))
                    .Where(candidate => candidate.NormalizedDistance <= MaximumNeighborDistanceInTextHeights)
                    .OrderBy(candidate => candidate.NormalizedDistance)
                    .ThenBy(candidate => candidate.Input.Handle, CadHandleComparer.Instance)
                    .Take(MaximumNeighbors)
                    .ToArray();
                var neighbors = candidates.Select(candidate => new CadSemanticNeighbor
                {
                    SegmentId = candidate.Input.SegmentId,
                    SourceTextHash = candidate.Input.SourceTextHash,
                    EntityType = candidate.Input.EntityType,
                    Relation = Relation(input, candidate.Input, candidate.NormalizedDistance),
                    DistanceBand = DistanceBand(candidate.NormalizedDistance),
                    SameLayer = string.Equals(input.Layer, candidate.Input.Layer, StringComparison.Ordinal)
                }).ToList();
                var discipline = ResolveDiscipline(input, policyVersion);
                var signals = ResolveSignals(input, candidates.Select(candidate => candidate.Input));
                var sheetRole = input.Space == "PaperSpace" ? "Sheet" : "Model";
                var xBand = CoordinateBand(input.AnchorX, minX, maxX);
                var yBand = CoordinateBand(input.AnchorY, minY, maxY);
                var neighborhoodDigest = Hash(NeighborhoodHashDomain(policyVersion), candidates.SelectMany(candidate => new[]
                {
                    Relation(input, candidate.Input, candidate.NormalizedDistance),
                    DistanceBand(candidate.NormalizedDistance).ToString(CultureInfo.InvariantCulture),
                    string.Equals(input.Layer, candidate.Input.Layer, StringComparison.Ordinal) ? "same-layer" : "other-layer",
                    candidate.Input.EntityType,
                    Normalize(candidate.Input.SourceText)
                }));
                var semanticKey = Hash(SemanticKeyHashDomain(policyVersion), SemanticKeyValues(
                    policyVersion, sheetRole, discipline, signals, neighborhoodDigest));
                var contextHash = Hash(ContextHashDomain(policyVersion), new[]
                {
                    policyVersion,
                    input.SegmentId,
                    input.EntityType,
                    input.Handle,
                    input.Space,
                    input.Layout ?? string.Empty,
                    input.Layer,
                    input.AnchorSource,
                    xBand.ToString(CultureInfo.InvariantCulture),
                    yBand.ToString(CultureInfo.InvariantCulture),
                    readingOrder[input.SegmentId].ToString(CultureInfo.InvariantCulture),
                    semanticKey,
                    neighborhoodDigest,
                    string.Join("\u001f", neighbors.Select(neighbor =>
                        $"{neighbor.SegmentId}|{neighbor.SourceTextHash}|{neighbor.EntityType}|{neighbor.Relation}|{neighbor.DistanceBand}|{neighbor.SameLayer}"))
                });
                result.Add(input.SegmentId, new CadSemanticContext
                {
                    Version = policyVersion,
                    ContextHash = contextHash,
                    SemanticKey = semanticKey,
                    SheetRole = sheetRole,
                    Discipline = discipline.Name,
                    DisciplineEvidence = discipline.Evidence.ToList(),
                    DisciplineConflict = discipline.Conflict,
                    DisciplineResolution = discipline.Resolution,
                    AnchorSource = input.AnchorSource,
                    XBand = xBand,
                    YBand = yBand,
                    ReadingOrder = readingOrder[input.SegmentId],
                    Signals = signals.ToList(),
                    NeighborhoodDigest = neighborhoodDigest,
                    Neighbors = neighbors
                });
            }
        }
        return Results.Success<IReadOnlyDictionary<string, CadSemanticContext>>(result);
    }

    public static bool IsSupportedPolicyVersion(string? policyVersion) =>
        policyVersion is PolicyVersionOneZero or PolicyVersionOneOne or PolicyVersionOneTwo;

    public static Result<string> AggregateHash(IEnumerable<CadTextSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var materialized = segments.ToArray();
        if (materialized.Length == 0 || materialized.Length > 100_000 ||
            materialized.Any(segment => segment is null || segment.Entity is null || segment.Entity.BlockPath is null ||
                segment.SourceText is null || !ContractPatterns.SegmentId().IsMatch(segment.SegmentId ?? string.Empty) ||
                !string.Equals(segment.SourceTextHash, SourceTextHash(segment.SourceText), StringComparison.Ordinal)) ||
            materialized.Select(segment => segment.SegmentId).Distinct(StringComparer.Ordinal).Count() != materialized.Length)
        {
            return Failure<string>("CAD_SEMANTIC_CONTEXT_SET_INVALID", "Semantic context aggregation requires a unique non-empty segment set.");
        }
        if (materialized.Any(segment => segment.SemanticContext is null))
            return Failure<string>("CAD_SEMANTIC_CONTEXT_LEGACY_MISSING", "A legacy segment has no semantic context.");

        var versions = materialized.Select(segment => segment.SemanticContext!.Version)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (versions.Length != 1)
            return Failure<string>("CAD_SEMANTIC_CONTEXT_VERSION_MIXED", "Semantic context aggregation rejects mixed policy versions.");
        var policyVersion = versions[0];
        if (!IsSupportedPolicyVersion(policyVersion))
            return Failure<string>("CAD_SEMANTIC_CONTEXT_SET_INVALID", "The semantic context policy version is unsupported.");

        var byId = materialized.ToDictionary(segment => segment.SegmentId, StringComparer.Ordinal);
        foreach (var segment in materialized)
        {
            var context = segment.SemanticContext!;
            if (!ValidContext(segment, context, byId))
                return Failure<string>("CAD_SEMANTIC_CONTEXT_SET_INVALID", "A semantic context is incomplete, tampered, or cross-references an invalid neighbor.");
        }
        if (materialized.GroupBy(segment => new Scope(segment.Entity.Space, segment.Entity.Layout), ScopeComparer.Instance)
            .Any(scope => scope.Select(segment => segment.SemanticContext!.ReadingOrder).Distinct().Count() != scope.Count() ||
                          scope.Any(segment => segment.SemanticContext!.ReadingOrder >= scope.Count())))
            return Failure<string>("CAD_SEMANTIC_CONTEXT_SET_INVALID", "Semantic context reading order is not a complete per-scope sequence.");
        return Results.Success(Hash(AggregateHashDomain(policyVersion),
            materialized.OrderBy(segment => segment.SegmentId, StringComparer.Ordinal).SelectMany(segment => new[]
            {
                segment.SegmentId,
                segment.SourceTextHash,
                segment.SemanticContext!.Version,
                segment.SemanticContext.ContextHash,
                segment.SemanticContext.SemanticKey
            })));
    }

    public static Result<IReadOnlyList<CadTextSegment>> UpgradeOneOneToOneTwo(
        IReadOnlyList<CadTextSegment> segments,
        string manifestBoundBasename)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (!IsArchitecturalManifestBasename(manifestBoundBasename) ||
            segments.Count == 0 || segments.Any(segment =>
                segment.SemanticContext?.Version != PolicyVersionOneOne))
            return Failure<IReadOnlyList<CadTextSegment>>("CAD_SEMANTIC_CONTEXT_UPGRADE_INVALID",
                "The semantic context upgrade requires an exact 1.1 set and an ARQ manifest basename.");

        var original = AggregateHash(segments);
        if (!original.IsSuccess)
            return Results.Failure<IReadOnlyList<CadTextSegment>>(original.Error!);
        var byId = segments.ToDictionary(segment => segment.SegmentId, StringComparer.Ordinal);
        var upgraded = new List<CadTextSegment>(segments.Count);
        foreach (var segment in segments)
        {
            var context = segment.SemanticContext!;
            var local = ResolveDisciplineV1_0(segment.Entity.Layer, segment.Entity.Layout, segment.Entity.BlockPath);
            var discipline = ResolveDisciplineV1_2(local,
                context.DisciplineEvidence.Contains("DRAWING_NAME_CONTROLS", StringComparer.Ordinal),
                drawingArchitectural: true);
            var neighborhoodDigest = Hash(NeighborhoodHashDomain(PolicyVersionOneTwo), context.Neighbors.SelectMany(neighbor => new[]
            {
                neighbor.Relation,
                neighbor.DistanceBand.ToString(CultureInfo.InvariantCulture),
                neighbor.SameLayer ? "same-layer" : "other-layer",
                neighbor.EntityType,
                Normalize(byId[neighbor.SegmentId].SourceText)
            }));
            var semanticKey = Hash(SemanticKeyHashDomain(PolicyVersionOneTwo), SemanticKeyValues(
                PolicyVersionOneTwo, context.SheetRole, discipline, context.Signals, neighborhoodDigest));
            var contextHash = Hash(ContextHashDomain(PolicyVersionOneTwo), new[]
            {
                PolicyVersionOneTwo,
                segment.SegmentId,
                segment.Entity.Type,
                segment.Entity.Handle,
                segment.Entity.Space,
                segment.Entity.Layout ?? string.Empty,
                segment.Entity.Layer,
                context.AnchorSource,
                context.XBand.ToString(CultureInfo.InvariantCulture),
                context.YBand.ToString(CultureInfo.InvariantCulture),
                context.ReadingOrder.ToString(CultureInfo.InvariantCulture),
                semanticKey,
                neighborhoodDigest,
                string.Join("\u001f", context.Neighbors.Select(neighbor =>
                    $"{neighbor.SegmentId}|{neighbor.SourceTextHash}|{neighbor.EntityType}|{neighbor.Relation}|{neighbor.DistanceBand}|{neighbor.SameLayer}"))
            });
            upgraded.Add(segment with
            {
                SemanticContext = context with
                {
                    Version = PolicyVersionOneTwo,
                    ContextHash = contextHash,
                    SemanticKey = semanticKey,
                    Discipline = discipline.Name,
                    DisciplineEvidence = discipline.Evidence.ToList(),
                    DisciplineConflict = discipline.Conflict,
                    DisciplineResolution = discipline.Resolution,
                    NeighborhoodDigest = neighborhoodDigest
                }
            });
        }
        var verified = AggregateHash(upgraded);
        return verified.IsSuccess
            ? Results.Success<IReadOnlyList<CadTextSegment>>(upgraded)
            : Results.Failure<IReadOnlyList<CadTextSegment>>(verified.Error!);
    }

    private static bool ValidContext(CadTextSegment segment, CadSemanticContext context,
        Dictionary<string, CadTextSegment> segments)
    {
        if (!IsSupportedPolicyVersion(context.Version) || !ContractPatterns.Sha256().IsMatch(context.ContextHash ?? string.Empty) ||
            !ContractPatterns.Sha256().IsMatch(context.SemanticKey ?? string.Empty) ||
            !ContractPatterns.Sha256().IsMatch(context.NeighborhoodDigest ?? string.Empty) ||
            context.SheetRole is not ("Model" or "Sheet") ||
            context.SheetRole != (segment.Entity.Space == "PaperSpace" ? "Sheet" : "Model") ||
            context.Discipline is not ("Unknown" or "Architectural" or "Mechanical" or "Electrical" or "Plumbing" or "Fire" or "Controls") ||
            context.DisciplineEvidence is null || context.DisciplineEvidence.Count > 8 ||
            context.DisciplineEvidence.Any(value => !ValidDisciplineEvidence(value)) ||
            context.DisciplineEvidence.Distinct(StringComparer.Ordinal).Count() != context.DisciplineEvidence.Count ||
            !context.DisciplineEvidence.SequenceEqual(context.DisciplineEvidence.Order(StringComparer.Ordinal), StringComparer.Ordinal) ||
            (context.DisciplineConflict && context.Discipline != "Unknown") ||
            (context.Version == PolicyVersionOneZero && context.DisciplineResolution is not null) ||
            (context.Version == PolicyVersionOneOne && context.DisciplineResolution is not (null or LocalEvidenceOverridesDrawingHint)) ||
            (context.Version == PolicyVersionOneTwo && context.DisciplineResolution is not
                (null or LocalEvidenceOverridesDrawingHint or DrawingNameArchitecturalFallback)) ||
            context.AnchorSource is not ("ExtentsCenter" or "EntityPosition") ||
            context.XBand is < 0 or > 15 || context.YBand is < 0 or > 15 || context.ReadingOrder < 0 ||
            context.Signals is null || context.Signals.Count > 8 || context.Signals.Any(signal => signal is not
                ("CEILING_CONTEXT" or "RAISED_FLOOR_CONTEXT" or "ROOF_CONTEXT" or "VERTICAL_LEVEL_CONTEXT_CONFLICT")) ||
            context.Signals.Distinct(StringComparer.Ordinal).Count() != context.Signals.Count ||
            !context.Signals.SequenceEqual(context.Signals.Order(StringComparer.Ordinal), StringComparer.Ordinal) ||
            context.Neighbors is null || context.Neighbors.Count > MaximumNeighbors ||
            context.Neighbors.Select(neighbor => neighbor.SegmentId).Distinct(StringComparer.Ordinal).Count() != context.Neighbors.Count)
            return false;

        foreach (var neighbor in context.Neighbors)
        {
            if (neighbor is null || neighbor.SegmentId == segment.SegmentId ||
                !segments.TryGetValue(neighbor.SegmentId, out var target) || target.SemanticContext is null ||
                !string.Equals(neighbor.SourceTextHash, target.SourceTextHash, StringComparison.Ordinal) ||
                !string.Equals(neighbor.EntityType, target.Entity.Type, StringComparison.Ordinal) ||
                neighbor.SameLayer != string.Equals(segment.Entity.Layer, target.Entity.Layer, StringComparison.Ordinal) ||
                neighbor.Relation is not ("SamePosition" or "Above" or "Below" or "Left" or "Right") ||
                neighbor.DistanceBand is < 0 or > 3 ||
                !string.Equals(segment.Entity.Space, target.Entity.Space, StringComparison.Ordinal) ||
                !string.Equals(segment.Entity.Layout, target.Entity.Layout, StringComparison.Ordinal))
                return false;
        }
        var entityDiscipline = ResolveDisciplineV1_0(segment.Entity.Layer, segment.Entity.Layout, segment.Entity.BlockPath);
        var expectedEvidence = entityDiscipline.Evidence
            .Concat(context.DisciplineEvidence.Where(value => value == "DRAWING_NAME_CONTROLS"))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var expectedDiscipline = context.Version switch
        {
            PolicyVersionOneZero => DisciplineFromEvidence(expectedEvidence),
            PolicyVersionOneOne => ResolveDisciplineV1_1(entityDiscipline,
                expectedEvidence.Contains("DRAWING_NAME_CONTROLS", StringComparer.Ordinal)),
            PolicyVersionOneTwo => ResolveDisciplineV1_2ForValidation(entityDiscipline,
                expectedEvidence.Contains("DRAWING_NAME_CONTROLS", StringComparer.Ordinal), context),
            _ => new DisciplineResult("Unknown", false, [])
        };
        var expectedSignals = ResolveSignals(segment.Entity.Layer, segment.Entity.Layout, segment.Entity.BlockPath,
            context.Neighbors.Select(neighbor => segments[neighbor.SegmentId].SourceText));
        if (!string.Equals(context.Discipline, expectedDiscipline.Name, StringComparison.Ordinal) ||
            context.DisciplineConflict != expectedDiscipline.Conflict ||
            !string.Equals(context.DisciplineResolution, expectedDiscipline.Resolution, StringComparison.Ordinal) ||
            !context.DisciplineEvidence.SequenceEqual(expectedDiscipline.Evidence, StringComparer.Ordinal) ||
            !context.Signals.SequenceEqual(expectedSignals, StringComparer.Ordinal))
            return false;
        var expectedNeighborhood = Hash(NeighborhoodHashDomain(context.Version), context.Neighbors.SelectMany(neighbor => new[]
        {
            neighbor.Relation,
            neighbor.DistanceBand.ToString(CultureInfo.InvariantCulture),
            neighbor.SameLayer ? "same-layer" : "other-layer",
            neighbor.EntityType,
            Normalize(segments[neighbor.SegmentId].SourceText)
        }));
        var expectedSemanticKey = Hash(SemanticKeyHashDomain(context.Version), SemanticKeyValues(
            context.Version, context.SheetRole, expectedDiscipline, context.Signals, expectedNeighborhood));
        var expectedContextHash = Hash(ContextHashDomain(context.Version), new[]
        {
            context.Version,
            segment.SegmentId,
            segment.Entity.Type,
            segment.Entity.Handle,
            segment.Entity.Space,
            segment.Entity.Layout ?? string.Empty,
            segment.Entity.Layer,
            context.AnchorSource,
            context.XBand.ToString(CultureInfo.InvariantCulture),
            context.YBand.ToString(CultureInfo.InvariantCulture),
            context.ReadingOrder.ToString(CultureInfo.InvariantCulture),
            expectedSemanticKey,
            expectedNeighborhood,
            string.Join("\u001f", context.Neighbors.Select(neighbor =>
                $"{neighbor.SegmentId}|{neighbor.SourceTextHash}|{neighbor.EntityType}|{neighbor.Relation}|{neighbor.DistanceBand}|{neighbor.SameLayer}"))
        });
        return string.Equals(context.NeighborhoodDigest, expectedNeighborhood, StringComparison.Ordinal) &&
               string.Equals(context.SemanticKey, expectedSemanticKey, StringComparison.Ordinal) &&
               string.Equals(context.ContextHash, expectedContextHash, StringComparison.Ordinal);
    }

    private static bool Valid(CadSemanticContextInput input) =>
        input is not null && ContractPatterns.SegmentId().IsMatch(input.SegmentId ?? string.Empty) &&
        input.EntityType is "TEXT" or "MTEXT" && input.Handle is { Length: >= 1 and <= 32 } &&
        input.Handle.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F') &&
        input.Space is "ModelSpace" or "PaperSpace" &&
        ((input.Space == "ModelSpace" && input.Layout is null) || (input.Space == "PaperSpace" && input.Layout is { Length: >= 1 and <= 255 })) &&
        input.BlockPath is not null && input.BlockPath.Count == 0 && input.Layer is { Length: >= 1 and <= 255 } &&
        input.SourceText is { Length: <= 65_535 } &&
        string.Equals(input.SourceTextHash, SourceTextHash(input.SourceText), StringComparison.Ordinal) &&
        double.IsFinite(input.AnchorX) && double.IsFinite(input.AnchorY) && double.IsFinite(input.TextHeight) && input.TextHeight > 0 &&
        input.AnchorSource is "ExtentsCenter" or "EntityPosition" &&
        (input.DrawingHint is null || input.DrawingHint is { Length: >= 1 and <= 255 } &&
            string.Equals(Path.GetFileName(input.DrawingHint), input.DrawingHint, StringComparison.Ordinal));

    private static int CoordinateBand(double value, double minimum, double maximum)
    {
        if (maximum <= minimum) return 8;
        return Math.Min(15, (int)Math.Floor(Math.Clamp((value - minimum) / (maximum - minimum), 0d, 1d) * 16d));
    }

    private static string Relation(CadSemanticContextInput origin, CadSemanticContextInput neighbor, double distance)
    {
        if (distance <= 1e-12) return "SamePosition";
        var deltaX = neighbor.AnchorX - origin.AnchorX;
        var deltaY = neighbor.AnchorY - origin.AnchorY;
        return Math.Abs(deltaY) >= Math.Abs(deltaX)
            ? deltaY > 0 ? "Above" : "Below"
            : deltaX > 0 ? "Right" : "Left";
    }

    private static int DistanceBand(double distance) => distance switch
    {
        <= 2d => 0,
        <= 8d => 1,
        <= 20d => 2,
        _ => 3
    };

    private static DisciplineResult ResolveDiscipline(CadSemanticContextInput input, string policyVersion)
    {
        if (policyVersion == PolicyVersionOneZero)
            return ResolveDisciplineV1_0(input.Layer, input.Layout, input.BlockPath, input.DrawingHint);

        var local = ResolveDisciplineV1_0(input.Layer, input.Layout, input.BlockPath);
        if (policyVersion == PolicyVersionOneOne)
            return ResolveDisciplineV1_1(local, HasDrawingControlsHint(input.DrawingHint));
        return ResolveDisciplineV1_2(local, HasDrawingControlsHint(input.DrawingHint),
            HasAsciiDrawingToken(input.DrawingHint, "ARQ"));
    }

    private static DisciplineResult ResolveDisciplineV1_0(string layer, string? layout, IReadOnlyList<string> blockPath,
        string? drawingHint = null)
    {
        var values = new List<(string Origin, string Value)> { ("LAYER", layer) };
        if (layout is not null) values.Add(("LAYOUT", layout));
        values.AddRange(blockPath.Select(value => ("BLOCK_PATH", value)));
        if (drawingHint is not null) values.Add(("DRAWING_NAME", drawingHint));
        var matches = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (origin, raw) in values)
        {
            var value = Normalize(raw);
            if (origin == "DRAWING_NAME")
            {
                if (HasAny(value, "CTRL", "CONTROL", "CONTROLS", "AUTOMATION") ||
                    ContainsDrawingPrefix(value, "BMS") || ContainsDrawingPrefix(value, "BAS"))
                {
                    if (!matches.TryGetValue("Controls", out var drawingEvidence)) matches["Controls"] = drawingEvidence = [];
                    drawingEvidence.Add("DRAWING_NAME_CONTROLS");
                }
                continue;
            }
            AddEvidence(matches, "Architectural", origin, value, "ARCH", "ARCHITECTURAL", "ARQ", "ARQUITECTURA", "ARCHITECTURE");
            AddEvidence(matches, "Mechanical", origin, value, "MECH", "MECHANICAL", "MEC", "HVAC", "DUCT", "DUCTO");
            AddEvidence(matches, "Electrical", origin, value, "ELEC", "ELECTR", "ELECTRICAL", "ELECTRICO", "ELÉCTRICO");
            AddEvidence(matches, "Plumbing", origin, value, "PLUMB", "PLUMBING", "PLOM", "SANIT", "PIPING", "TUBER");
            AddEvidence(matches, "Fire", origin, value, "FIRE", "INCENDIO", "SPRINK");
            AddEvidence(matches, "Controls", origin, value, "CTRL", "CONTROL", "CONTROLS", "BMS", "BAS", "AUTOMATION");
        }
        var found = DisciplineOrder.Where(matches.ContainsKey).ToArray();
        var evidence = found.SelectMany(name => matches[name]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return found.Length == 1 ? new(found[0], false, evidence) : new("Unknown", found.Length > 1, evidence);
    }

    private static DisciplineResult ResolveDisciplineV1_1(DisciplineResult local, bool drawingControls)
    {
        var evidence = local.Evidence.Concat(drawingControls ? ["DRAWING_NAME_CONTROLS"] : Array.Empty<string>())
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (local.Conflict)
            return new("Unknown", true, evidence);
        if (local.Name != "Unknown")
            return new(local.Name, false, evidence,
                drawingControls && local.Name != "Controls" ? LocalEvidenceOverridesDrawingHint : null);
        return drawingControls ? new("Controls", false, evidence) : new("Unknown", false, evidence);
    }

    private static DisciplineResult ResolveDisciplineV1_2(
        DisciplineResult local,
        bool drawingControls,
        bool drawingArchitectural)
    {
        var evidence = local.Evidence.Concat(drawingControls ? ["DRAWING_NAME_CONTROLS"] : Array.Empty<string>())
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (local.Conflict)
            return new("Unknown", true, evidence);
        if (local.Name != "Unknown")
            return new(local.Name, false, evidence,
                drawingControls && local.Name != "Controls" ? LocalEvidenceOverridesDrawingHint : null);
        if (drawingArchitectural)
            return new("Architectural", false, [], DrawingNameArchitecturalFallback);
        return drawingControls ? new("Controls", false, evidence) : new("Unknown", false, evidence);
    }

    private static DisciplineResult ResolveDisciplineV1_2ForValidation(
        DisciplineResult local,
        bool drawingControls,
        CadSemanticContext context)
    {
        var drawingArchitectural = string.Equals(
            context.DisciplineResolution, DrawingNameArchitecturalFallback, StringComparison.Ordinal);
        if (drawingArchitectural && (context.DisciplineEvidence.Count != 0 || local.Name != "Unknown" || local.Conflict))
            return new("Unknown", false, []);
        return ResolveDisciplineV1_2(local, drawingControls, drawingArchitectural);
    }

    private static bool HasDrawingControlsHint(string? drawingHint)
    {
        if (drawingHint is null) return false;
        var value = Normalize(drawingHint);
        return HasAny(value, "CTRL", "CONTROL", "CONTROLS", "AUTOMATION") ||
               ContainsDrawingPrefix(value, "BMS") || ContainsDrawingPrefix(value, "BAS");
    }

    internal static bool IsArchitecturalManifestBasename(string? manifestBoundBasename) =>
        !string.IsNullOrWhiteSpace(manifestBoundBasename) && manifestBoundBasename.Length <= 255 &&
        string.Equals(Path.GetFileName(manifestBoundBasename), manifestBoundBasename, StringComparison.Ordinal) &&
        HasAsciiDrawingToken(manifestBoundBasename, "ARQ");

    private static bool HasAsciiDrawingToken(string? drawingHint, string token)
    {
        if (drawingHint is null || token.Length == 0) return false;
        for (var start = 0; start <= drawingHint.Length - token.Length; start++)
        {
            if (!drawingHint.AsSpan(start, token.Length).Equals(token, StringComparison.OrdinalIgnoreCase)) continue;
            var before = start == 0 || !IsAsciiAlphaNumeric(drawingHint[start - 1]);
            var end = start + token.Length;
            var after = end == drawingHint.Length || !IsAsciiAlphaNumeric(drawingHint[end]);
            if (before && after) return true;
        }
        return false;
    }

    private static bool IsAsciiAlphaNumeric(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';

    private static DisciplineResult DisciplineFromEvidence(IReadOnlyList<string> evidence)
    {
        var found = DisciplineOrder.Where(discipline => evidence.Any(value =>
            value.EndsWith('_' + discipline.ToUpperInvariant(), StringComparison.Ordinal))).ToArray();
        return found.Length == 1 ? new(found[0], false, evidence) : new("Unknown", found.Length > 1, evidence);
    }

    private static bool ValidDisciplineEvidence(string value) => value is { Length: <= 80 } &&
        (value == "DRAWING_NAME_CONTROLS" ||
         (value.StartsWith("LAYER_", StringComparison.Ordinal) || value.StartsWith("LAYOUT_", StringComparison.Ordinal) ||
          value.StartsWith("BLOCK_PATH_", StringComparison.Ordinal)) &&
         DisciplineOrder.Any(discipline => value.EndsWith('_' + discipline.ToUpperInvariant(), StringComparison.Ordinal)));

    private static void AddEvidence(Dictionary<string, List<string>> matches, string discipline, string origin,
        string value, params string[] terms)
    {
        if (!terms.Any(term => ContainsToken(value, term))) return;
        if (!matches.TryGetValue(discipline, out var evidence)) matches[discipline] = evidence = [];
        evidence.Add($"{origin}_{discipline.ToUpperInvariant()}");
    }

    private static string[] ResolveSignals(CadSemanticContextInput input, IEnumerable<CadSemanticContextInput> neighbors) =>
        ResolveSignals(input.Layer, input.Layout, input.BlockPath, neighbors.Select(neighbor => neighbor.SourceText));

    private static string[] ResolveSignals(string layer, string? layout, IReadOnlyList<string> blockPath,
        IEnumerable<string> neighborTexts)
    {
        var context = new[] { layer, layout }.Concat(blockPath)
            .Concat(neighborTexts)
            .Where(value => !string.IsNullOrEmpty(value)).Select(value => Normalize(value!)).ToArray();
        var signals = new List<string>();
        if (context.Any(value => HasAny(value, "PISO FALSO", "PISO TECNICO", "PISO TÉCNICO", "RAISED", "ACCESS FLOOR", "RAFL")))
            signals.Add("RAISED_FLOOR_CONTEXT");
        if (context.Any(value => HasAny(value, "CUBIERTA", "ROOF", "AZOTEA", "FRL"))) signals.Add("ROOF_CONTEXT");
        if (context.Any(value => HasAny(value, "TECHO", "CEILING", "CIELO", "PLAFON", "PLAFÓN", "FCL"))) signals.Add("CEILING_CONTEXT");
        if (signals.Contains("ROOF_CONTEXT", StringComparer.Ordinal) && signals.Contains("CEILING_CONTEXT", StringComparer.Ordinal))
            signals.Add("VERTICAL_LEVEL_CONTEXT_CONFLICT");
        return signals.Order(StringComparer.Ordinal).ToArray();
    }

    private static bool HasAny(string value, params string[] terms) => terms.Any(term => ContainsToken(value, term));

    private static bool ContainsDrawingPrefix(string value, string term)
    {
        for (var start = 0; start <= value.Length - term.Length; start++)
        {
            if (!value.AsSpan(start, term.Length).Equals(term, StringComparison.Ordinal)) continue;
            var before = start == 0 || !char.IsLetterOrDigit(value[start - 1]);
            var end = start + term.Length;
            var after = end == value.Length || !char.IsLetter(value[end]);
            if (before && after) return true;
        }
        return false;
    }

    private static bool ContainsToken(string value, string term)
    {
        for (var start = 0; start <= value.Length - term.Length; start++)
        {
            if (!value.AsSpan(start, term.Length).Equals(term, StringComparison.Ordinal)) continue;
            var before = start == 0 || !char.IsLetterOrDigit(value[start - 1]);
            var end = start + term.Length;
            var after = end == value.Length || !char.IsLetterOrDigit(value[end]);
            if (before && after) return true;
        }
        return false;
    }

    private static string Normalize(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormC).ToUpperInvariant();
        var result = new StringBuilder(normalized.Length);
        var whitespace = false;
        foreach (var character in normalized)
        {
            if (char.IsWhiteSpace(character)) { whitespace = result.Length > 0; continue; }
            if (whitespace) result.Append(' ');
            whitespace = false;
            result.Append(character);
        }
        return result.ToString().Trim();
    }

    private static string SourceTextHash(string value) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string NeighborhoodHashDomain(string policyVersion) => policyVersion switch
    {
        PolicyVersionOneZero => "dwg-translator/neighborhood/v1",
        PolicyVersionOneOne => "dwg-translator/neighborhood/v1.1",
        PolicyVersionOneTwo => "dwg-translator/neighborhood/v1.2",
        _ => throw new ArgumentOutOfRangeException(nameof(policyVersion))
    };

    private static string SemanticKeyHashDomain(string policyVersion) => policyVersion switch
    {
        PolicyVersionOneZero => "dwg-translator/semantic-key/v1",
        PolicyVersionOneOne => "dwg-translator/semantic-key/v1.1",
        PolicyVersionOneTwo => "dwg-translator/semantic-key/v1.2",
        _ => throw new ArgumentOutOfRangeException(nameof(policyVersion))
    };

    private static string ContextHashDomain(string policyVersion) => policyVersion switch
    {
        PolicyVersionOneZero => "dwg-translator/semantic-context/v1",
        PolicyVersionOneOne => "dwg-translator/semantic-context/v1.1",
        PolicyVersionOneTwo => "dwg-translator/semantic-context/v1.2",
        _ => throw new ArgumentOutOfRangeException(nameof(policyVersion))
    };

    private static string AggregateHashDomain(string policyVersion) => policyVersion switch
    {
        PolicyVersionOneZero => "dwg-translator/semantic-context-set/v1",
        PolicyVersionOneOne => "dwg-translator/semantic-context-set/v1.1",
        PolicyVersionOneTwo => "dwg-translator/semantic-context-set/v1.2",
        _ => throw new ArgumentOutOfRangeException(nameof(policyVersion))
    };

    private static IEnumerable<string> SemanticKeyValues(string policyVersion, string sheetRole,
        DisciplineResult discipline, IEnumerable<string> signals, string neighborhoodDigest)
    {
        yield return policyVersion;
        yield return sheetRole;
        yield return discipline.Name;
        yield return discipline.Conflict ? "conflict" : "clear";
        yield return string.Join("\u001f", discipline.Evidence);
        if (policyVersion is PolicyVersionOneOne or PolicyVersionOneTwo)
            yield return discipline.Resolution ?? string.Empty;
        yield return string.Join("\u001f", signals);
        yield return neighborhoodDigest;
    }

    private static string Hash(string domain, IEnumerable<string> values)
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

    private sealed record Scope(string Space, string? Layout);
    private sealed class ScopeComparer : IEqualityComparer<Scope>
    {
        public static ScopeComparer Instance { get; } = new();
        public bool Equals(Scope? left, Scope? right) => left is not null && right is not null &&
            string.Equals(left.Space, right.Space, StringComparison.Ordinal) && string.Equals(left.Layout, right.Layout, StringComparison.Ordinal);
        public int GetHashCode(Scope value) => HashCode.Combine(StringComparer.Ordinal.GetHashCode(value.Space),
            value.Layout is null ? 0 : StringComparer.Ordinal.GetHashCode(value.Layout));
    }

    private sealed record NeighborCandidate(CadSemanticContextInput Input, double NormalizedDistance)
    {
        public static NeighborCandidate Create(CadSemanticContextInput origin, CadSemanticContextInput candidate)
        {
            var deltaX = candidate.AnchorX - origin.AnchorX;
            var deltaY = candidate.AnchorY - origin.AnchorY;
            var scale = Math.Max(origin.TextHeight, candidate.TextHeight);
            return new(candidate, Math.Sqrt(deltaX * deltaX + deltaY * deltaY) / scale);
        }
    }

    private sealed record DisciplineResult(string Name, bool Conflict, IReadOnlyList<string> Evidence,
        string? Resolution = null);

    private sealed class CadHandleComparer : IComparer<string>
    {
        public static CadHandleComparer Instance { get; } = new();
        public int Compare(string? left, string? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            var leftTrimmed = left.TrimStart('0');
            var rightTrimmed = right.TrimStart('0');
            if (leftTrimmed.Length != rightTrimmed.Length) return leftTrimmed.Length.CompareTo(rightTrimmed.Length);
            var ordinal = string.Compare(leftTrimmed, rightTrimmed, StringComparison.Ordinal);
            return ordinal != 0 ? ordinal : string.Compare(left, right, StringComparison.Ordinal);
        }
    }

    private static Result<T> Failure<T>(string code, string message) =>
        Results.Failure<T>(new ContractError(code, ErrorCategory.Contract, message, false));
}
