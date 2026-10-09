# Blood Credit account and ledger contract

Implemented for Backend #80, with the first concrete sibling-game certification for #63.
This capability uses the existing V2 projects, API process, `Level5V2DbContext`, PostgreSQL database,
and migration stream. Repository and assembly naming remains unchanged: a second real game now
exists, but this slice provides no operational benefit that warrants a disruptive rename.

## Authority and initial balances

Unity's local/offline `profile.json` BLOOD economy remains a separate authority. Backend online
Blood Credits use shared `Level5.Domain.Ids.PlayerId` exclusively. Local balance, starting 1,000
BLOOD, MatchStake, history, run state, and client/host outcomes are never issuance evidence.
There is no profile import, balance synchronization, registration grant, or automatic 1,000-credit
grant. Both existing and newly registered Backend players have an effective online balance of zero
until a trusted issuance occurs. Registration and reads do not create credit state or backfill rows.

A missing account reads as zero. Only its first trusted positive issuance creates it, in the same
commit as that issuance's ledger evidence. Positive corrections require an existing account;
negative corrections/spends against a missing account fail with insufficient credits.

## Module and persistence ownership

- `Level5.Domain.BloodMoney`: immutable account projection, transaction ID, transaction and postings.
- `Level5.Application.BloodMoney`: dedicated read/issue/spend/correct use cases and ledger store port.
- `Level5.Infrastructure.BloodMoney`: EF mappings, persistence rows and store implementation.
- API composition: ordinary typed DI; authenticated self-balance controller.

The three additive tables are `blood_money_credit_accounts`, `blood_money_credit_transactions`,
and `blood_money_credit_postings`. One `PlayerId` owns at most one account. Its `AvailableBalance`
and `Revision` are nonnegative signed `bigint`/C# `long`. Revision is an EF concurrency token.
Every successful mutation advances it once. All arithmetic is checked; overflow fails before staging.
Player references have restrictive FKs to `player_profiles`. The module exposes no transaction or
posting update/delete operation and no arbitrary posting-insertion port.

Transactions and posting contracts are immutable, including defensive read-only posting copies.
Factories and rehydration require exactly one Player posting (line 1, subject `PlayerId`) and one
Treasury posting (line 2, no `PlayerId`), equal and opposite. Treasury is only the Blood Money ledger
counterparty, with no player wallet or shared currency abstraction.

| Operation | Player posting | Treasury posting |
| --- | --- | --- |
| Issuance | `+amount` | `-amount` |
| Spend | `-amount` | `+amount` |
| Correction | `delta` | `-delta` |

Issuance/spend require positive amounts. Correction requires nonzero delta, excluding
`long.MinValue`, and a nonblank audit reference of at most 128 characters. All three operations
require nonempty transaction and player IDs and a bounded nonblank reference. Negative mutations
cannot overdraw the account. Database checks independently reject negative balances/revisions,
zero or unnegatable transaction deltas, invalid kinds/signs, empty transaction IDs, blank/oversized
references, zero posting amounts, invalid line/owner combinations, and invalid posting ownership.
The transaction PK and posting `(TransactionId, LineNumber)` PK protect uniqueness. Balancing and
projection consistency are enforced by the module's domain/store commit path and certified by
reconciliation; row-level SQL constraints alone do not establish cross-row accounting invariants.

## Trusted operations, replay and atomicity

`IssueBloodCreditsUseCase`, `SpendBloodCreditsUseCase`, and `CorrectBloodCreditsUseCase` are trusted
in-process integration capabilities. Their presence does not approve an issuance source or make
them caller-authorized HTTP APIs. Correction uses the dedicated request with transaction ID,
player ID, signed delta and mandatory audit reference. There is no public `ApplyDelta`, issuance,
spend, correction, admin endpoint or UI.

The caller supplies a stable `BloodCreditTransactionId`. On retry, exact persisted
`PlayerId`/kind/signed delta/reference matches return `IsReplay = true` without staging or saving.
Different intent with the same ID raises `ConflictException`. Server-generated time is not part
of the intent; a replay retains the original committed time and postings. The GUID is never
generated or replaced by EF. Concurrent same-ID races are protected by the transaction PK, even
when the two calls initially read no transaction.

The store only stages changes. One final `IUnitOfWork.SaveChangesAsync` commits the tracked account
update (conditioned on expected revision), transaction, and two postings in EF's database transaction.
There is no independent immediate balance update. Failed commands, uniqueness conflicts, and stale
revisions roll back the complete financial mutation. Existing conflict translation yields
`ConflictException`. A caller must discard a failed unit of work and retry the same intent in a fresh
scope. A concurrency loser may receive conflict initially; its retry resolves persisted replay or
new balance validation. With 100 credits, two competing spends of 80 can commit only one; retrying
the loser sees 20 and insufficient credits.

## Player-facing API

`GET /api/v2/games/blood-money/me/credits` requires authentication and resolves ownership through
`ICurrentPlayerProvider`. Its only response field is `availableCredits` (signed 64-bit integer).
Missing account returns `{ "availableCredits": 0 }` without inserting anything. Query-supplied
identities are not ownership inputs. No other player's path or public mutation route exists.
Account identity, revision and Treasury are absent from the HTTP response. The canonical V2
OpenAPI includes only this Blood Money operation.

## Reconciliation and migration evidence

For each transaction, `SUM(postings.Amount) = 0`, with exactly one Player line and one Treasury line.
For each player, `SUM(player postings.Amount) = account.AvailableBalance`.
PostgreSQL tests exercise two players through issuance, issuance, spend, positive and negative
corrections, then verify both equations and replay without additional rows.

The normal additive EF migration follows `20261006233500_AddEmailChangeReservation`. It creates
only the three Blood Money tables and their constraints/indexes. It neither changes shared/Level5
tables nor seeds player accounts. A dedicated-container upgrade test seeds existing Platform
identity/entitlement and Level5 match-result rows, upgrades, verifies preservation and constraints,
and confirms no pending migrations/model changes. A second dedicated container pre-creates the
later postings table: migration failure rolls back the earlier account/transaction DDL and history;
removing that fixture conflict allows a clean retry.

Tests also inject failure after execution of a posting insert, before commit, for both new-account
issuance and existing-account spend. A fresh context verifies the account, revision, transaction and
postings were all rolled back; retry succeeds. Separate contexts with a barrier before final save
prove overspend prevention, duplicate-ID races on new/existing accounts, and global duplicate-ID
conflict across different players. API tests use the real host and PostgreSQL for authentication,
minimal response shape, exact own balance, identity tampering and absent mutation routes.

`DependencyRuleTests` requires concrete Blood Money Domain/Application types and recursively guards
sibling and shared-module dependencies, including co-located Level5-owned ports and game IDs.
All prior layer rules remain. Exact implementation SHA and final validation results are recorded
in [the implementation evidence](blood-credit-ledger-evidence.md).

## #81 handoff

Reservations remain entirely owned by #81. This slice has no reserved balance, escrow, challenge,
roster, wager, settlement, refund, forfeit, purchased-credit, cash-out, blockchain, messaging or
Unity/Platform client integration. Future reservation work must extend the Blood Money contracts
and preserve atomic ledger/projection transitions, replay, restrictive identity ownership and
reconciliation. No current player endpoint permits arbitrary minting or debiting.
