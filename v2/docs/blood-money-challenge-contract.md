# Blood Money persistent friend challenges (#82)

Audited against fetched `origin/dev` at `c7dc6bd0e6ea34aed54ce13a8d9c22c72d5d06bf` on
2026-10-09 (America/Chicago). Live #78/#82/#83/#84 and all open PRs were read before coding;
there were no open PRs. #79/#80/#81 are already implemented in this baseline, irrespective
of tracker state. This slice composes their existing financial authority.

## Lifecycle and participants

`BloodMoneyChallenge` owns the immutable ordered roster, creator, positive integer equal
stake, trimmed bounded ruleset ID, positive ruleset version, create request key, timestamps,
and revision. It uses the existing `BloodMoneyChallengeId` and shared `PlayerId`.
Participant rows carry player ID, unique seat index, Invited/Accepted/Declined status and
optional acceptance time. The creator occupies seat zero and is accepted at creation.
There are no roster editing operations.

| Operation | Eligible state / actor | Effective result |
| --- | --- | --- |
| Create | Creator plus 1–3 distinct invitees | PendingAcceptance; creator reserves stake |
| Accept | PendingAcceptance, Invited participant, before acceptance deadline | Reserve actor stake, mark Accepted; Active only when all accepted and every exact reservation is Reserved |
| Decline | PendingAcceptance, Invited participant, before deadline | Declined; record declining actor; release all current Reserved stakes |
| Cancel | PendingAcceptance, creator, before deadline | Cancelled; release all current Reserved stakes |
| Expire pending | PendingAcceptance, at or beyond acceptance deadline | Expired; release all current Reserved stakes |

Accept replays for an already accepted participant return the current pending/active view
without another reservation or revision. A pending replay at its deadline expires instead.
Decline replays require the original declining actor. Cancel replays require the creator.
Expired expiry replays are no-ops. Other terminal/active mutations are rejected.

All invitees must be accepted friends of the creator when a new challenge is created;
invitees need not be friends with each other. Existing challenges and exact create retries
do not reconstruct authority from later friendship state. Removing friendship does not
cancel, refund or change participants. Only participants can read the application view;
unknown challenges and unauthorized callers both produce NotFound. No account IDs,
financial operation IDs, treasury/posting details or private identity state are projected.

## Server time and policy

`IClock` supplies decision times. `BloodMoneyChallengeTimingPolicy` requires positive
AcceptanceWindow and GameplayWindow supplied by the host. This slice selects no product
duration. Creation fixes CreatedAt and AcceptanceDeadlineAt; activation fixes ActivatedAt
and GameplayDeadlineAt using the activation decision time plus GameplayWindow.

`now >= AcceptanceDeadlineAt` wins over otherwise authorized pending accept/decline/cancel
operations, including an already accepted actor's pending acceptance replay. Cancellation
still requires the creator; other player operations require participant membership.
Gameplay deadline is persisted and projected but never triggers a lifecycle or financial
transition here. Active cancellation, decline and pending expiry are rejected, including
at and beyond GameplayDeadlineAt. Stakes remain held for #83/#84.

## Atomic persistence and races

Creation checks `(CreatorPlayerId, ClientRequestId)` before friendship/funding work. Exact
semantic replay compares ordered player/seat pairs, equal stake, normalized rules ID and
rules version and returns the existing challenge. Changed intent conflicts. A PostgreSQL
unique index arbitrates concurrent creates; a losing scope may conflict, and a fresh retry
resolves the winner. If a concurrent winner consumes the remaining credits after the first
request-key lookup, an insufficient-funding result rechecks that key before returning:
identical intent replays the winner and changed intent conflicts. This recovery stages no
financial changes and performs no save. Challenge IDs and reserve/release transaction IDs
are generated internally.

Challenge and participant updates, `BloodCreditReservationMutator.StageReserveAsync` /
`StageReleaseAsync`, account projections and balanced ledger postings remain staged until
**one `IUnitOfWork.SaveChangesAsync`**. Terminal operations look up each roster member and
release only existing Reserved stakes with Declined/Cancelled/PendingExpired attribution.
No second wallet, escrow account, reserved balance projection or transaction service is added.

Revision begins at 1 and advances once per effective lifecycle operation; activation and
its final participant acceptance share one increment. The store reads challenge and roster
in one tracked query. StageUpdate preserves the loaded EF concurrency baseline and writes
only lifecycle/participant status fields. It rejects changes to the request, roster or
acceptance window. A losing revision check rolls back every financial and participant
write in that save, even when some commands already executed. Failed scopes must be disposed;
retry in a new scope, with newly generated internal transaction IDs if needed.

Concurrent decisions use the server time captured by each operation and the revision they
loaded. Two operations staged on the same revision cannot both commit. A fresh operation at
the acceptance deadline expires a still-pending challenge; an already active winner remains
active. Reads provide the durable projection and do not themselves expire pending invitations.

`blood_money_challenges` has a primary ID key and unique creator/request key.
`blood_money_challenge_participants` has a challenge/player primary key and unique
challenge/seat key. Player references and the participant parent FK use restrictive deletes.
There are no relationships to Level5 competition or result tables. Existing #81 reservations
remain unchanged; this migration does not fabricate challenge history for them.

Current Create admission permits 2–4 participants. Domain and storage have no four-seat
ceiling, and seat index 4 is valid. A five-player admission change can reuse this schema;
tests round-trip five participant rows today without exposing five-player creation.

## Composition and downstream handoff

The host can call `AddLevel5Infrastructure(configuration)` followed by
`AddBloodMoneyChallenges(approvedTimingPolicy)` to register the scoped lifecycle/use cases
and existing financial mutator. No challenge HTTP route is added or enabled in #82, so the
canonical OpenAPI document remains byte-equivalent. A future HTTP adapter must derive
PlayerId from `ICurrentPlayerProvider`, never an ordinary request-body caller ID.

#83 owns wager-eligible server rules/result contracts, adjudication and settlement. The
minimal rules identity here does not authorize any result producer or payout. #84 can reuse
`ExpirePendingBloodMoneyChallengeUseCase` from a fresh scope for each claimed challenge;
it must not create a competing expiry writer. Active timeout handling belongs to those
issues. Notifications, messaging, UI integration and background workers are outside #82.

Validation and the two review passes are recorded in [the evidence document](blood-money-challenge-evidence.md).
