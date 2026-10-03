# ADR-0005: Correct an approved review after a prewrite invariant gate

## Status

Accepted for the source candidate. A correction and any later CAD write still require separate operator authorization.

## Context

A batch can reject an approved review before CAD writing when a final text violates an invariant. Repeating extraction or provider translation would discard useful reviewed work. Editing durable JSON by hand would bypass optimistic versions and audit evidence.

## Decision

- Extend explicit review application with a narrowly scoped `reviseApproved` option. It is accepted only when a batch is `CompletedWithFailures`, its exact child file failed with `BATCH_HUMAN_REVIEW_REQUIRED` / `REVIEW_INVARIANT_FAILED`, the job and review are still approved at the expected versions, and no output or generation operation exists.
- Require the complete per-segment decision set, a new manifest/context-bound reviewer and QA receipt, and exactly one changed final text per call. The changed text must pass CAD text and terminology invariants, including numeric sequence. Preserve all other final texts.
- Save the revised review and advance the approved job version with an audit event. The operation does not call CAD or the provider. Generation still requires a fresh plan and exact approval, and publishes to a new output only after readback and `VisualStrictV2`.
- Keep a failed batch's history. Individual recovered child outputs are reported separately unless a later evidence-bound batch close operation is added.

## Consequences

Approved translations can be corrected without repeating provider calls. The failed batch remains an honest record of its first generation attempt; direct child recovery does not silently rewrite its result. Tests cover the admission gate and a one-row revision.
