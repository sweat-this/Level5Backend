# Blood Credit reservation contract

Implemented for Backend [#81](https://github.com/sweat-this/Level5Backend/issues/81), on
`dev` baseline `cdffb27fd08b7d1211789d5b2fab9033556302c0`. The existing
[credit ledger](blood-credit-ledger-contract.md) remains the only financial authority.

## Identity, lifecycle and accounting

`BloodMoneyChallengeId` is a Blood Money-owned opaque GUID. It has no relationship to a Level5
series, Unity challenge, account or match participant ID. #82 will reuse it for its canonical
challenge aggregate. The reservation natural key is `(ChallengeId, PlayerId)`; there is no
surrogate reservation ID, second wallet, escrow balance or `ReservedBalance` projection.

`BloodCreditReservation` is immutable and rehydratable. It records the positive stake, status,
reserve/release transaction IDs, times, optional release reason and positive revision. Its only
transitions are creation as `Reserved` and `Reserved -> Released`. A released pair cannot reopen,
even with a new transaction ID. The only release reasons are `Declined`, `Cancelled` and
`PendingExpired`. Settlement/consumption is a future #83 terminal transition, not implemented here.

| Kind | Player posting | Treasury posting |
| --- | ---: | ---: |
| Reserve | `-Amount` | `+Amount` |
| Release | `+Amount` | `-Amount` |

Both are distinct ledger kinds with exactly two equal-and-opposite immutable postings. Reserve
reduces `BloodCreditAccount.AvailableBalance`, the only spendable balance. Missing accounts cannot
reserve and are never created/funded by either operation. Release derives its exact amount from
the stored reservation and requires the existing account. Checked balance/revision arithmetic
fails before staging. Domain and PostgreSQL checks enforce the new transaction signs while
retaining Issuance, Spend and Correction behavior.

## Trusted staging, commits and retries

`BloodCreditReservationMutator.StageReserveAsync` and `StageReleaseAsync` validate financial intent
and stage account, transaction/postings and reservation through the dedicated module stores.
They do not reference `IUnitOfWork` or commit. Optional trusted standalone reserve/release use cases
wrap staging and save exactly once for a new operation; exact replay stages/saves nothing.
There are no public reserve/release endpoints; the existing authenticated balance GET is unchanged.

The caller supplies a stable `BloodCreditTransactionId`, reused as the operation idempotency key.
Audit references are generated internally using invariant decimal amounts and GUID `N` format:

```text
reserve:{ChallengeId}:{PlayerId}:{Amount}
release:{ChallengeId}:{PlayerId}:{stored Amount}:{ReleaseReason}
```

The maximum reference length is 108 characters, below the existing 128-character bound. Clients
cannot supply an arbitrary reference or release amount. Exact persisted kind/player/delta/reference
matches replay with the original committed evidence; any changed fingerprint using the same ID
conflicts. A different operation ID for an existing reserve or completed release also conflicts.
The reservation retains both operation IDs; no generic idempotency table exists.

Mutation reads obtain the account, then reservation, then transaction-ID evidence. Thus any
competing commit included in either projection is resolved before balance/lifecycle validation.
A commit after the reads remains protected by account/reservation revision checks, natural-key
uniqueness and the transaction PK. Failed scopes must be discarded; retry in a fresh scope with
the original ID resolves replay/conflict or validates the newly committed balance.

Stores expose staged state within the current scope, permitting several operations to compose
before a save. Repeated account staging retains the original database revision for the final CAS
while advancing the candidate revision for each ledger mutation. Unchanged tracked rows do not
replace fresh database reads. No financial mutation uses an immediate update or separate commit.

## Persistence and reconciliation

`20261009205516_AddBloodCreditReservations` follows the existing ledger migration in the same
`Level5V2DbContext` and migration stream. It creates only `blood_money_credit_reservations` and
deliberately expands the transaction-kind check. Existing rows remain valid; no accounts, grants
or reservations are backfilled.

The table enforces its natural PK, nonempty challenge/player IDs, positive amount/revision,
state/release-field coherence, release time at/after reserve time, distinct reserve/release IDs,
unique reserve ID, unique non-null release ID, and restrictive player/transaction FKs. Reservation
revision is an EF concurrency token. Database row checks do not replace cross-row financial proof:
the module commit path and reconciliation tests establish ledger ownership, amounts and balancing.

For every player, `SUM(player postings) == AvailableBalance`. Every transaction has exactly two
owned postings summing to zero. A Reserved row has exactly one matching Reserve transaction with
`PlayerDelta == -Amount` and no release evidence. A Released row additionally has exactly one
Release with `PlayerDelta == Amount`. Each operation services one reservation lifecycle.
The module exposes no update/delete port for ledger evidence.

## #82 composition and authority boundary

#81 establishes financial atomicity, not challenge eligibility. The future challenge owner must
authorize creation/acceptance/decline/cancellation/expiry and stage its challenge transition,
participant transition and reservation mutation within one `IUnitOfWork.SaveChangesAsync`.
In particular, #82 must add definitive cancellation-versus-acceptance concurrency tests using its
canonical challenge revision. Financial race tests alone do not prove that acceptance remains
legal after cancellation. Reservations contain no challenge status, tombstone, deadline, friend
policy, expiry worker, result verification or settlement policy.

Blood Money remains in its existing Domain/Application/Infrastructure namespaces, using shared
`PlayerId`, `IClock` and `IUnitOfWork`. Architecture guards require concrete reservation and mutator
types and continue to reject Level5 game domains, ports, IDs and private `AccountId`. There are no
Unity/Platform changes, messaging, public wager API, new services/databases or real-money scope.

Validation and review results are recorded in [reservation evidence](blood-credit-reservations-evidence.md).
