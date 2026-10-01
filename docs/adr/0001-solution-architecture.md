# 0001 – Solution architecture

## Context
SecOps uploads malicious-activity CSV files. The ticket asks for a CLI and a microservice
with **one** endpoint. Every record must be enriched by an **unreliable** Enrichment Service
and then sent to an Analytics Service limited to **20 messages / 10 s**. The system is
mission-critical (incident response).

The numbers decide the design: with one record per message, the 992 valid rows in the
sample take at least 992 / 2 per second ≈ **8.3 minutes** to deliver, before any retries.
Batching (ADR 0009) cuts that to seconds, but delivery still depends on two external
services that are slow and fail (one rate-limited, one unreliable). A synchronous request
that waits for delivery would need very long timeouts, keep a connection open the whole
time, and lose all progress if anything along the way drops it.

## Decision
```
┌────────────┐  POST /api/v1/ingestions   ┌──────────────────────────────────────────────┐
│  CLI       │  (batches ≤1000, JSON,     │  Ingestion API                               │
│  parse     │   Idempotency-Key)         │  validate ─► enqueue ─► 202 + report         │
│  validate  ├───────────────────────────►│                │                             │
│  filter    │◄───────────────────────────┤                ▼                             │
│  report    │  202/422 per-record report │  IngestionQueue ─► EnrichmentWorker ×8       │
└────────────┘                            │   (cache, retry, circuit breaker; per record)│
                                          │        ─► DeliveryBuffer (bounded)           │
                                          │        ─► AnalyticsDispatcher ×1             │
                                          │   (batches ≤100, rate limiter, retry,        │
                                          │    Retry-After; ADR 0009)                    │
                                          │   dead-letter on final failure               │
                                          └───────┬───────────────────────┬──────────────┘
                                                  ▼                       ▼
                                         Enrichment Service       Analytics Service
```

* **The CLI** parses, validates (shared rules, ADR 0005), filters and uploads in batches. It
  reports per row with line numbers.
* **The API** has a single business endpoint. It validates (authoritative), queues valid
  records and **returns 202 straight away** with a per-record report (ADR 0004).
* **Background stages:** enrichment workers handle one record at a time; one dispatcher
  sends records to Analytics in batches of up to 100 per message. Each stage has resilience
  suited to its dependency (ADR 0006, 0007, 0009).
* **Projects:** `Ingestion.Core` (wire contracts + domain rules, shared), `Ingestion.Api`,
  `Ingestion.Cli`, `tools/FakeServices` (local stand-ins), `tests/Ingestion.Tests`.

## Consequences
* + Uploads finish in milliseconds. Slow or flaky dependencies never block or fail an upload.
* + Users see data-quality problems right away, before anything reaches Analytics.
* − "Accepted" means *queued*, not *in Analytics*. With only one endpoint, the CLI can't
  ask about progress later. The CLI says so and gives an estimated delivery time.
  **Future:** a `GET /ingestions/{id}` status endpoint, or notifications. We left it out
  because the ticket says single endpoint, but we'd raise it with the team.
* − Sharing `Ingestion.Core` couples the CLI and API versions. The API still re-validates,
  so an outdated CLI can't get bad data in; at worst its pre-validation is out of date.
