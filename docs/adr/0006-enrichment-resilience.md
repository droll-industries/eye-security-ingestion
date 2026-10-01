# 0006 – Enrichment Service resilience

## Context
"Intermittent issues… proceed with caution." Enrichment is **required** for each record, so
a failure must not drop a record or let an unenriched one through.

## Decision
Layered, from the inside out:

1. **Per-attempt timeout (5 s)**, so a hung call can't hold a worker.
2. **HTTP retries:** 4 retries, exponential back-off **with jitter** (to avoid retry storms),
   on 408/429/5xx/network errors/timeouts. Handles short blips.
3. **Circuit breaker:** opens for 15 s when ≥50% of calls fail within 30 s **and there were at
   least 10 calls** (`CircuitMinimumThroughput`; Polly's default of 100 never trips at our
   volume, see ADR 0010). Stops us adding
   load to a struggling service; calls fail fast while it's open.
   (1–3 = `AddStandardResilienceHandler`, Microsoft.Extensions.Http.Resilience / Polly v8.)
4. **Record-level retry with back-off** (`FailureHandler`, shared by the enrichment and
   delivery stages): if all HTTP retries fail, the record is **requeued** with capped,
   jittered exponential back-off for up to a **time budget** (6 h). While the circuit is
   open, workers **pause** instead of cycling records. *(Revised by ADR 0010: the first
   version used 5 attempts, about 75 s, which would have dead-lettered the whole backlog
   during a 2-minute outage.)*
5. **Dead-letter** when the retry budget is exhausted, or straight away on a permanent (4xx) error.
   Nothing is silently dropped (ADR 0002 for production DLQ + alerting).
6. **Per-IP cache (`EnrichmentCache`):** the sample repeats IPs heavily (101 unique in 992 rows).
   Fresh for 15 min; concurrent lookups of the same IP share one request; a stale result is
   used when a refresh fails (ADR 0010).

**Not chosen:** sending records to Analytics **without** enrichment when Enrichment is down.
The ticket says data "needs to be enriched", and half-enriched data could mislead
incident-response analysts. If SecOps preferred speed over completeness, a "send now,
enrich later" approach is possible, but it's a product decision.

## Consequences
* + In the local test (30% failures, 5% hangs), 992 records were processed with **0
  dead-letters**. All failures were absorbed by layers 1–4. A 90 s full outage also ended
  with 0 dead-letters (ADR 0010).
* − If Enrichment is down for a long time, records wait (bounded by the 6 h retry budget)
  and then pile up in the DLQ. That's the right failure mode, but it needs alerting and a redrive tool.
* − The cache assumes enrichment depends only on the IP and is stable for 15 min (ADR 0003).
