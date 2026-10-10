# Blood Money challenge implementation evidence (#82)

Baseline: fetched `origin/dev = c7dc6bd0e6ea34aed54ce13a8d9c22c72d5d06bf`.
Audit and validation date: 2026-10-09, America/Chicago.
Live #82, parent #78, downstream #83/#84 and the complete open-PR list were read before
implementation; the open-PR list was empty. The baseline already contains the module gate,
Blood Credit ledger and atomic reserve/release primitives. Existing financial code was reused.

See [the contract](blood-money-challenge-contract.md) for lifecycle, caller authorization,
timing, atomicity, revision handling, five-player compatibility and downstream handoff.

## Final validation

| Check | Result |
| --- | --- |
| `dotnet build v2/Level5BackendV2.sln -c Release -m:1` | Passed |
| `dotnet test v2/Level5BackendV2.sln -c Release -m:1 --no-build` | 1,224 passed; 0 failed/skipped |
| Domain tests | 372 passed, including 30 challenge transition/rehydration tests |
| Application tests | 232 passed, including positive timing-policy validation |
| Infrastructure integration tests | 363 passed against real PostgreSQL 18 containers |
| API integration tests | 227 passed against real PostgreSQL containers |
| Architecture tests | 30 passed, including concrete challenge capabilities and isolated Infrastructure dependencies |
| Focused final challenge PostgreSQL tests | 51 passed; 0 failed/skipped |
| `dotnet build Level5Backend.csproj -c Release -m:1` | Passed |
| `dotnet test Level5Backend.Tests/Level5Backend.Tests.csproj -c Release -m:1` | 32 passed; 0 failed/skipped |
| OpenAPI exporter and binary comparison to checked-in baseline | Passed, byte-identical |
| Docker V2 API image build | Passed, `level5-v2-api:issue82` |
| Self-contained Linux x64 EF migration bundle build | Passed |
| `git diff --check` | Passed |

Final local TRX reports are retained under the gitignored
`v2/artifacts/issue82-fix/{tests,legacy,focused}`.
These are local automated results. No hosted CI, production migration or deployment is claimed.
Builds retain baseline warnings in PostgresConfiguration nullability, forwarded-header
obsolete APIs, and existing ChallengeConcurrencyTests blocking-task assertions; the new
implementation introduces no additional compiler/analyzer warnings.

OpenAPI was exported with:

```text
dotnet run --project v2/tools/Level5.Api.OpenApiExport -c Release --no-build -- v2/artifacts/issue82-fix/openapi.json
fc /b v2\openapi\level5-v2.openapi.json v2\artifacts\issue82-fix\openapi.json
```

Both files have
SHA-256 `59eb7f9bd1def411c27624c7954d5c0acbcedcc0114a7b87f3dcaf1ea2ef11a0`.
There are no new public HTTP routes.

Deployment artifacts were built with:

```text
docker build -f v2/src/Level5.Api/Dockerfile -t level5-v2-api:issue82 v2/src
dotnet ef migrations bundle --project v2/src/Level5.Infrastructure --startup-project v2/src/Level5.Api --configuration Release --no-build --self-contained -r linux-x64 -o v2/artifacts/efbundle-issue82 --force
```

The bundle build used only the design-time `Host=unused;Database=unused;Username=unused;Password=unused`
connection placeholder; it was built without executing migrations against a deployed database.
Final Docker image ID: `sha256:e5475b3ad7736028597b36a8e96d08ab0510a295184389d95b390311428e82cb`.
Final Linux bundle SHA-256: `370e1f48eca590f880acaddf2bc7ee9006a70bccb88b8abe8a2ca49605cd039f`.

## Race, failure and migration proof

Barrier tests stage both competing scopes before either saves: accept/accept, accept/cancel,
accept/decline, accept/expire, cancel/expire, same-participant accept/decline and duplicate
create. Two-player activation races are covered in addition to three-player partial funding.
Forced accept-first and decline-first tests prove both same-participant commit orders.
Fresh contexts reconcile participant status, challenge revision, exact holds, available
balances, transaction IDs and balanced postings. Losers cannot leave an orphan hold, double
release, accepted participant without funding, unfunded Active challenge or held pending-terminal stake.

Command interceptors inject errors after challenge insert/update, participant insert/update,
account update, ledger transaction/posting insertion and reservation insert/update execute
but before commit. Fresh contexts observe unchanged lifecycle/financial evidence; fresh-scope
retries succeed. Tests also deliberately damage creator funding to prove final acceptance
rechecks existence, Reserved status and exact amount rather than trusting participant status.

Migration `20261010011751_AddBloodMoneyChallenges` is tested from #81's
`20261009205516_AddBloodCreditReservations`. Seeded identities, accepted friendships, product
entitlements, Level5 results, credit accounts, transactions, postings and both reserved/released
reservations remain byte-equivalent as canonical PostgreSQL JSON snapshots. New challenge
tables start empty. A conflicting participant-table sentinel causes the preceding challenge
DDL and migration history to roll back; removing only that sentinel allows a clean retry.
Tests verify restrictive FKs, required unique keys, no pending migrations/model drift, and an
actual five-player persisted/rehydrated roster including seat index 4. Earlier ledger and
reservation upgrade tests also run through the complete new migration chain.

## Review passes

Pass 1 reviewed aggregate creation/rehydration, tracked revision baselines, acceptance funding,
atomic saves, terminal releases, active-cancel prohibition, server-time deadlines, friendship
authorization and structural roster capacity. It fixed two issues before final validation:

- Pending accepted-actor replay in Domain now rejects a due acceptance window; Application
  routes that operation through the shared atomic expiry path.
- EF can update participants before checking the parent revision. A stale decline initially
  updated only Status and inherited a competing accept's AcceptedAt, causing a participant
  check violation before the revision check. The store now explicitly writes Status and
  AcceptedAt together for a changed participant. Both forced commit orders produce the
  canonical Conflict, roll back every staged financial piece and pass fresh-context reconciliation.

The store also verifies that StageUpdate cannot alter the original request fingerprint,
roster, creation time or acceptance deadline. No material findings remain from this pass.

Pass 2 reviewed scope and dependency boundaries. There is no VersusSeries reuse, settlement,
result verification, tie/forfeit/void policy, active refund, background worker, notification,
messaging, UI integration, generic challenge/idempotency framework, arbitrary rules JSON,
new service/database or duplicate wallet authority. All new game types remain in the
BloodMoney Domain/Application/Infrastructure namespaces. No material findings remain.

Follow-up senior review found a duplicate-create funding race: a request could miss its
key lookup, then an identical winner could commit using the creator's last stake, causing
the delayed request to report InsufficientCredits instead of resolving the durable key.
Creation now rechecks the key on insufficient funding before any financial staging or save.
Exact intent returns the existing view; changed intent produces Conflict; a missing key
preserves the original insufficient-funding error. Database save failures still require a
fresh scope. Two deterministic PostgreSQL tests pause immediately after the initial lookup,
commit the competing request with exactly one stake available, then resume identical or
changed intent. They verify zero delayed saves/staged changes, one challenge, one hold,
unchanged revision, correct balance and balanced ledger postings. The full validation above
was rerun after this correction. Follow-up atomicity and scope review found no remaining
material findings.
