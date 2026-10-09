# Blood Credit implementation and certification evidence

- Validated: 2026-10-09 (America/Chicago).
- Initial implementation commit: `64de7e83d469680edb4d70d6bd74fa4df432e7ac`.
- Final code commit, including replay remediation: `1c57bbee23991187c2821d228bc8b667fdeba507`.
- Implementation branch: `feat/blood-money-credit-ledger-80`, based on `dev`.
- Scope: Backend #80 and the concrete sibling-module certification required by #63/#79.
- Contract: [Blood Credit ledger](blood-credit-ledger-contract.md).

## Fresh audit before edits

| Repository | Fetched `origin/dev` SHA |
| --- | --- |
| Backend | `e464ffa246426d013838eea9be85a2c192392948` |
| Blood Money | `95c92cc3c0c3c188fe7f8cf6502042194a768d80` |
| Platform | `9f2ba2c8836fb9ca9534a921755586bf0b0a239b` |

All three open-PR lists were empty. Backend issues #78, #79, #80 and #63, their comments,
the module-entry decision, current source, migration stream, architecture rules and CI definition
were re-audited. No approved policy superseded the requested zero-online-balance policy.
Backend's change since the supplied `66602a7` baseline was a dependency vulnerability fix.
Companion repositories were read only; their production source and unrelated local work were preserved.

## Full regression results

All tests ran locally, without skipped cases. Infrastructure/API tests used real PostgreSQL 18
Testcontainers on Docker Desktop. Docker was initially stopped, then started successfully; no
PostgreSQL proof remained blocked and no in-memory provider substituted for V2 integration tests.

| Suite | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| V2 Domain | 322 | 0 | 0 |
| V2 Application | 220 | 0 | 0 |
| V2 Infrastructure integration | 262 | 0 | 0 |
| V2 API integration | 227 | 0 | 0 |
| V2 Architecture | 29 | 0 | 0 |
| **Full V2** | **1,060** | **0** | **0** |
| Legacy Backend | 32 | 0 | 0 |

The full V2 suite and build were rerun on the final code commit after remediation. Legacy checks
passed in the initial implementation run; the remediation changes no legacy source.
Focused Blood Credit runs passed: 17 Domain, 6 Application, 36 Infrastructure and 12 API cases.
The architecture suite gained 10 cases while retaining all previous guards. The full run includes
these focused cases plus all existing Level5/shared Platform regressions.

Validation commands (repository root):

```powershell
dotnet build v2/Level5BackendV2.sln -c Release -m:1
dotnet test v2/Level5BackendV2.sln -c Release -m:1 --no-build
dotnet build Level5Backend.csproj -c Release -m:1
dotnet test Level5Backend.Tests/Level5Backend.Tests.csproj -c Release -m:1
dotnet run --project v2/tools/Level5.Api.OpenApiExport -c Release -- v2/openapi/level5-v2.openapi.json
docker build -f v2/src/Level5.Api/Dockerfile -t level5-v2-api:blood-credit-80 v2/src
./v2/scripts/build-migration-bundle.ps1 -Runtime linux-x64
git diff --check
```

All passed. A second OpenAPI export compared byte-for-byte equal to the canonical file,
matching the repository's strict drift-check semantics. The OpenAPI diff adds exactly
one path/GET operation, its balance DTO and controller tag; existing operations remain intact.
The deployment image ID was
`sha256:dd3b98b08e9b372d000bef68c29685bf3727333037e71b2e452514fd673535c3`.
The Linux self-contained migration bundle compiled at `v2/artifacts/efbundle`; it was not executed
against a deployed database. These are local equivalents of `build`/`build-v2` job checks, not a
claim that hosted GitHub checks ran. Existing nullable/forwarded-header/xUnit blocking-test warnings
remain outside this slice; the new code adds no build warnings.

TRX reports and the verification OpenAPI export are retained locally under
`v2/artifacts/blood-credit-tests/` (ignored build evidence, not committed generated output).
The final V2 reports and before/after replay regressions are in its `replay-fix/` subdirectory.

## Financial and architecture proof

Real PostgreSQL tests passed for account/transaction uniqueness, player FKs and restricted deletion,
nonnegative account state, transaction checks, posting ownership/nonzero checks, mixed two-player
reconciliation, concurrent overspend, concurrent duplicate IDs on both new/existing accounts, and
duplicate-ID intent conflict across different players. Barriers force both operations to stage
against the original state before final save. Only one conflicting mutation commits.

Fault injection after a posting insert executed, before commit, proved rollback of account creation
or account update/revision, transaction and both postings, followed by successful retry. Dedicated
PostgreSQL containers proved upgrade from the immediately previous migration with preserved
Platform and Level5 data, three empty credit tables, all expected checks/FKs, and no pending
migration/model changes. A conflicting later postings table proved rollback of earlier DDL and
migration-history changes; removing the fixture conflict allowed a successful migration retry.

API proof covered 401, zero without account creation, exact own 64-bit balance, query/path identity
tampering, absent POST/PUT/DELETE mutation routes, and the minimal read-only OpenAPI contract.
Architecture proof requires actual account, transaction, issuance and balance use-case classes;
recursive sibling/shared boundaries also reject Level5-owned co-located ports and IDs.
The second module still needs no new assembly, DbContext, process, database, migration stream or
repository rename. #63's naming reassessment is therefore to retain current names.

## Two review passes

1. Reviewed ledger/balance atomicity, authority separation, default grants, public issuance,
   integer/overflow behavior, account concurrency and replay. Fixed EF's implicit GUID generation:
   transaction identity is explicitly `ValueGeneratedNever`, so empty IDs cannot be replaced before
   the database check. Corrected the test's PostgreSQL RESTRICT error expectation. Final constraints,
   failure rollback, replay and concurrency tests all pass; no material finding remains.
2. Reviewed cross-game genericization, #81 reservation leakage, challenge/settlement/refund/forfeit
   scope, Level5 reuse, duplicate infrastructure, admin/UI and real-money/blockchain scope.
   No material findings. No downstream #81 work or client integration was introduced.

## Subsequent review finding and remediation

A dedicated review confirmed a race when transaction lookup missed, another request committed
the same ID, and the first call then read the already-mutated projection. A duplicate spend could
report insufficient credits; duplicate issuance near `long.MaxValue` could report overflow.
This did not corrupt ledger state, but violated replay/conflict semantics.

The final code reads the account before transaction lookup, so a commit included in the account
projection is resolved by the subsequent ID lookup before validating the balance. A commit after
both reads remains protected by revision concurrency and transaction uniqueness.
Eight real-PostgreSQL regressions force the competing commit between reads across spend, issuance,
and positive/negative corrections, checking both matching and conflicting intent. All eight failed
before the fix and passed after it. They also verify no staging on replay/conflict, unchanged final
revision/balance, one committed transaction ID, two balanced postings and player reconciliation.
The complete 36-case Blood Credit Infrastructure suite and 6-case Application suite passed, followed
by the final 1,060-case V2 regression. No review findings remain unresolved.
