# 0002 – In-memory queue (assessment) vs durable broker (production)

## Context
Async processing needs a queue between the endpoint and the workers. The assessment says
"no cloud infrastructure". The system is mission-critical, so losing accepted records is the
worst failure it can have.

## Decision
Use `System.Threading.Channels` behind a small `IngestionQueue` class
(batch enqueue, single-item consume, delayed requeue, pending count, capacity limit), and an
`IDeadLetterSink` kept in memory and logged at Error.

**In production** this becomes a durable broker, e.g. **Azure Service Bus** or **AWS SQS**:
* The endpoint publishes the accepted batch (transactional/batch send) **before** returning 202.
* Workers are competing consumers, with peek-lock / visibility timeout, so a crash means
  redelivery, not loss.
* The record-level back-off (ADR 0006) becomes scheduled messages / visibility delay.
* The broker's native **DLQ** replaces `InMemoryDeadLetterSink`, with an alert on DLQ
  depth > 0 and a redrive runbook.

## Consequences
* + No infrastructure needed to run or review the solution. The code paths (capacity →
  503 back-pressure, requeue with back-off, dead-letter) are the same ones a broker needs.
* − **Known risk, accepted only for the assessment:** a process crash or redeploy loses
  queued records, and a 202 only promises "queued in this process". Graceful shutdown does
  not drain the queue.
* − Dead letters and idempotency keys only exist in memory, per instance.
