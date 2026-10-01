# 0010 – Surviving Enrichment outages: time-based retries, pausing on an open circuit, single-flight, stale-if-error

## Context
A review of the enrichment path (ADR 0006) found that its failure handling worked for **random**
failures but not for **outages**, which the ticket warns about ("intermittent issues… unsure if
Canary Team has resolved their issue").

1. **Attempt-based retry turned outages into mass dead-lettering.** With `MaxAttempts = 5` and
   delays of 5/10/20/40 s, a record gave up about 75 s after its first failure. During an outage the
   circuit opens and every call fails immediately, so the 8 workers kept cycling records:
   take one → fail instantly → use an attempt → requeue. **An outage of about 2 minutes would have
   dead-lettered the entire backlog.** The tests didn't catch it because the fake only failed
   at random.
2. **Concurrent cache misses multiplied load:** with 8 workers, several missed the cache for
   the same IP at once and each called the service. The fake received about 160 calls for 101 unique IPs.
3. **Cache expiry during an outage blocked records** whose IP had perfectly good (slightly
   older) intel.

## Decision
1. **Retry for a time budget, not a number of attempts** (`FailureHandler`):
   * A transient failure is retried until `RetryBudget` (**6 h**) has passed since the
     record's *first* failure (`IngestionWorkItem.FirstFailedAt`), then dead-lettered.
     Dead-lettering means "this record is wrong", not "a dependency was down for a while".
   * Back-off `5 s × 2^(attempt-1)`, **capped at 5 min**, with **jitter of 80–100 %** so that a whole
     batch requeued together doesn't retry at the same moment.
   * Permanent failures (4xx, rejected events) are still dead-lettered immediately.
2. **A circuit breaker that actually trips at our volume:** `CircuitMinimumThroughput = 10`
   calls per 30 s window (Polly's default is 100). With 8 workers spending most of an outage
   in HTTP retry back-off, we make about 30 calls per window, so with the default the circuit
   (almost) never opened. *Found in review; see Verification.*
3. **Pause while the circuit is open** (`EnrichmentPause`): on `BrokenCircuitException` all
   enrichment workers stop taking records for `CircuitBreakDuration` (15 s, the same value
   configures Polly). The record that hit the open circuit is requeued **without counting an
   attempt**, since its call never reached the service. During an outage the queue simply grows,
   which is what a queue is for, and is processed after recovery.
4. **Single-flight cache** (`EnrichmentCache`, now a singleton): concurrent lookups of the same IP
   share one in-flight request.
5. **Stale-if-error:** results are fresh for 15 min (`CacheTtl`). If a refresh fails
   *transiently* (including an open circuit), the old result is used for up to 24 h
   (`MaxStaleAge`). Permanent errors (e.g. 400) are **not** hidden by stale data.
6. **Auth/URL errors are retried, not dead-lettered** (added in review): 401/403/404 mean *our
   configuration* is wrong (rotated key, wrong base URL; the contracts never use 404 for data).
   They affect every record, so treating them as permanent would dead-letter the entire
   backlog within seconds. They're classified transient with `IsConfigurationError`, logged at
   **Error**, and retried within the budget while an operator fixes the configuration.

## Verification
* Unit tests on virtual time: a 30-minute outage → delivered after many attempts, not
  dead-lettered; an outage longer than the budget → dead-lettered with the reason
  "Retry budget exhausted"; an open circuit → workers paused, no attempt used; back-off capped
  and jittered; 8 concurrent misses → 1 fetch; stale served for 3 h but not after 25 h; permanent
  errors not hidden.
* End to end with `POST /admin/enrichment-outage?seconds=90` on the fake, uploading the full
  file during the outage (the attempt-based policy would have dead-lettered the whole backlog):

  | | First run (Polly default `MinimumThroughput` = 100) | After the review fix (10) |
  |---|---|---|
  | Calls that reached the down service | 224 | **14** |
  | Circuit opened / half-opened | 1 / 1 | 6 / 6 (re-probes every 15 s) |
  | HTTP-level retries | 240 | 63 |
  | Delivered / dead-lettered | 992 / 0 | **992 / 0** |
  | Delivery done after recovery | +83 s | +71 s |

  **Review correction:** the first run's write-up credited the pause for the low call
  count, but the Polly event log showed the circuit had opened only once. The load was
  really limited by HTTP retry back-off. Only after lowering the minimum throughput did the
  pause behave as designed.
* **100 successful enrichment calls for 100 unique valid IPs** (about 160 before single-flight).

## Consequences
* + Outages shorter than the retry budget now cause **delay, not data loss**.
* + Much less load on a struggling service: workers pause, IPs are fetched once, stale data is reused.
* − **Recovery lag:** after the service recovers, records wait for their *next scheduled* retry,
  up to `MaxRetryDelay`. In the 90 s outage test, delivery finished 71 s after recovery.
  *Next step:* when the circuit closes again (Polly `OnClosed`), release records that are
  waiting early (with jitter, so the service isn't flooded). A durable broker (ADR 0002)
  makes this easier: stop and restart the consumer instead of tracking delayed retries
  in memory.
* − A 6 h budget means a long outage builds a large backlog that only shows up as delay.
  **This must be monitored:** queue depth, age of the oldest record, circuit open > N min
  (README next steps). The budget is a backstop, not the alert.
* − Stale enrichment is not marked as stale in the event sent to Analytics. If analysts need
  to know, add an `enrichedAt` field to the contract.
* − The single-flight fetch isn't cancelled by any single caller (others may be waiting). It is
  bounded by the Polly total timeout (60 s), which can delay shutdown by up to that long.
