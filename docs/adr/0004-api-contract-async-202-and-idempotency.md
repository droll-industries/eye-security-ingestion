# 0004 – API contract: 202 Accepted, per-record report, idempotency

## Context
There is one endpoint, records take minutes to deliver (ADR 0001), the input data is messy
(ADR 0005), and CLI uploads can be retried (timeouts, re-runs). We must not lose records
or ingest them twice.

## Decision
`POST /api/v1/ingestions` with body `{ "records": [ActivityRecordDto…] }` (JSON, typed fields).

| Status | When | Body |
|---|---|---|
| **202** | ≥1 record valid and queued | `IngestionResponse` (ingestionId, received, accepted, rejected[index, id, errors]) |
| **422** | every record invalid | same `IngestionResponse` |
| 400 | empty or > `MaxRecordsPerRequest` (10 000) | ProblemDetails |
| 409 | `Idempotency-Key` reused with a different payload | ProblemDetails |
| 503 + `Retry-After` | queue at capacity (back-pressure) | ProblemDetails |

* **Partial acceptance:** valid records go ahead and invalid ones are reported (reasons in ADR 0005).
* **Idempotency-Key header:** the CLI sets it to the SHA-256 of the batch. The API stores
  `key → (payload fingerprint, response)` for 24 h. A retry gets the **original** response
  back with `Idempotent-Replayed: true` and nothing is queued again. 503s are not stored,
  so they can be retried. This makes CLI retries and re-runs after partial failures safe.
* **CLI → API is JSON, not the raw CSV:** the CLI must parse anyway to filter, and a typed
  contract keeps CSV quirks out of the API. Other producers could use the API directly.
* **At-least-once delivery to Analytics, with a deterministic `eventId`** = SHA-256 of the
  normalized record content. We don't use the record `id` because the sample has the same
  id on different records (4568, 517009, 541902). The same record uploaded twice (even under
  different keys or filters) gets the same `eventId`, so Analytics can deduplicate.
  Exactly-once delivery to an external HTTP service isn't achievable, so we get the
  equivalent with at-least-once delivery plus a key Analytics can deduplicate on.
* Versioned route (`/v1`) so the contract can evolve.

## Consequences
* + Retrying is always safe for clients, and partial uploads can be finished by re-running.
* − The idempotency store is in memory and per instance (prod: Redis or a DB unique index).
  A global lock around keyed submissions is fine at this scale (it only enqueues in memory).
* − 202 doesn't confirm delivery (see ADR 0001 on the missing status endpoint).
