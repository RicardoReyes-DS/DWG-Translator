# ADR-0002: Manifest-bound discipline classification

- Status: Accepted
- Date: 2026-10-02

## Context

The contextual batch policy requires a resolved discipline before a reviewed translation can be written. Generic layers and model-space layouts may not provide one, even when the DWG basename carries a discipline code. The existing context policy recognizes an architectural basename fallback but does not represent structural drawings or the delimited electrical and structural codes.

## Decision

- Introduce `cad-semantic-context/1.3`. Preserve the meaning and hashes of contexts created under 1.0–1.2.
- Recognize `Structural` from structural layer/layout/block evidence. When local evidence is absent, accept only a single, delimited `ARQ`, `ELE`, or `EST` token in the manifest-bound DWG basename as a discipline fallback. Reject conflicting basename tokens. Local CAD evidence takes precedence; the context records a drawing-name resolution only when the fallback is used.
- Bind each basename fallback to the exact manifest filename during CAD extraction validation. Do not derive discipline from a parent directory or an unbound path.
- Keep per-segment review, terminology checks, readback, `VisualStrictV2`, and human visual review as independent gates. A resolved discipline does not approve a translation.
- Keep earlier review snapshots immutable. Reprocessing under 1.3 uses a new plan and distinct output; it needs the usual CAD and provider authorization.

## Consequences

Structural and electrical batches with generic CAD layers can reach explicit review when their filenames provide unambiguous evidence. Existing 1.2 jobs remain auditable but are not silently upgraded. Any filename without a supported, unique token still fails closed at the contextual generation gate.
