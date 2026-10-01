# Malicious Activity Ingestion

A CLI and a single-endpoint microservice that SecOps uses to upload malicious-activity CSV logs.
Each record is **validated**, **enriched** through the (unreliable) Enrichment Service, and
**delivered** to the Analytics Service, which is limited to 20 messages per 10 s.

> The design decisions are recorded in **[docs/adr](docs/adr/README.md)**.

## Architecture at a glance

```
CLI ──(JSON batches + Idempotency-Key)──► POST /api/v1/ingestions ──► 202 + per-record report
                                                   │
                                                   ▼
                                          queue ─► enrichment workers ×8 ─► Enrichment (per record: cache · retry · circuit breaker)
                                                   │
                                                   ▼ bounded buffer (back-pressure)
                                          dispatcher ×1 ─► Analytics POST /events/batch (≤100 events/message ·
                                                   │        sliding-log limiter 20/10 s · retry · Retry-After)
                                                   └─► dead-letter (rejected events / after final failure)
```

* **Async by design:** delivery depends on a rate-limited service and an unreliable one, so
  the API answers in milliseconds and delivers in the background ([ADR 0001](docs/adr/0001-solution-architecture.md)).
* **Batched delivery:** the limit counts *messages*, so up to 100 events go in each message.
  The sample's 992 records reach Analytics in about 35 s (16 messages) instead of about 8.3 min.
  Single-event mode remains as a fallback ([ADR 0009](docs/adr/0009-batch-delivery-to-analytics.md)).
* **Safe to retry:** idempotency keys on uploads, plus a content-based `eventId` so Analytics
  can deduplicate ([ADR 0004](docs/adr/0004-api-contract-async-202-and-idempotency.md)).
* **Messy data handled explicitly:** 7 of the 999 sample rows are invalid and reported by line;
  20 category spellings are normalized to 9 MITRE ATT&CK techniques ([ADR 0005](docs/adr/0005-data-validation-and-normalization.md)).
* **Flaky enrichment:** timeouts, retries with jitter and a circuit breaker; a **time-based**
  retry budget, so outages delay records instead of dead-lettering them; workers pause while
  the circuit is open; auth/URL errors are retried and alerted rather than dead-lettered; a
  single-flight, stale-if-error cache per IP ([ADR 0006](docs/adr/0006-enrichment-resilience.md),
  [ADR 0010](docs/adr/0010-enrichment-outage-handling.md)).
* **Rate limit is never exceeded:** an exact sliding-log limiter rather than the built-in
  ones, which can burst ([ADR 0007](docs/adr/0007-analytics-rate-limiting.md)).

## Repository layout

| Path | What |
|---|---|
| `src/Ingestion.Core` | Wire contracts + domain rules shared by CLI and API (`RecordValidator`, `CategoryNormalizer`) |
| `src/Ingestion.Api` | ASP.NET Core minimal API, queue, background worker, downstream clients, rate limiter |
| `src/Ingestion.Cli` | `System.CommandLine` CLI: CSV reader, filter, batching, user feedback |
| `docs/contracts` | **Proposed** OpenAPI contracts for the Enrichment and Analytics services, based on the CSV |
| `tools/FakeServices` | Local stand-ins that enforce those contracts strictly: Enrichment (30 % failures, 5 % slow), Analytics (batch + single endpoints, per-event validation, eventId dedup, strict 20/10 s, 429) |
| `tests/Ingestion.Tests` | Unit, integration and contract tests (93) |
| `samples/example_data.csv` | The SecOps sample file |
| `docs/adr` | Architecture decision records |

## Running it

Prerequisite: **.NET 10 SDK** (`global.json` pins 10.0.x).

```bash
dotnet build
dotnet test          # 93 tests, incl. contract tests against docs/contracts

# terminal 1 – fake downstream services on :5090
dotnet run --project tools/FakeServices

# terminal 2 – the API on :5080 (Development env supplies the auth header)
dotnet run --project src/Ingestion.Api

# terminal 3 – the CLI
dotnet run --project src/Ingestion.Cli -- samples/example_data.csv --dry-run
dotnet run --project src/Ingestion.Cli -- samples/example_data.csv --source defender --category phishing --from 2024-03-01 --to 2024-04-30
curl localhost:5090/stats   # what the fake Analytics received, and any 429s
curl -X POST "localhost:5090/admin/enrichment-outage?seconds=90"   # simulate an Enrichment outage
```

To use the real services, set `Enrichment__BaseUrl`, `Analytics__BaseUrl` and the
`*__AuthorizationHeader` values with environment variables or a secret store. The base
`appsettings.json` leaves the secret empty on purpose, so startup fails fast outside Development.

## CLI

```
ingestion-cli <file> [--api-url <url>] [--source <s>...] [--category <c>...]
                     [--from <date>] [--to <date>] [--delimiter ';'] [--batch-size 1000]
                     [--dry-run] [--verbose]
```

* **Filters (nice-to-have):** options are combined with AND, multiple values with OR. They
  match **normalized** values, so `--category phishing` also matches `Phising`, and
  `--category T1566` works too. `--to 2024-04-30` includes that whole day.
* **Feedback:** summary counts, each invalid or rejected row with line number and reasons,
  per-batch results with ingestion ids, a note when a batch was already submitted, and the
  API's estimated delivery time (backlog ÷ rate limit × batch size).
* **Exit codes:** `0` all ingested · `2` finished but some rows rejected · `1` fatal (bad
  file or args, API unreachable). Re-running after a failure is safe: accepted batches are
  not ingested again.

Example output:
```
Rows read:     999
Invalid rows:  7
To ingest:     992

Invalid rows (not sent):
  - line 105: category is missing
  - line 436: ip 'N' is not a valid IPv4/IPv6 address
  - line 604: source is missing
  ...
Batch 1/1 (992 records): accepted 992, rejected 0, ingestion id 01a0f91a-...

Accepted for processing: 992/992
Accepted records are queued for enrichment and delivery to Analytics, which is rate limited. Estimated delivery time (best case, including queued backlog): about 00:00:06.
```

## API

`POST /api/v1/ingestions` — body `{ "records": [ { id, assetName, ip, createdUtc, source, category } ] }`,
optional `Idempotency-Key` header. Returns `202`/`422` with
`{ ingestionId, received, accepted, rejected: [{ index, id, errors[] }], estimatedDeliverySeconds }`, or ProblemDetails for
`400` / `409` / `503`. `GET /health` is a liveness probe. Full contract: [ADR 0004](docs/adr/0004-api-contract-async-202-and-idempotency.md).

## Key assumptions
Full list in the ADRs; each one is also stated in the code where it applies.
1. **The Enrichment and Analytics contracts are our own proposals**, because the docs weren't
   available: [`enrichment.openapi.yaml`](docs/contracts/enrichment.openapi.yaml) and
   [`analytics.openapi.yaml`](docs/contracts/analytics.openapi.yaml), based on the CSV
   (column → field mapping in the Analytics spec). They are enforced by the fakes and by
   contract tests, and must be confirmed with the owning teams
   ([ADR 0003](docs/adr/0003-assumed-downstream-contracts.md)).
2. `created_utc` is UTC in `dd/MM/yyyy HH:mm`. CSV fields are never quoted.
3. Enrichment depends only on the IP (so it can be cached). Analytics can deduplicate on `eventId`.
4. Records without a source, or with an unknown category, are rejected rather than guessed.
5. Analytics counts one batch request as one message against its rate limit (our proposed contract).
6. One API instance (the rate limiter and idempotency store are per process; [ADR 0007](docs/adr/0007-analytics-rate-limiting.md)).
7. Authentication and authorization of our own API are out of scope (per the brief).

## Verified locally
* 93 automated tests: outage handling on virtual time (a 30-min outage is delivered, not
  dead-lettered; budget exhaustion; pause on open circuit; capped/jittered back-off);
  enrichment cache (single-flight, stale-if-error, permanent errors not hidden); contract tests (YAML specs ↔ code; real clients ↔ strict fakes,
  including batch partial failures, duplicates and error classification); batch dispatcher
  (size/linger flush, per-event outcomes, whole-batch retry); normalizer covers every spelling in the sample; validator; CSV reader
  on the real sample (exactly lines 105, 436, 604, 613, 647, 667, 675 rejected); filter;
  rate limiter on virtual time, including a boundary burst; enrichment worker retry/back-off/dead-letter;
  API integration (202, 422, 400, idempotent replay, 409).
* End to end against FakeServices with the full file, in batch mode: **992 records delivered
  in 35 s using 16 messages**, 0 × 429, 36 enrichment failures and 8 hangs absorbed, **0
  dead-letters**. Single-event mode (first version): exactly 20 messages per 10 s, 0 × 429.
* **90 s full Enrichment outage** during the upload: 992/992 delivered, **0 dead-letters**; the
  circuit opened and re-probed every 15 s while workers paused (only 14 calls reached the
  down service); 100 successful enrichment calls for 100 unique IPs thanks to single-flight.
  Known trade-off: delivery finished 71 s after recovery, because of back-off (ADR 0010).

## Known limitations and next steps
In priority order for a production rollout:
1. **Durable queue + DLQ** (Service Bus / SQS) instead of in-memory. This is the main gap for
   a mission-critical system ([ADR 0002](docs/adr/0002-in-memory-queue-and-durability.md)).
2. **Observability:** OpenTelemetry traces across CLI → API → workers. Metrics for queue depth,
   **age of the oldest pending record**, rate-limiter wait, circuit state (alert if open > N min),
   stale enrichments served, DLQ depth (alert > 0), and rejections by reason. The 6 h retry
   budget is a backstop, not the alert.
3. **Faster recovery after outages:** release records waiting for back-off as soon as the circuit
   closes (ADR 0010).
4. **Delivery status** for users: `GET /ingestions/{id}` (needs agreement, since the ticket
   says one endpoint) or a notification when an ingestion completes.
5. **Scale-out:** a single Analytics sender or a distributed limiter; idempotency store in Redis or a DB.
6. Confirm the proposed downstream contracts (`docs/contracts`) with the Canary and Analytics teams, above all that Analytics will provide **our proposed batch endpoint** (otherwise use `Analytics:Mode=SingleEvent`),
   and confirm with SecOps how to handle `crowdstrike_cs` and the alias list.
7. Containerization, CI pipeline, load tests, `LoggerMessage` source-generated logging,
   CsvHelper if quoted fields appear.

## Note on AI assistance
Built pair-programming with an AI assistant (Claude Code), as the brief allows. Every
design decision is recorded in the ADRs and I can walk through and defend the code.
