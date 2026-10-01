# 0009 – Batch delivery to Analytics

## Context
The Analytics limit is **20 messages per 10 seconds**, which counts messages, not records.
With one record per message (the first version), ingestion was capped at 2 records/s: the
992-row sample took about 8.3 minutes and 10k records would take about 83 minutes. For a
mission-critical incident-response pipeline that's the main bottleneck, and the ticket's
wording points to batching as the intended answer. The Analytics docs weren't available, so
the batch endpoint is part of our **proposed** contract (ADR 0003).

## Decision
**Contract** (`docs/contracts/analytics.openapi.yaml`):
* `POST /events/batch` with `{ "events": [ … 1–100 AnalyticsEvent … ] }` → `200` with
  `results`: one per event, **in order**, `accepted | duplicate | rejected (+errors)`.
* **Partial success:** events are validated individually. Only a malformed envelope fails
  the whole batch (400). 429/5xx mean nothing was stored, so retry the whole batch.
* `duplicate` (an `eventId` already stored) counts as delivered, which makes whole-batch
  retries safe (at-least-once delivery, deduplicated by the receiver; ADR 0004).
* `POST /events` stays as a **fallback** (`Analytics:Mode = SingleEvent`) in case the real
  service has no batch endpoint. The same pipeline then sends batches of 1.

**Pipeline**, split into two stages:
```
IngestionQueue ─► EnrichmentWorker ×8 ─► DeliveryBuffer ─► AnalyticsDispatcher ×1 ─► POST /events/batch
                  (per record:           (bounded, ≈2      (batch = 100 records or
                   cache, retry, CB)      batches; back-     2 s linger; 1 permit
                                          pressure)          per batch)
```
* **Enrichment stays per record** (no batch API is known). More workers (4 → 8) keep the
  buffer full, so a batch is ready whenever a permit frees up.
* **One dispatcher:** throughput is limited by permits, not concurrency, and one sender
  makes it easy to reason about the rate limit. It sends when the batch is full **or** when the
  linger time (2 s) has passed since its first record. Under load batches fill up; when traffic
  is light, a record waits at most 2 s.
* The rate limiter, retry (with `Retry-After`) and timeout handlers stay as they were
  (ADR 0007). They now apply per batch request.
* **Per-event outcome:** accepted/duplicate → completed. Rejected → dead-letter (permanent).
  Whole-batch failure → every record goes to `FailureHandler`: requeue with back-off
  (transient) or dead-letter (permanent / attempts exhausted). A requeued record goes
  through enrichment again; the per-IP cache usually makes that free.
* **Bounded buffer = back-pressure:** if Analytics slows down, enrichment pauses instead of
  enriching far ahead (which would let cached enrichment go stale and use memory).
* The API now returns `estimatedDeliverySeconds` (backlog ÷ max records/s), so the CLI shows
  an estimate based on the real configuration instead of a hard-coded rate.

## Consequences
* + Measured locally with the full sample: **992 records delivered in 35 s using 16 messages**
  (previously about 8.3 min using 992), with 0 × 429 and 0 dead-letters. The limit now
  allows 200 records/s, about 100× more.
* + Enrichment is now the bottleneck (30 % failures and 20 s hangs in the fake). The next
  improvement would be on that side (e.g. a batch enrichment API, more concurrency, a
  longer cache).
* − A record can wait up to `BatchLinger` (2 s) more before delivery when traffic is light.
* − **Depends on our proposed batch endpoint being accepted.** If not, `SingleEvent` mode
  works today, at 2 records/s.
* − A batch is all-or-nothing on transport failure. One flaky large batch delays 100 records
  instead of one, which is bounded by retries and dead-lettering.
* − `estimatedDeliverySeconds` is a best case: it ignores enrichment latency and retries.
