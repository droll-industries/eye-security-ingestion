# 0003 – Proposed downstream contracts (docs unavailable)

## Context
The ticket links to docs for the Enrichment and Analytics services, but the links couldn't
be accessed and we found no public docs. We know only the auth header
(`Authorization: eye-am-hiring`), the Analytics rate limit, and that Enrichment is flaky.
Waiting for the docs would block the work; guessing without writing anything down would
leave hidden assumptions.

## Decision
**Write the contracts ourselves, based on the CSV data, and treat them as proposals** to
agree with the owning teams:

* [`docs/contracts/enrichment.openapi.yaml`](../contracts/enrichment.openapi.yaml):
  `GET /enrichment?ip={ip}` → `EnrichmentResult` (`ip` required; `country`, `asn`,
  `asOrganization`, `reputationScore`, `knownMalicious`, `tags`, `lastSeenUtc` optional;
  `additionalProperties: true`).
* [`docs/contracts/analytics.openapi.yaml`](../contracts/analytics.openapi.yaml):
  `POST /events/batch` with up to 100 `AnalyticsEvent`s → `200` with a result per event
  (preferred, ADR 0009), and `POST /events` with one event → `202` (fallback). Includes a table mapping each CSV column
  to its event field, a strict schema (`additionalProperties: false`, MITRE enums), and
  `eventId` deduplication semantics, and per-event batch results.

What the CSV told us:
* **The event shape:** each CSV column maps to one event field after normalization
  (ADR 0005), plus the derived `techniqueId`, `eventId`, `ingestionId`.
* **The enrichment key is the IP:** in the sample, one IP appears on several
  records/assets and one asset has several IPs. Threat intel belongs to the network
  indicator, and an IP key is cacheable (ADR 0006).
* **`id` is not unique** (4568, 517009, 541902), so deduplication uses a content-derived
  `eventId`, not `recordId` (ADR 0004).

How the contracts are enforced (three layers):
1. **The client code** (`EnrichmentClient`, `AnalyticsClient`): the only classes that know
   the endpoint shapes. A real contract that differs means changing only these.
2. **`tools/FakeServices`** implements the specs *strictly*: auth → 401, invalid IP → 400,
   any schema violation in an event → 400 with the list of violations, 20/10 s → 429 +
   `Retry-After`, duplicate `eventId` → 202 no-op.
3. **Contract tests** (`tests/Ingestion.Tests/Contracts`):
   * `ContractSpecTests` parses the YAML and checks that the serialized `AnalyticsEvent`
     has exactly the declared properties, that the category/technique enums match
     `CategoryNormalizer`, and the `eventId` pattern. The specs and the code can't drift
     apart unnoticed.
   * `ClientContractTests` runs our real clients against the fake: the full
     enrich → deliver path is accepted, batch partial failures and duplicates are reported per
     event, 400 is permanent, and 401 and 429 are retried.

Design choices inside the contracts:
* Enrichment is **open** (`additionalProperties: true`, only `ip` required) and passed through
  unchanged, so the Canary Team can add fields without breaking ingestion.
* The Analytics event is **closed** (`additionalProperties: false`, enums): this is our
  output, so we want drift from it to fail loudly.
* An unknown IP gets `200` with only `ip`, not `404`. "No intel" is a valid result, not an
  error that would send the record to the dead-letter queue.
* Error classes: 408/429/5xx transient; **401/403/404 are configuration errors**, retried and
  logged at Error (they affect every record, so dead-lettering would empty the backlog into the
  DLQ; ADR 0010); other 4xx (400/422, rejected events) permanent.

## Consequences
* + The interview, the Canary/Analytics teams and SecOps all have a concrete proposal to
  review, instead of an assumption buried in code.
* + Changing the contract later is mechanical: update the YAML, and the tests show which
  code needs to follow.
* − These contracts are **ours, not the providers'**. Before release they must be confirmed;
  most importantly, that Analytics supports **our batch endpoint** and counts one batch as
  one message (ADR 0009). If not, `SingleEvent` mode is the fallback.
* − The fake checks the spec by hand rather than with a JSON-Schema validator (kept small
  on purpose). The spec tests cover the drift risk this leaves for the event shape.
