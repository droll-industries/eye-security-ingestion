# 0007 – Analytics rate limiting (20 messages / 10 s)

## Context
After a budget incident, Analytics allows **20 messages per 10 seconds**. Going over it
costs money or gets us throttled, so we need to stay under it **with no exceptions**.

## Decision
* **Limit on our side, before sending** (don't rely on 429s): `SlidingWindowLogRateLimiter`
  stores the timestamps of the last N grants and allows a send only when fewer than N fall
  within the window. Waiters are served in order.
  * **Why not `System.Threading.RateLimiting`?** `FixedWindowRateLimiter` and
    `TokenBucketRateLimiter` allow up to **2×** the limit across a window boundary
    (20 at t=9.9 s + 20 at t=10.0 s). `SlidingWindowRateLimiter` is only accurate to
    one segment. With N = 20 an exact log is cheap, and a test checks every window
    across a boundary burst.
  * **Safety margin:** the window is 10 s + 500 ms, to allow for clock and network jitter
    between our timing and the server's. Configurable.
* **Handler order** on the Analytics `HttpClient`:
  `retry (honours Retry-After) → rate limiter → per-attempt timeout → network`.
  * The limiter is *inside* the retry, so **every retry uses a permit**. Analytics counts
    them as messages.
  * The limiter is *outside* the attempt timeout, so waiting for a permit isn't treated as
    a failed attempt.
* A 429 is still treated as transient (retry, then requeue the record) in case the limit is
  shared with other clients or our assumptions are wrong.
* A single `AnalyticsDispatcher` sends batches of up to 100 events (ADR 0009), and each
  batch uses one permit. More senders couldn't beat the limit; enrichment workers
  (`WorkerCount = 8`) keep the next batch ready.

## Consequences
* + Verified locally: 0 × 429. Single-event mode: exactly 20 messages per ~10 s over several
  minutes. Batch mode: 992 records in 16 messages.
* − **The limit applies per process.** Scaling the API out to N instances would allow N × 20.
  Options: (a) one instance/partition sends to Analytics (simplest, throughput is fixed at
  2 msg/s anyway); (b) a distributed limiter (Redis sorted-set sliding log); (c) an egress
  gateway / API-management policy. Our preference would be (a) with the durable broker
  (ADR 0002): the API scales freely and one consumer group delivers.
* − Throughput is capped at 20 messages / 10.5 s: with batching (ADR 0009) about 190
  records/s (10k ≈ 1 min); in single-event fallback mode 2 records/s (10k ≈ 83 min).
