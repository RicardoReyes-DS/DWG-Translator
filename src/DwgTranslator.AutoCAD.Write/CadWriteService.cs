using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using DwgTranslator.Application;
using DwgTranslator.Contracts;

namespace DwgTranslator.AutoCAD.Write;

internal static class CadWriteService
{
    public static Result<CadReconcileResponsePayload> Reconcile(WireEnvelope request, CancellationToken cancellationToken)
    {
        var decoded = EnvelopeCodec.DecodePayload<CadReconcileRequestPayload>(request, MessageTypes.ReconcileRequest);
        if (!decoded.IsSuccess) return Results.Failure<CadReconcileResponsePayload>(decoded.Error!);
        var payload = decoded.Value!;
        if (!string.Equals(payload.ValidationPolicy, "VisualStrictV2", StringComparison.Ordinal) || payload.Mappings.Count == 0)
            return ReconcileFailure("RECONCILIATION_INPUT_INVALID", ErrorCategory.Input, "The orphan validation request is incomplete.");
        if (!File.Exists(payload.SourcePath) || !File.Exists(payload.OrphanPath) ||
            ContainsReparsePoint(payload.SourcePath) || ContainsReparsePoint(payload.OrphanPath))
            return ReconcileFailure("RECONCILIATION_PATH_INVALID", ErrorCategory.Security, "The source or orphan path is unsafe.");
        if (!string.Equals(FileHash(payload.SourcePath), payload.ExpectedSourceHash, StringComparison.Ordinal) ||
            !string.Equals(FileHash(payload.OrphanPath), payload.ExpectedOrphanHash, StringComparison.Ordinal))
            return ReconcileFailure("RECONCILIATION_HASH_MISMATCH", ErrorCategory.Integrity, "A reconciliation input hash differs.");

        var targets = payload.Mappings.Select(mapping => mapping.Handle).ToHashSet(StringComparer.Ordinal);
        var beforeVisual = new Dictionary<string, VisualSnapshot>(StringComparer.Ordinal);
        InvariantSnapshot beforeInvariant;
        using (var source = OpenReadOnly(payload.SourcePath))
        {
            beforeInvariant = InvariantFingerprint(source, targets);
            using var transaction = source.TransactionManager.StartOpenCloseTransaction();
            foreach (var mapping in payload.Mappings)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entity = ResolveEntity(source, transaction, mapping.Handle, OpenMode.ForRead);
                var sourceText = EntityText(entity);
                if (!string.Equals(TextHash(sourceText), mapping.ExpectedSourceTextHash, StringComparison.Ordinal))
                    return ReconcileFailure("SOURCE_TEXT_CHANGED", ErrorCategory.Integrity, "A source entity no longer matches the approved checkpoint.");
                var visual = VisualSnapshotFor(source, transaction, entity);
                EnsureExpectedVisualIdentity(mapping, visual.Properties);
                beforeVisual.Add(mapping.SegmentId, visual);
            }
        }

        var applied = new List<CadAppliedMapping>(payload.Mappings.Count);
        InvariantSnapshot afterInvariant;
        using (var orphan = OpenReadOnly(payload.OrphanPath))
        {
            afterInvariant = InvariantFingerprint(orphan, targets);
            using var transaction = orphan.TransactionManager.StartOpenCloseTransaction();
            foreach (var mapping in payload.Mappings)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entity = ResolveEntity(orphan, transaction, mapping.Handle, OpenMode.ForRead);
                var finalText = EntityText(entity);
                var postHash = TextHash(finalText);
                if (!string.Equals(postHash, mapping.ApprovedFinalTextHash, StringComparison.Ordinal))
                    return ReconcileFailure("POST_WRITE_TEXT_MISMATCH", ErrorCategory.Integrity, "An orphan entity differs from the approved final hash.");
                var expectedTokens = CadProtectedTokenPolicy.Extract(mapping.ApprovedFinalText).Select(token => token.Token);
                var actualTokens = CadProtectedTokenPolicy.Extract(finalText).Select(token => token.Token);
                if (!expectedTokens.SequenceEqual(actualTokens, StringComparer.Ordinal))
                    return ReconcileFailure("FORMAT_TOKEN_INTEGRITY_FAILED", ErrorCategory.Integrity, "Protected tokens differ in the orphan.");
                var before = beforeVisual[mapping.SegmentId];
                var after = VisualSnapshotFor(orphan, transaction, entity);
                EnsureExpectedVisualIdentity(mapping, after.Properties);
                if (!string.Equals(before.Fingerprint, after.Fingerprint, StringComparison.Ordinal))
                    return ReconcileFailure("VISUAL_INVARIANTS_CHANGED", ErrorCategory.Integrity, "VisualStrictV2 rejected an orphan entity.");
                applied.Add(new CadAppliedMapping
                {
                    SegmentId = mapping.SegmentId,
                    Result = "ReconciledReadOnly",
                    PostWriteTextHash = postHash,
                    VisualEvidence = new CadVisualInvariantEvidence
                    {
                        BeforeProperties = before.Properties, AfterProperties = after.Properties,
                        BeforeFingerprint = before.Fingerprint, AfterFingerprint = after.Fingerprint,
                        InvariantMatch = true, BeforeExtents = before.Extents, AfterExtents = after.Extents,
                        BoundsChanged = !string.Equals(before.Extents, after.Extents, StringComparison.Ordinal),
                        VisualReviewRequired = true
                    }
                });
            }
        }
        if (!InvariantSnapshotsEquivalent(beforeInvariant.Rows, afterInvariant.Rows))
        {
            var difference = DescribeInvariantDifference(beforeInvariant.Rows, afterInvariant.Rows);
            return ReconcileFailure("GEOMETRY_INVARIANTS_CHANGED", ErrorCategory.Integrity,
                "VisualStrictV2 rejected the orphan drawing invariants.", difference.Summary, difference.Diagnostics);
        }
        if (!string.Equals(FileHash(payload.SourcePath), payload.ExpectedSourceHash, StringComparison.Ordinal) ||
            !string.Equals(FileHash(payload.OrphanPath), payload.ExpectedOrphanHash, StringComparison.Ordinal))
            return ReconcileFailure("RECONCILIATION_HASH_CHANGED", ErrorCategory.Integrity, "A reconciliation input changed while open.");
        return Results.Success(new CadReconcileResponsePayload
        {
            SourceHashAfter = payload.ExpectedSourceHash,
            OrphanHash = payload.ExpectedOrphanHash,
            Applied = applied,
            Validation = new CadValidationResult
            {
                ReopenedByAutoCAD = true, EntityMappingValid = true, GeometryInvariantsValid = true,
                FormatTokenIntegrityValid = true, VisualInvariantsValid = true, Policy = "VisualStrictV2"
            }
        });
    }

    public static Result<CadWriteResponsePayload> Execute(
        WireEnvelope request,
        CancellationToken cancellationToken,
        Action<string>? stageObserver = null)
    {
        string? currentStage = null;
        void Stage(string value)
        {
            currentStage = value;
            stageObserver?.Invoke(value);
        }

        Stage(CadWriteTechnicalStages.Preflight);
        var safety = WriteSafetyPolicy.Validate(request);
        if (!safety.IsSuccess) return Results.Failure<CadWriteResponsePayload>(safety.Error!);
        var decoded = EnvelopeCodec.DecodePayload<CadWriteRequestPayload>(request, MessageTypes.WriteRequest);
        if (!decoded.IsSuccess) return Results.Failure<CadWriteResponsePayload>(decoded.Error!);
        var payload = decoded.Value!;
        var preflight = Preflight(payload);
        if (!preflight.IsSuccess) return Results.Failure<CadWriteResponsePayload>(preflight.Error!);

        cancellationToken.ThrowIfCancellationRequested();
        var targetHandles = payload.Mappings.Select(mapping => mapping.Handle).ToHashSet(StringComparer.Ordinal);
        var normalizedBaselinePath = CreateNormalizedBaselinePath(Path.GetDirectoryName(payload.FinalPath)!);
        var normalizedBaselineOwned = false;
        var applied = new List<CadAppliedMapping>(payload.Mappings.Count);
        string candidateHash;
        try
        {
            // Both sides of the invariant comparison must traverse the same
            // AutoCAD SaveAs/reopen lifecycle. The source is copied, never saved.
            Stage(CadWriteTechnicalStages.NormalizedBaselineCopy);
            CopySourceToCandidate(
                payload.SourcePath,
                normalizedBaselinePath,
                payload.ExpectedSourceHash,
                () => normalizedBaselineOwned = true);
            Dictionary<string, VisualSnapshot> normalizedBaselineVisual;
            Stage(CadWriteTechnicalStages.NormalizedBaselineOpen);
            using (var baseline = OpenCandidate(normalizedBaselinePath))
            {
                Stage(CadWriteTechnicalStages.NormalizedBaselinePrime);
                normalizedBaselineVisual = PrimeNormalizedBaselineMappings(
                    baseline, payload.Mappings, cancellationToken);
                Stage(CadWriteTechnicalStages.NormalizedBaselineSave);
                baseline.SaveAs(normalizedBaselinePath, DwgVersion.Current);
            }

            var normalizedBaselineHash = FileHash(normalizedBaselinePath);
            Stage(CadWriteTechnicalStages.NormalizedBaselineStabilize);
            var normalizedBaselineCapture = CaptureStableInvariant(
                normalizedBaselinePath,
                normalizedBaselineHash,
                targetHandles,
                payload.Mappings,
                normalizedBaselineVisual,
                InvariantArtifactRole.NormalizedBaseline,
                cancellationToken);
            if (!normalizedBaselineCapture.IsSuccess)
                return Results.Failure<CadWriteResponsePayload>(normalizedBaselineCapture.Error!);
            var normalizedBaselineInvariant = normalizedBaselineCapture.Value!.Invariant;

            Stage(CadWriteTechnicalStages.SourceRevalidate);
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(FileHash(payload.SourcePath), payload.ExpectedSourceHash, StringComparison.Ordinal))
                return Failure("SOURCE_CHANGED", ErrorCategory.Integrity, "The source hash changed while preparing the normalized baseline.");

            Stage(CadWriteTechnicalStages.CandidateCopy);
            CopySourceToCandidate(payload.SourcePath, payload.CandidatePath, payload.ExpectedSourceHash);
            Dictionary<string, VisualSnapshot> beforeVisual;
            Stage(CadWriteTechnicalStages.CandidateOpen);
            using (var database = OpenCandidate(payload.CandidatePath))
            {
                Stage(CadWriteTechnicalStages.CandidateApply);
                beforeVisual = ApplyMappings(database, payload.Mappings, cancellationToken);
                Stage(CadWriteTechnicalStages.CandidateSave);
                database.SaveAs(payload.CandidatePath, DwgVersion.Current);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var candidateArtifactHash = FileHash(payload.CandidatePath);
            Stage(CadWriteTechnicalStages.CandidateStabilize);
            var candidateCapture = CaptureStableInvariant(
                payload.CandidatePath,
                candidateArtifactHash,
                targetHandles,
                payload.Mappings,
                beforeVisual,
                InvariantArtifactRole.Candidate,
                cancellationToken);
            if (!candidateCapture.IsSuccess)
                return Results.Failure<CadWriteResponsePayload>(candidateCapture.Error!);
            var candidateInvariant = candidateCapture.Value!.Invariant;
            Stage(CadWriteTechnicalStages.InvariantCompare);
            if (!InvariantSnapshotsEquivalent(normalizedBaselineInvariant.Rows, candidateInvariant.Rows))
            {
                var difference = DescribeInvariantDifference(normalizedBaselineInvariant.Rows, candidateInvariant.Rows);
                return Results.Failure<CadWriteResponsePayload>(new ContractError(
                    "GEOMETRY_INVARIANTS_CHANGED",
                    ErrorCategory.Integrity,
                    "Non-text geometry, placement, layers or styles changed in the candidate.",
                    false,
                    "Inspect the preserved candidate before retrying.",
                    difference.Summary,
                    difference.Diagnostics));
            }
            AppendAppliedMappings(
                payload.Mappings,
                beforeVisual,
                candidateCapture.Value.ObservedMappings,
                applied);

            candidateHash = FileHash(payload.CandidatePath);
            var sourceHashAfter = FileHash(payload.SourcePath);
            if (!string.Equals(sourceHashAfter, payload.ExpectedSourceHash, StringComparison.Ordinal))
                return Failure("SOURCE_CHANGED", ErrorCategory.Integrity, "The source hash changed before candidate promotion.");
        }
        finally
        {
            if (normalizedBaselineOwned)
            {
                var interruptedStage = currentStage;
                Stage(CadWriteTechnicalStages.NormalizedBaselineCleanup);
                DeleteNormalizedBaseline(normalizedBaselinePath);
                if (interruptedStage is not null) Stage(interruptedStage);
            }
        }

        // Promotion is deliberately outside the baseline scope: a cleanup
        // failure can never coexist with a promoted final output.
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(payload.FinalPath)) return Failure("OUTPUT_EXISTS", ErrorCategory.Concurrency, "The destination appeared before promotion.");
        Stage(CadWriteTechnicalStages.CandidatePromote);
        File.Move(payload.CandidatePath, payload.FinalPath, overwrite: false);
        Stage(CadWriteTechnicalStages.PromotionVerify);
        if (!string.Equals(FileHash(payload.FinalPath), candidateHash, StringComparison.Ordinal))
            return Failure("PROMOTION_HASH_MISMATCH", ErrorCategory.Integrity, "The promoted output differs from the validated candidate.");
        if (!string.Equals(FileHash(payload.SourcePath), payload.ExpectedSourceHash, StringComparison.Ordinal))
            return Failure("SOURCE_CHANGED", ErrorCategory.Integrity, "The source hash changed during promotion.");

        return Results.Success(new CadWriteResponsePayload
        {
            CandidateHash = candidateHash,
            CandidateBytes = new FileInfo(payload.FinalPath).Length,
            SourceHashAfter = payload.ExpectedSourceHash,
            Applied = applied,
            Validation = new CadValidationResult
            {
                ReopenedByAutoCAD = true,
                EntityMappingValid = true,
                GeometryInvariantsValid = true,
                FormatTokenIntegrityValid = true,
                VisualInvariantsValid = true,
                Policy = "VisualStrictV2"
            },
            Promotion = new CadPromotionResult { Performed = true, FinalPath = payload.FinalPath }
        });
    }

    private static Result<bool> Preflight(CadWriteRequestPayload payload)
    {
        if (!File.Exists(payload.SourcePath)) return Failure<bool>("SOURCE_NOT_FOUND", ErrorCategory.Input, "The selected source does not exist.");
        if (File.Exists(payload.CandidatePath) || Directory.Exists(payload.CandidatePath)) return Failure<bool>("CANDIDATE_EXISTS", ErrorCategory.Concurrency, "The candidate path already exists.");
        if (File.Exists(payload.FinalPath) || Directory.Exists(payload.FinalPath)) return Failure<bool>("OUTPUT_EXISTS", ErrorCategory.Concurrency, "The destination already exists.");
        var outputDirectory = Path.GetDirectoryName(payload.FinalPath);
        if (string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory)) return Failure<bool>("OUTPUT_DIRECTORY_NOT_FOUND", ErrorCategory.Input, "The output directory does not exist.");
        if (ContainsReparsePoint(payload.SourcePath) || ContainsReparsePoint(outputDirectory)) return Failure<bool>("REPARSE_POINT_REJECTED", ErrorCategory.Security, "Source and output paths cannot traverse reparse points.");
        if (!string.Equals(FileHash(payload.SourcePath), payload.ExpectedSourceHash, StringComparison.Ordinal)) return Failure<bool>("SOURCE_CHANGED", ErrorCategory.Integrity, "The source hash differs before copying.");
        try
        {
            var root = Path.GetPathRoot(payload.FinalPath);
            if (!string.IsNullOrWhiteSpace(root))
            {
                var drive = new DriveInfo(root);
                var required = checked(new FileInfo(payload.SourcePath).Length * 3 + 64L * 1024 * 1024);
                if (drive.AvailableFreeSpace < required) return Failure<bool>("INSUFFICIENT_DISK_SPACE", ErrorCategory.Environment, "Insufficient free space for normalized baseline, candidate and validation.");
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or OverflowException)
        {
            return Failure<bool>("DISK_SPACE_UNKNOWN", ErrorCategory.Environment, "Free space could not be verified for the destination.");
        }
        return Results.Success(true);
    }

    private static void CopySourceToCandidate(string sourcePath, string candidatePath, string expectedHash, Action? destinationCreated = null)
    {
        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        var actual = "sha256:" + Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant();
        if (!string.Equals(actual, expectedHash, StringComparison.Ordinal)) throw new InvalidDataException("SOURCE_CHANGED");
        source.Position = 0;
        using var candidate = new FileStream(candidatePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.WriteThrough);
        destinationCreated?.Invoke();
        source.CopyTo(candidate);
        candidate.Flush(flushToDisk: true);
    }

    private static string CreateNormalizedBaselinePath(string outputDirectory) =>
        Path.Combine(outputDirectory, $".dwgtranslator-normalized-{Guid.NewGuid():N}.dwg");

    private static void DeleteNormalizedBaseline(string path)
    {
        if (!CadNormalizedBaselineCleanupPolicy.TryRemove(
                () => File.Delete(path),
                () => File.Exists(path),
                () => Directory.Exists(path)))
            throw new InvalidDataException("NORMALIZED_BASELINE_CLEANUP_FAILED");
    }

    private static Database OpenCandidate(string path)
    {
        var database = new Database(false, true);
        database.ReadDwgFile(path, FileOpenMode.OpenForReadAndWriteNoShare, true, null);
        database.CloseInput(true);
        return database;
    }

    private static Database OpenReadOnly(string path)
    {
        var database = new Database(false, true);
        database.ReadDwgFile(path, FileOpenMode.OpenForReadAndAllShare, true, null);
        database.CloseInput(true);
        return database;
    }

    private static Dictionary<string, VisualSnapshot> PrimeNormalizedBaselineMappings(
        Database database,
        IReadOnlyList<CadWriteMapping> mappings,
        CancellationToken cancellationToken) =>
        ApplyMappingsCore(database, mappings, static (_, current) => current, cancellationToken);

    private static Dictionary<string, VisualSnapshot> ApplyMappings(
        Database database,
        IReadOnlyList<CadWriteMapping> mappings,
        CancellationToken cancellationToken) =>
        ApplyMappingsCore(database, mappings, static (mapping, _) => mapping.ApprovedFinalText, cancellationToken);

    private static Dictionary<string, VisualSnapshot> ApplyMappingsCore(
        Database database,
        IReadOnlyList<CadWriteMapping> mappings,
        Func<CadWriteMapping, string, string> replacement,
        CancellationToken cancellationToken)
    {
        var snapshots = new Dictionary<string, VisualSnapshot>(StringComparer.Ordinal);
        using var transaction = database.TransactionManager.StartTransaction();
        foreach (var mapping in mappings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entity = ResolveEntity(database, transaction, mapping.Handle, OpenMode.ForWrite);
            var current = EntityText(entity);
            if (!string.Equals(TextHash(current), mapping.ExpectedSourceTextHash, StringComparison.Ordinal)) throw new InvalidDataException("SOURCE_TEXT_CHANGED");
            if (entity.HasFields) throw new InvalidDataException("FIELD_BACKED_EXCLUDED");
            var snapshot = VisualSnapshotFor(database, transaction, entity);
            EnsureExpectedVisualIdentity(mapping, snapshot.Properties);
            snapshots.Add(mapping.SegmentId, snapshot);
            var finalText = replacement(mapping, current);
            switch (entity)
            {
                case DBText text: text.TextString = finalText; break;
                case MText mtext: mtext.Contents = finalText; break;
                default: throw new InvalidDataException("WRITE_ENTITY_TYPE_INVALID");
            }
        }
        transaction.Commit();
        return snapshots;
    }

    private static Result<StableInvariantObservation> CaptureStableInvariant(
        string artifactPath,
        string expectedArtifactHash,
        HashSet<string> targetHandles,
        IReadOnlyList<CadWriteMapping> mappings,
        IReadOnlyDictionary<string, VisualSnapshot> beforeVisual,
        InvariantArtifactRole role,
        CancellationToken cancellationToken)
    {
        InvariantSnapshot? previous = null;
        var consecutiveAuthoritativeSnapshots = 0;
        for (var captureNumber = 1;
             captureNumber <= CadInvariantConvergencePolicy.MaximumCaptures;
             captureNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(FileHash(artifactPath), expectedArtifactHash, StringComparison.Ordinal))
                return StableInvariantFailure(
                    ArtifactChangedCode(role),
                    $"The {ArtifactLabel(role)} bytes changed before a validation reopening.");

            InvariantSnapshot current;
            Dictionary<string, VisualSnapshot> observedMappings;
            using (var reopened = OpenReadOnly(artifactPath))
            {
                current = InvariantFingerprint(reopened, targetHandles);
                observedMappings = ValidateObservedMappings(
                    reopened,
                    mappings,
                    beforeVisual,
                    role,
                    cancellationToken);
            }

            if (!string.Equals(FileHash(artifactPath), expectedArtifactHash, StringComparison.Ordinal))
                return StableInvariantFailure(
                    ArtifactChangedCode(role),
                    $"The {ArtifactLabel(role)} bytes changed during a validation reopening.");

            if (previous is null)
            {
                previous = current;
                continue;
            }

            var decision = CadInvariantConvergencePolicy.Evaluate(
                captureNumber,
                consecutiveAuthoritativeSnapshots,
                previous.Rows,
                current.Rows);
            if (decision.Outcome == CadInvariantObservationOutcome.Stable)
                return Results.Success(new StableInvariantObservation(current, observedMappings));

            if (decision.Outcome is CadInvariantObservationOutcome.Unsafe or CadInvariantObservationOutcome.Exhausted)
            {
                var difference = DescribeInvariantDifference(previous.Rows, current.Rows);
                var unsafeTransition = decision.Outcome == CadInvariantObservationOutcome.Unsafe;
                return Results.Failure<StableInvariantObservation>(new ContractError(
                    unsafeTransition ? UnsafeTransitionCode(role) : UnstableCode(role),
                    ErrorCategory.Integrity,
                    unsafeTransition
                        ? $"The {ArtifactLabel(role)} produced a non-convergent material invariant transition."
                        : $"The {ArtifactLabel(role)} did not converge within the bounded validation reopenings.",
                    false,
                    "Inspect the source, preserved candidate when present, and CAD environment before retrying.",
                    $"captures={captureNumber}; stableSnapshots={decision.ConsecutiveAuthoritativeSnapshots}; {difference.Summary}",
                    difference.Diagnostics));
            }

            consecutiveAuthoritativeSnapshots = decision.ConsecutiveAuthoritativeSnapshots;
            previous = current;
        }

        throw new InvalidOperationException("Bounded invariant convergence terminated without a result.");
    }

    private static Dictionary<string, VisualSnapshot> ValidateObservedMappings(
        Database database,
        IReadOnlyList<CadWriteMapping> mappings,
        IReadOnlyDictionary<string, VisualSnapshot> beforeVisual,
        InvariantArtifactRole role,
        CancellationToken cancellationToken)
    {
        var observed = new Dictionary<string, VisualSnapshot>(StringComparer.Ordinal);
        using var transaction = database.TransactionManager.StartOpenCloseTransaction();
        foreach (var mapping in mappings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entity = ResolveEntity(database, transaction, mapping.Handle, OpenMode.ForRead);
            var expectedTextHash = role == InvariantArtifactRole.NormalizedBaseline
                ? mapping.ExpectedSourceTextHash
                : mapping.ApprovedFinalTextHash;
            if (!string.Equals(TextHash(EntityText(entity)), expectedTextHash, StringComparison.Ordinal))
                throw new InvalidDataException(role == InvariantArtifactRole.NormalizedBaseline
                    ? "NORMALIZED_BASELINE_TEXT_CHANGED"
                    : "POST_WRITE_TEXT_MISMATCH");
            if (entity.HasFields) throw new InvalidDataException("FIELD_BACKED_EXCLUDED");
            var after = VisualSnapshotFor(database, transaction, entity);
            var visualError = role == InvariantArtifactRole.NormalizedBaseline
                ? "NORMALIZED_BASELINE_VISUAL_INVARIANTS_CHANGED"
                : "VISUAL_INVARIANTS_CHANGED";
            EnsureExpectedVisualIdentity(mapping, after.Properties, visualError);
            if (!string.Equals(beforeVisual[mapping.SegmentId].Fingerprint, after.Fingerprint, StringComparison.Ordinal))
                throw new InvalidDataException(visualError);
            observed.Add(mapping.SegmentId, after);
        }
        return observed;
    }

    private static void AppendAppliedMappings(
        IReadOnlyList<CadWriteMapping> mappings,
        IReadOnlyDictionary<string, VisualSnapshot> beforeVisual,
        IReadOnlyDictionary<string, VisualSnapshot> afterVisual,
        List<CadAppliedMapping> applied)
    {
        foreach (var mapping in mappings)
        {
            var before = beforeVisual[mapping.SegmentId];
            var after = afterVisual[mapping.SegmentId];
            var match = string.Equals(before.Fingerprint, after.Fingerprint, StringComparison.Ordinal);
            if (!match) throw new InvalidOperationException("A stable candidate mapping lost its validated visual identity.");
            applied.Add(new CadAppliedMapping
            {
                SegmentId = mapping.SegmentId,
                Result = "WriteSucceeded",
                PostWriteTextHash = mapping.ApprovedFinalTextHash,
                VisualEvidence = new CadVisualInvariantEvidence
                {
                    BeforeProperties = before.Properties,
                    AfterProperties = after.Properties,
                    BeforeFingerprint = before.Fingerprint,
                    AfterFingerprint = after.Fingerprint,
                    InvariantMatch = match,
                    BeforeExtents = before.Extents,
                    AfterExtents = after.Extents,
                    BoundsChanged = !string.Equals(before.Extents, after.Extents, StringComparison.Ordinal),
                    VisualReviewRequired = true
                }
            });
        }
    }

    private static VisualSnapshot VisualSnapshotFor(Database database, Transaction transaction, Entity entity)
    {
        var owner = (BlockTableRecord)transaction.GetObject(entity.OwnerId, OpenMode.ForRead);
        var (space, layout) = ResolveScope(database, transaction, entity.OwnerId);
        var properties = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["handle"] = entity.Handle.Value.ToString("X", CultureInfo.InvariantCulture),
            ["entityType"] = entity switch { DBText => "TEXT", MText => "MTEXT", _ => throw new InvalidDataException("WRITE_ENTITY_TYPE_INVALID") },
            ["space"] = space,
            ["layout"] = layout ?? string.Empty,
            ["ownerRecord"] = owner.Name,
            ["layer"] = entity.Layer,
            ["colorIndex"] = entity.ColorIndex.ToString(CultureInfo.InvariantCulture),
            ["linetypeHandle"] = entity.LinetypeId.Handle.Value.ToString("X", CultureInfo.InvariantCulture),
            ["lineWeight"] = ((int)entity.LineWeight).ToString(CultureInfo.InvariantCulture)
        };
        switch (entity)
        {
            case DBText text:
                properties["position"] = Point(text.Position);
                properties["alignmentPoint"] = Point(text.AlignmentPoint);
                properties["height"] = Number(text.Height);
                properties["rotation"] = Number(text.Rotation);
                properties["widthFactor"] = Number(text.WidthFactor);
                properties["oblique"] = Number(text.Oblique);
                properties["textStyleHandle"] = text.TextStyleId.Handle.Value.ToString("X", CultureInfo.InvariantCulture);
                properties["horizontalMode"] = text.HorizontalMode.ToString();
                properties["verticalMode"] = text.VerticalMode.ToString();
                properties["normal"] = Vector(text.Normal);
                properties["thickness"] = Number(text.Thickness);
                properties["mirroredInX"] = text.IsMirroredInX.ToString(CultureInfo.InvariantCulture);
                properties["mirroredInY"] = text.IsMirroredInY.ToString(CultureInfo.InvariantCulture);
                break;
            case MText mtext:
                properties["location"] = Point(mtext.Location);
                properties["textHeight"] = Number(mtext.TextHeight);
                properties["rotation"] = Number(mtext.Rotation);
                properties["width"] = Number(mtext.Width);
                properties["attachment"] = mtext.Attachment.ToString();
                properties["flowDirection"] = mtext.FlowDirection.ToString();
                properties["textStyleHandle"] = mtext.TextStyleId.Handle.Value.ToString("X", CultureInfo.InvariantCulture);
                properties["normal"] = Vector(mtext.Normal);
                break;
        }
        return new VisualSnapshot(properties, CadVisualEvidencePolicy.Fingerprint(properties), LegacyExtents(Extents(entity)));
    }

    private static (string Space, string? Layout) ResolveScope(Database database, Transaction transaction, ObjectId ownerId)
    {
        var dictionary = (DBDictionary)transaction.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
        foreach (DBDictionaryEntry entry in dictionary)
        {
            var layout = (Layout)transaction.GetObject(entry.Value, OpenMode.ForRead);
            if (layout.BlockTableRecordId == ownerId)
                return layout.ModelType ? ("ModelSpace", null) : ("PaperSpace", layout.LayoutName);
        }
        throw new InvalidDataException("WRITE_ENTITY_SCOPE_INVALID");
    }

    private static void EnsureExpectedVisualIdentity(
        CadWriteMapping mapping,
        Dictionary<string, string> properties,
        string errorCode = "WRITE_VISUAL_IDENTITY_MISMATCH")
    {
        if (!string.Equals(properties["handle"], mapping.Handle, StringComparison.Ordinal) ||
            !string.Equals(properties["entityType"], mapping.ExpectedEntityType, StringComparison.Ordinal) ||
            !string.Equals(properties["space"], mapping.ExpectedSpace, StringComparison.Ordinal) ||
            !string.Equals(properties["layout"], mapping.ExpectedLayout ?? string.Empty, StringComparison.Ordinal) ||
            !string.Equals(properties["layer"], mapping.ExpectedLayer, StringComparison.Ordinal))
            throw new InvalidDataException(errorCode);
    }

    private static Entity ResolveEntity(Database database, Transaction transaction, string handle, OpenMode mode)
    {
        var value = long.Parse(handle, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        try
        {
            var id = database.GetObjectId(false, new Handle(value), 0);
            if (id.IsNull || !id.IsValid) throw new InvalidDataException("WRITE_HANDLE_NOT_FOUND");
            return transaction.GetObject(id, mode, false, true) as Entity ?? throw new InvalidDataException("WRITE_HANDLE_NOT_ENTITY");
        }
        catch (Autodesk.AutoCAD.Runtime.Exception) { throw new InvalidDataException("WRITE_HANDLE_NOT_FOUND"); }
    }

    private static string EntityText(Entity entity) => entity switch
    {
        DBText text => text.TextString,
        MText mtext => mtext.Contents,
        _ => throw new InvalidDataException("WRITE_ENTITY_TYPE_INVALID")
    };

    private static InvariantSnapshot InvariantFingerprint(Database database, HashSet<string> targetHandles)
    {
        var rows = new Dictionary<string, CadInvariantDiagnosticRow>(StringComparer.Ordinal);
        using var transaction = database.TransactionManager.StartOpenCloseTransaction();
        var table = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
        var references = OwnerReferenceMetadata.Create(table, transaction);
        foreach (ObjectId recordId in table)
        {
            var record = (BlockTableRecord)transaction.GetObject(recordId, OpenMode.ForRead);
            // AutoCAD may renumber anonymous dimension/dynamic block names during SaveAs.
            // The owning block-table-record handle is the stable structural identity.
            var ownerIdentity = record.Handle.Value.ToString("X", CultureInfo.InvariantCulture);
            foreach (ObjectId entityId in record)
            {
                var entity = (Entity)transaction.GetObject(entityId, OpenMode.ForRead);
                var handle = entity.Handle.Value.ToString("X", CultureInfo.InvariantCulture);
                var key = ownerIdentity + "|" + handle;
                var diagnostic = DiagnosticRow(entity, record, ownerIdentity, targetHandles.Contains(handle), references.For(recordId));
                var legacy = targetHandles.Contains(handle) ? TargetInvariant(entity, ownerIdentity) : GeneralInvariant(entity, ownerIdentity, diagnostic.Extents);
                rows[key] = diagnostic with { InvariantRowFingerprint = TextHash(legacy) };
            }
        }
        return new InvariantSnapshot(rows, TextHash(string.Join('\n', rows.OrderBy(row => row.Key, StringComparer.Ordinal).Select(row => row.Value.InvariantRowFingerprint))));
    }

    private static InvariantDifference DescribeInvariantDifference(IReadOnlyDictionary<string, CadInvariantDiagnosticRow> before, IReadOnlyDictionary<string, CadInvariantDiagnosticRow> after)
    {
        var added = after.Keys.Except(before.Keys, StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal).ToArray();
        var removed = before.Keys.Except(after.Keys, StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal).ToArray();
        var changed = before.Keys.Intersect(after.Keys, StringComparer.Ordinal)
            .Where(key => !InvariantRowsEquivalent(before[key], after[key]))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        static string Sample(IReadOnlyList<string> values) => values.Count == 0 ? "none" : string.Join(',', values.Take(8));
        var diagnostics = CadInvariantDiagnosticsPolicy.Create(
            before,
            after,
            added, removed, changed);
        return new InvariantDifference(
            $"added={added.Length} [{Sample(added)}]; removed={removed.Length} [{Sample(removed)}]; changed={changed.Length} [{Sample(changed)}].",
            diagnostics);
    }

    private static string GeneralInvariant(Entity entity, string owner, CadInvariantExtents? extents) => string.Join('|',
        entity.Handle.Value.ToString("X", CultureInfo.InvariantCulture), entity.GetRXClass().DxfName, owner, entity.Layer,
        entity.ColorIndex.ToString(CultureInfo.InvariantCulture), entity.LinetypeId.Handle.Value.ToString("X", CultureInfo.InvariantCulture),
        ((int)entity.LineWeight).ToString(CultureInfo.InvariantCulture), LegacyExtents(extents));

    private static bool InvariantSnapshotsEquivalent(
        IReadOnlyDictionary<string, CadInvariantDiagnosticRow> before,
        IReadOnlyDictionary<string, CadInvariantDiagnosticRow> after) =>
        CadNormalizedInvariantPolicy.AreEquivalent(before, after);

    private static bool InvariantRowsEquivalent(CadInvariantDiagnosticRow before, CadInvariantDiagnosticRow after) =>
        CadNormalizedInvariantPolicy.AreEquivalentRow(before, after);

    private static string TargetInvariant(Entity entity, string owner)
    {
        var common = string.Join('|', entity.Handle.Value.ToString("X", CultureInfo.InvariantCulture), entity.GetRXClass().DxfName, owner, entity.Layer,
            entity.ColorIndex.ToString(CultureInfo.InvariantCulture), entity.LinetypeId.Handle.Value.ToString("X", CultureInfo.InvariantCulture), ((int)entity.LineWeight).ToString(CultureInfo.InvariantCulture));
        return entity switch
        {
            DBText text => string.Join('|', common, Point(text.Position), Point(text.AlignmentPoint), Number(text.Height), Number(text.Rotation), Number(text.WidthFactor), Number(text.Oblique), text.TextStyleId.Handle.Value.ToString("X", CultureInfo.InvariantCulture), text.HorizontalMode, text.VerticalMode, Vector(text.Normal), Number(text.Thickness), text.IsMirroredInX, text.IsMirroredInY),
            MText mtext => string.Join('|', common, Point(mtext.Location), Number(mtext.TextHeight), Number(mtext.Rotation), Number(mtext.Width), mtext.Attachment, mtext.FlowDirection, mtext.TextStyleId.Handle.Value.ToString("X", CultureInfo.InvariantCulture), Vector(mtext.Normal)),
            _ => throw new InvalidDataException("WRITE_ENTITY_TYPE_INVALID")
        };
    }

    private static CadInvariantDiagnosticRow DiagnosticRow(Entity entity, BlockTableRecord owner, string ownerHandle, bool target, OwnerReferenceMetadata references)
    {
        return new CadInvariantDiagnosticRow
        {
            EntityHandle = Bounded(entity.Handle.Value.ToString("X", CultureInfo.InvariantCulture)),
            OwnerHandle = Bounded(ownerHandle),
            DxfType = Bounded(entity.GetRXClass().DxfName),
            RuntimeClass = Bounded(entity.GetType().FullName ?? entity.GetRXClass().Name),
            OwnerBlockName = Bounded(owner.Name),
            OwnerClass = Bounded(owner.GetRXClass().DxfName),
            IsTargetText = target,
            IsAnonymousDimensionBlockName = owner.Name.StartsWith("*D", StringComparison.OrdinalIgnoreCase),
            ReferencedByDimensionCount = references.DimensionCount,
            ReferencedByNonDimensionCount = references.NonDimensionCount,
            DerivedDimensionGraphicsCandidate = references.DerivedDimensionGraphicsCandidate,
            Layer = Bounded(entity.Layer),
            ColorIndex = entity.ColorIndex,
            LinetypeHandle = Bounded(entity.LinetypeId.Handle.Value.ToString("X", CultureInfo.InvariantCulture)),
            Lineweight = (int)entity.LineWeight,
            Extents = Extents(entity),
            InvariantRowFingerprint = string.Empty
        };
    }

    private static CadInvariantExtents? Extents(Entity entity)
    {
        try
        {
            var extents = entity.GeometricExtents;
            return new CadInvariantExtents { Minimum = Bounded(Point(extents.MinPoint)), Maximum = Bounded(Point(extents.MaxPoint)) };
        }
        catch (Autodesk.AutoCAD.Runtime.Exception) { return null; }
    }

    private static string LegacyExtents(CadInvariantExtents? extents) => extents is null ? "NO_EXTENTS" : $"{extents.Minimum}:{extents.Maximum}";

    private static string Point(Autodesk.AutoCAD.Geometry.Point3d point) => $"{Number(point.X)},{Number(point.Y)},{Number(point.Z)}";
    private static string Vector(Autodesk.AutoCAD.Geometry.Vector3d vector) => $"{Number(vector.X)},{Number(vector.Y)},{Number(vector.Z)}";
    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static string Bounded(string value) => value.Length <= CadInvariantDiagnosticsLimits.MaximumStringLength
        ? value
        : value[..(CadInvariantDiagnosticsLimits.MaximumStringLength - 1)] + "…";
    private static string TextHash(string value) => "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string FileHash(string path) { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); return "sha256:" + Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    private static bool ContainsReparsePoint(string path)
    {
        FileSystemInfo? current = File.Exists(path) ? new FileInfo(path) : new DirectoryInfo(path);
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) return true;
            current = current switch { FileInfo file => file.Directory, DirectoryInfo directory => directory.Parent, _ => null };
        }
        return false;
    }

    private static Result<CadWriteResponsePayload> Failure(string code, ErrorCategory category, string message) =>
        Results.Failure<CadWriteResponsePayload>(new ContractError(code, category, message, false));
    private static Result<T> Failure<T>(string code, ErrorCategory category, string message) =>
        Results.Failure<T>(new ContractError(code, category, message, false));
    private static Result<CadReconcileResponsePayload> ReconcileFailure(string code, ErrorCategory category, string message,
        string? diagnosticId = null, CadInvariantDiagnostics? invariantDiagnostics = null) =>
        Results.Failure<CadReconcileResponsePayload>(new ContractError(code, category, message, false, DiagnosticId: diagnosticId, InvariantDiagnostics: invariantDiagnostics));

    private static Result<StableInvariantObservation> StableInvariantFailure(string code, string message) =>
        Results.Failure<StableInvariantObservation>(new ContractError(code, ErrorCategory.Integrity, message, false));

    private static string ArtifactLabel(InvariantArtifactRole role) =>
        role == InvariantArtifactRole.NormalizedBaseline ? "normalized baseline" : "candidate";

    private static string ArtifactChangedCode(InvariantArtifactRole role) =>
        role == InvariantArtifactRole.NormalizedBaseline ? "NORMALIZED_BASELINE_ARTIFACT_CHANGED" : "CANDIDATE_ARTIFACT_CHANGED";

    private static string UnsafeTransitionCode(InvariantArtifactRole role) =>
        role == InvariantArtifactRole.NormalizedBaseline ? "NORMALIZED_BASELINE_TRANSITION_UNSAFE" : "CANDIDATE_INVARIANT_TRANSITION_UNSAFE";

    private static string UnstableCode(InvariantArtifactRole role) =>
        role == InvariantArtifactRole.NormalizedBaseline ? "NORMALIZED_BASELINE_UNSTABLE" : "CANDIDATE_INVARIANTS_UNSTABLE";

    private sealed record VisualSnapshot(Dictionary<string, string> Properties, string Fingerprint, string Extents);
    private sealed record InvariantSnapshot(Dictionary<string, CadInvariantDiagnosticRow> Rows, string Fingerprint);
    private sealed record InvariantDifference(string Summary, CadInvariantDiagnostics Diagnostics);
    private sealed record StableInvariantObservation(
        InvariantSnapshot Invariant,
        Dictionary<string, VisualSnapshot> ObservedMappings);
    private enum InvariantArtifactRole { NormalizedBaseline, Candidate }

    private sealed record OwnerReferenceMetadata(int? DimensionCount, int? NonDimensionCount, bool? DerivedDimensionGraphicsCandidate)
    {
        public static OwnerReferenceMetadata Unknown { get; } = new(null, null, null);

        public static OwnerReferenceIndex Create(BlockTable table, Transaction transaction)
        {
            try
            {
                var dimensions = new Dictionary<ObjectId, int>();
                var nonDimensions = new Dictionary<ObjectId, int>();
                foreach (ObjectId recordId in table)
                {
                    var record = (BlockTableRecord)transaction.GetObject(recordId, OpenMode.ForRead);
                    foreach (ObjectId entityId in record)
                    {
                        var entity = transaction.GetObject(entityId, OpenMode.ForRead) as Entity;
                        if (entity is Dimension dimension && !dimension.DimBlockId.IsNull)
                            dimensions[dimension.DimBlockId] = dimensions.GetValueOrDefault(dimension.DimBlockId) + 1;
                        else if (entity is BlockReference reference && !reference.BlockTableRecord.IsNull)
                            nonDimensions[reference.BlockTableRecord] = nonDimensions.GetValueOrDefault(reference.BlockTableRecord) + 1;
                    }
                }
                return new OwnerReferenceIndex(dimensions, nonDimensions, false);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                return new OwnerReferenceIndex([], [], true);
            }
        }
    }

    private sealed record OwnerReferenceIndex(Dictionary<ObjectId, int> Dimensions, Dictionary<ObjectId, int> NonDimensions, bool IsUnknown)
    {
        public OwnerReferenceMetadata For(ObjectId recordId)
        {
            if (IsUnknown) return OwnerReferenceMetadata.Unknown;
            var dimensions = Dimensions.GetValueOrDefault(recordId);
            var nonDimensions = NonDimensions.GetValueOrDefault(recordId);
            return new OwnerReferenceMetadata(dimensions, nonDimensions, dimensions > 0 && nonDimensions == 0);
        }
    }
}
