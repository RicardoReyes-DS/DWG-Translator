# ADR-0003: Evidence-bound recovery after provider rate limiting

- Status: Accepted
- Date: 2026-10-02

## Context

A contextual batch can fail with `OPENAI_RATE_LIMITED` after saving some translation batches or before saving any. The provider adapter marks every HTTP 429 as retryable, but 429 also represents exhausted credits and spend or usage limits. The batch recovery planner does not assign a safe action to the rate-limit error. A generic retry could discard valid review progress, trust incomplete evidence, or repeatedly call a provider account that cannot accept work.

## Decision

- Treat `OPENAI_RATE_LIMITED` as a recoverable translation failure only when the failed job, immutable batch manifest, source/output binding, job version, failed operation, and retryable translation error agree.
- Parse only a bounded error envelope for HTTP 429. Known credit, spend, and usage exhaustion codes become distinct nonretryable platform errors; an `insufficient_quota` type without a recognized code also stops. Keep temporary 429 responses retryable. Never persist or echo the provider error body.
- If a partial review exists and satisfies the existing exact missing-only checkpoint checks, resume only its missing translation batches under a new recovery scope.
- If no review directory exists and the exact failed prepare operation is present, create a fresh child job through `RetryCadFresh`. Never reuse a partially written or invalid review as an empty checkpoint.
- Reject divergent evidence. Recovery remains a newly approved action and keeps `CreateNew` output semantics.

## Consequences

The recovery plan can distinguish partial progress from a failure before the first completed translation batch. A temporary provider rate limit still needs external capacity to recover; the code does not infer that the limit has reset or retry requests automatically. Credit or spend exhaustion requires the account owner to restore capacity before any new provider work.
