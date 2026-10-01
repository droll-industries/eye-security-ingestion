# Architecture Decision Records

Short records of the significant decisions, in a lightweight Nygard format
(Context → Decision → Consequences). Each one says what we chose, what we gave up, and
what would change in production.

| #    | Decision | Status |
|------|----------|--------|
| [0001](0001-solution-architecture.md) | CLI → single-endpoint API → in-process queue → workers | Accepted |
| [0002](0002-in-memory-queue-and-durability.md) | In-memory queue for the assessment, durable broker in production | Accepted (with known risk) |
| [0003](0003-assumed-downstream-contracts.md) | Proposed Enrichment/Analytics contracts (OpenAPI, based on the CSV) enforced by fakes + contract tests | Accepted, **to confirm with owning teams** |
| [0004](0004-api-contract-async-202-and-idempotency.md) | 202 Accepted + per-record report; idempotency keys; at-least-once delivery | Accepted |
| [0005](0005-data-validation-and-normalization.md) | Partial acceptance; normalize categories to MITRE ATT&CK; shared validator | Accepted |
| [0006](0006-enrichment-resilience.md) | Two-level retries, circuit breaker, per-IP cache, dead-letter | Accepted (revised by 0010) |
| [0007](0007-analytics-rate-limiting.md) | Client-side sliding-log limiter, 20 msg / 10.5 s | Accepted |
| [0008](0008-technology-choices.md) | .NET 10, minimal APIs, System.CommandLine, Microsoft.Extensions.Http.Resilience | Accepted |
| [0009](0009-batch-delivery-to-analytics.md) | Batch delivery: `POST /events/batch` (≤100 events/message), two-stage pipeline, single-event fallback | Accepted, **contract to confirm** |
| [0010](0010-enrichment-outage-handling.md) | Enrichment outages: time-based retry budget, pause on open circuit, single-flight cache, stale-if-error | Accepted |
