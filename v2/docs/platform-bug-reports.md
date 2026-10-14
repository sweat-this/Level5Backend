# Shared Platform bug reports

Audited 2026-10-14: Backend `dev` `9850828e003bbf78c27d3f17d51730300cbfef43`; Platform `dev` `9f2ba2c8836fb9ca9534a921755586bf0b0a239b`. Both fetched before implementation. No open PRs. Recent messaging work already provides `IdempotentInsertRecovery`; community abuse reports (#93/#96 and Platform #58) are a separate capability. Nothing has delivered this game bug reporting page since the historical audit. The user explicitly approved authenticated-only submission on 2026-10-14; no purchase or entitlement is required.

## Reviews before implementation

Architecture/correctness pass: keep the existing Domain/Application/Infrastructure/API direction, shared `Platform` ownership, one immutable content value, one use case/port, one table in the current DbContext/migration stream. Use the existing clock, current-player provider, unit of work and conflict recovery. Normalize before replay comparison; distinct submission IDs remain distinct reports. Frontend catalog slugs are reporting keys, never ProductIds or module identifiers. No generic tickets, extra dependencies, game changes or legacy endpoint changes.

Security/operations pass: authenticate before writing; derive reporter and timestamp server-side. Reject unknown DTO members, invalid UUIDs, unsupported keys, NUL and excessive lengths. Use safe model-binding errors, a dedicated account partition with five attempts/minute and no queue. Preserve the UUID and exact payload after any uncertain response; acknowledge only a validated committed receipt. Keep text private and retrieval operator-only, with deployment and deletion limitations below.

## Wire and persistence contracts

`POST /api/v2/platform/bug-reports` requires the existing bearer authentication. Body: nonempty UUID `clientSubmissionId`; exact `gameKey` (`level5`, `zromp`, `secret-robot`, `blood-money`); required `title` (160 UTF-16 code units), `description` (8000); optional `category` (`gameplay`, `technical`, `interface`, `other`), `stepsToReproduce` (8000), `gameVersion` (64). Raw and normalized text are bounded. CRLF and CR normalize to LF; surrounding whitespace is trimmed; empty optional text becomes null. NUL is rejected. Text is never interpreted as HTML. Keys are case-sensitive. Category is trimmed before allowlist validation. The HTTP body envelope is bounded to 128 KiB; over-limit envelopes may receive the host's 413 in addition to field-validation 400s.

201 means newly committed; 200 means normalized replay; both return `{ "id": "<report UUID>" }`. Invalid input is 400; unauthenticated callers 401; changed content under an existing key 409; rate exhaustion 429 with Retry-After. Existing exception handling produces safe 500/503 responses for infrastructure failures. Cancellation propagates through reads and commits. There is no report GET endpoint or fabricated public Location URI.

Migration `20261014143750_AddPlatformBugReports` creates `bug_reports`: UUID `id` PK; restrictive `reporter_player_id` FK to `player_profiles.Id`; UUID `client_submission_id`; bounded game/content columns; server `created_at` timestamptz. Unique `(reporter_player_id, client_submission_id)` permits identical text with different IDs. Reads compare all normalized fields. A concurrent unique violation goes through the existing conflict translator and `IdempotentInsertRecovery`, detaches the rejected pending row, reloads the winner, and performs the same comparison. No report becomes success before the unit of work commits.

The limiter uses the verified account `sub`, which maps to one server-owned player. It is per API instance, consistent with existing in-process limiter infrastructure; multiple replicas multiply the fleet-wide budget. It counts retries and rejected bodies. Do not claim a distributed abuse-control guarantee.

## Restricted operator access

No browser, player API or public listing can retrieve reports. Provision a dedicated operator identity through the existing database access process, limited to approved personnel, a private connection and an audited session. A database administrator can grant only SELECT on `bug_reports` to that existing role; do not give it player/account tables or mutation privileges. Example grant (replace the role through the normal provisioning process):

```sql
GRANT SELECT ON TABLE bug_reports TO approved_bug_report_reader;
```

Use a parameterized query from the approved database console/client, with a narrow game and time interval. Start with metadata and request content only for specific triage work:

```sql
SELECT id, game_key, created_at, category, game_version
FROM bug_reports
WHERE game_key = $1 AND created_at >= $2 AND created_at < $3
ORDER BY created_at DESC, id DESC
LIMIT 100;

SELECT id, game_key, created_at, title, description, steps_to_reproduce,
       game_version, reporter_player_id, client_submission_id
FROM bug_reports WHERE id = $1;
```

Treat content and reporter identity as private user data. Do not paste it into public issues or telemetry. Access logs should record the operator, query purpose, report IDs and time, without report text. Keep EF sensitive-data logging and PostgreSQL `Include Error Detail` disabled; do not enable HTTP request-body logging or tracing. Reports require no extra device/IP metadata. Production database credentials and operator grants are operational prerequisites, not delivered by this patch.

## Retention and deletion proposal

Proposed policy, requiring operator/product approval before production rollout: retain reports for 90 days, permit a documented longer legal/security hold only when justified, and delete through a restricted, audited operator process after reviewing the affected IDs. No automatic purge or new account-deletion policy ships here. The restrictive FK deliberately blocks accidental player deletion; any future identity deletion/anonymization workflow must explicitly address reports in its own approved contract. Backups follow the existing approved backup retention process; removing a live row is not proof of erasure from backups.

Idempotency lasts while the report row exists. Deleting rows also deletes submission keys; after purging, an old request could create a new report. Do not replay historical submissions after approved deletion, and establish a retry horizon before adding automated retention. Public report exposure is outside this implementation and would require a separate authorization/privacy decision.

## Deployment order and rollback

1. Back up and apply the additive migration with the existing migration workload before enabling the endpoint on live traffic.
2. Deploy the reviewed Backend commit and verify readiness plus a private authenticated smoke submission.
3. Pin Platform to that exact Backend commit, regenerate/check OpenAPI types, then deploy Platform.
4. Verify login return, receipt and durable row using an approved test account; remove test data via the approved operator process.

Rollback Platform first, then Backend if needed; leave the additive table and data intact. The migration Down drops all report data and is not the routine rollback. No deployment or production readiness is asserted by local test results.

## Implementation validation (2026-10-14)

- `dotnet restore v2/Level5BackendV2.sln`: exit 0.
- `dotnet build v2/Level5BackendV2.sln --no-restore`: exit 0; existing unrelated compiler/analyzer warnings remain.
- Final `dotnet test v2/Level5BackendV2.sln`: exit 0, 1462 passed, 0 failed, 0 skipped (Domain 406; Application 249; Architecture 30; real-PostgreSQL Infrastructure 474; real-PostgreSQL API 303).
- Fresh controller OpenAPI export compared byte-for-byte with canonical: no differences, exit 0.
- The first full run exposed six existing migration-inventory assertions that assumed the old latest schema; only their expected new table/pending-migration inventory was extended. All upgrade/rollback checks pass in the final run. An initial new restrict-delete assertion was corrected to PostgreSQL 18's `restrict_violation` code.
- Final architecture review: immutable shared Platform ownership, existing ports/UoW/conflict translator/recovery, no new game dependency, only the additive reporting API/schema. Final security review: server authority, no entitlement gate, bounded input/rate budget, race and rollback proof, safe errors, private retrieval and explicit retention/deployment prerequisites. No legacy or gameplay runtime changes.

Local logs/TRX are under ignored `v2/TestResults/bug-reports-complete*`; they are development evidence, not hosted CI or production certification.
