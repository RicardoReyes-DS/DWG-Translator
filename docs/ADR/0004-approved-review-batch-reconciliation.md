# ADR-0004: Reconcile an approved child review into its batch

## Status

Accepted for the source candidate. CAD execution remains subject to a separate preflight and operator authorization.

## Context

A child review can be approved through the CLI or MCP after the batch reaches `ReviewRequired`. The batch file projection is then older than the durable child job and review. Reconciliation must refresh that projection without sending text to the translation provider or opening CAD. The previous reconciliation path required the child to remain in `ReviewRequired`; an already approved child failed and left the batch in `RecoveryRequired`.

## Decision

- Accept an approved child only when its job version advances exactly once from the batch projection, the review is complete, its automation receipt binds the current batch manifest and semantic context, and no output or generation operation exists. Read the durable approved review to refresh counts and receipt fingerprint.
- Preserve the existing separately validated path for a superseding review from an older recovery scope.
- If the first reconciliation failed solely while reading approved child reviews, allow one retry using the **same** idempotency key at the exact failed batch version. Require the known failed projection, absent output artifacts, and no CAD process. Record the retry count durably; further replays remain read-only.
- The retry only refreshes batch metadata and issues a new short-lived generation approval. Writing still requires a separate exact approval and the normal generation safety gates.

## Consequences

No translation request or CAD operation is repeated during review reconciliation. A mismatched receipt, changed source binding, existing output, or divergent child state fails closed. Tests cover the approved transition, one-time retry, and rejection of a second retry.
