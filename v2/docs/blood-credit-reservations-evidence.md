# Blood Credit reservation implementation evidence

- Validated: 2026-10-09 (America/Chicago).
- Issue: [Backend #81](https://github.com/sweat-this/Level5Backend/issues/81).
- Branch: `feat/blood-money-reservations-81`, based on `dev`.
- Fetched baseline: `cdffb27fd08b7d1211789d5b2fab9033556302c0`.
- Contract and #82 handoff: [reservation contract](blood-credit-reservations-contract.md).

## Current-source audit

Fetched `origin/dev`, read issue #81 and the #82 boundary, and inspected all open PRs targeting
`dev` before editing. No open PR overlapped; `dev` matched the supplied baseline. Read the module
entry, ledger contract/evidence, all Blood Money Domain/Application/Infrastructure types, unit of
work, architecture guards, PostgreSQL fixtures and migration tooling. The #79 audit and #80 ledger
were already landed and were preserved. Current #78/#80/#83 issue text was also checked against
the requested financial-only scope. A final fetch/open-PR check found the same baseline and no PRs.

Unrelated `.claude/`, `.vscode/` and the API observability workspace file were preserved and excluded
from this change. Unity and Platform production source was not changed.

## Final validation

All suites ran locally with zero failed or skipped tests. Infrastructure/API suites used real
PostgreSQL 18 Testcontainers on Docker Desktop. Initial sandbox Docker access was denied; host
Docker access resolved it. No in-memory replacement was used for PostgreSQL evidence.

| Suite | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| V2 Domain | 342 | 0 | 0 |
| V2 Application | 228 | 0 | 0 |
| V2 Infrastructure integration | 312 | 0 | 0 |
| V2 API integration | 227 | 0 | 0 |
| V2 Architecture | 29 | 0 | 0 |
| **Full V2** | **1,138** | **0** | **0** |
| Legacy Backend | 32 | 0 | 0 |

The final run includes 78 new cases: 20 Domain, 8 Application and 50 PostgreSQL/migration cases.
An earlier focused run passed 84 Blood Credit infrastructure cases; the final full run also
includes the two cross-player operation-ID race cases added during review. Existing concrete
module checks now require reservation, challenge ID, mutator and standalone wrapper types.

All requested gates passed:

```powershell
dotnet build v2/Level5BackendV2.sln -c Release -m:1
dotnet test v2/Level5BackendV2.sln -c Release -m:1 --no-build
dotnet build Level5Backend.csproj -c Release -m:1
dotnet test Level5Backend.Tests/Level5Backend.Tests.csproj -c Release -m:1
dotnet run --project v2/tools/Level5.Api.OpenApiExport -c Release -- v2/openapi/level5-v2.openapi.json
docker build -f v2/src/Level5.Api/Dockerfile -t level5-v2-api:blood-reservations-81 v2/src
./v2/scripts/build-migration-bundle.ps1 -Runtime linux-x64
git diff --check
```

The OpenAPI export is byte-equivalent to the baseline checkout. `git cat-file --filters` materialized
the baseline with the workspace's `core.autocrlf=true`; `fc /b` found no differences. Both files have
SHA-256 `59eb7f9bd1def411c27624c7954d5c0acbcedcc0114a7b87f3dcaf1ea2ef11a0`.
The canonical Git diff is empty. No public routes, DTOs or client contracts changed.

Container image: `sha256:74ba26268b55c7d9110748842fb0b4b7089f2ac64c973c82b1fce6b00170482a`.
The self-contained Linux migration bundle compiled at `v2/artifacts/efbundle`; it was not executed
against a deployed database. Existing nullable, forwarded-header and blocking-xUnit warnings remain
outside this slice. These are local validation results, not a claim that hosted CI ran.
TRX reports are retained under ignored `v2/artifacts/reservations-81/final/` and `legacy/`.

## Financial, concurrency and failure proof

Real PostgreSQL tests certify durable reserve/release, exact replay, changed-intent conflict,
terminal reservation reuse rejection, account/posting reconciliation and unique transaction evidence.
Barriers force competing operations to stage before either saves. Cases cover two 80-credit holds
against 100 available credits, same/different IDs for one reservation, reserve versus Spend and
positive/negative Correction, same/different-ID concurrent release, changed release reason, and
globally reused reserve/release IDs across different player accounts. At most one conflicting
operation commits. Fresh retries replay, conflict or fail against the newly reduced balance;
compatible correction retries preserve both deltas without lost updates.

Forced commits between account and reservation reads verify replay/conflict before balance or
lifecycle validation. Staging several mutations against one player, including reserve/release
before any commit, proves visibility of candidates and preservation of the original account CAS.
A fresh context sees no staged changes until the one final save.

Six fault cases throw after executed account, posting or reservation commands, before commit,
for both reserve and release. Fresh contexts verify unchanged balance/revision/reservation and
absence of transaction/postings for the failed ID. Fresh-scope retry succeeds with that same ID.
SQL checks, natural/operation uniqueness and all restrictive FKs are exercised independently.

Dedicated migration containers upgrade from the ledger migration with existing identity/profile,
entitlement, Level5 result, credit account, Issuance/Spend/Correction transactions and postings.
Existing values/evidence remain unchanged; the reservation table starts empty; the new kinds work;
no migrations/model changes remain pending. A sentinel reservation table causes upgrade failure:
the earlier transaction-kind constraint change and migration history roll back, old credit data
remains valid, and removing only the fixture sentinel permits a clean retry.

## Two review passes

1. Reviewed financial authority, dedicated Reserve/Release posting signs, release amount ownership,
   replay ordering/fingerprints, double release, account CAS, rollback and #82 composition. Ensured
   stores expose staged candidates and retain the original database revision across several staged
   operations; the permanent PostgreSQL composition test passes. Added cross-player ID collision
   races and a missing-release-account regression. Two initial constraint fixtures were corrected
   to reach the intended database checks rather than EF key validation/transaction uniqueness.
   No unresolved financial or implementation finding remains.
2. Reviewed for challenge-state leakage, friend authorization, deadlines/expiry workers, settlement,
   tie/forfeit/void/winner policy, generic wallets, new infrastructure/services, client integration,
   messaging and real-money/blockchain scope. None was introduced. No public reserve/release API
   or independent commit in the staging component exists. #82 still owns definitive legality and
   cancel-versus-accept tests, composed with these financial primitives in one unit of work.
