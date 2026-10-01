# 0005 – Data validation and normalization

## Context
Profiling `samples/example_data.csv` (999 rows, `;`-separated, CRLF, `dd/MM/yyyy HH:mm`):

| Finding | Rows |
|---|---|
| Missing `category` column (5 fields) | line 105 |
| Empty `category` | line 675 |
| IP is `N` | line 436 |
| `source` is the literal `null` | lines 604, 613, 647, 667 |
| Same `id`, different content | 4568, 517009, 541902 |
| 20 category spellings for 9 techniques (`phising`/`Phising`, `valid-accounts`/`valida_accounts`, `compromise (driveby)`…) | widespread |
| `crowdstrike` vs `crowdstrike_cs` | 240 / 111 |

## Decision
* **Partial acceptance:** reject invalid rows, ingest valid ones, and report every rejected
  row with its line number and **all** its errors. Rejecting the whole file over 7 bad rows
  would hold up incident-response data; silently dropping them would hide problems.
* **One rule set (`RecordValidator` in `Ingestion.Core`)** used by the CLI (fast feedback,
  `--dry-run`) and the API (authoritative). `Validate` is idempotent, so the API accepts
  CLI output unchanged (covered by a test).
* **Normalize `category` to canonical MITRE ATT&CK Initial-Access techniques** (T1566
  Phishing, T1078 Valid Accounts…) and add `techniqueId` to the Analytics event. Matching:
  lower-case, `-`/`_`/whitespace treated alike, other punctuation dropped, then an **explicit
  alias table** (including known typos). **Unknown categories are rejected, not guessed**:
  fuzzy matching on security data could misclassify incidents.
* Rules: positive integer `id`; non-empty `asset_name`; valid IPv4 (four octets; we don't
  accept the `0.0.0.1` shorthand) or IPv6; `created_utc` required, exactly `dd/MM/yyyy HH:mm` treated
  as UTC and no more than 5 min in the future (the API also rejects a missing `createdUtc`,
  which JSON would otherwise turn into 0001-01-01); `source` non-empty and not `null`
  (records without provenance aren't trustworthy for IR); `source` trimmed + lower-cased.
* **Duplicate ids are kept as separate events:** their content differs, so `id` isn't a
  unique key (see `eventId` in ADR 0004).
* **`crowdstrike_cs` is kept separate from `crowdstrike`:** it may be a different product
  or connector. We'd confirm with SecOps rather than merge.

## Consequences
* + Analytics gets consistent categories that map to MITRE, so data can be grouped across sources.
* − The alias table needs updating when new spellings appear. Rejected rows (with the
  exact value) make that visible. **Future:** move aliases to configuration and track
  rejection reasons as a metric.
