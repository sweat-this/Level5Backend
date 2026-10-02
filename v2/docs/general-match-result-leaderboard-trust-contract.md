# V2 general match-result and leaderboard trust contract

## Scope

This document is the canonical trust and ranking contract for ordinary V2 match results submitted
through `POST /api/v2/match-results` and read through
`GET /api/v2/leaderboards/{modeId}`. An ordinary result is an **authenticated client-reported
result**. Authentication binds the result to a player, but the backend does not independently
verify that the match occurred or that its gameplay metrics were legitimately earned.

This contract does not apply to correspondence `VersusSeries` attempts. Correspondence has its own
server-owned lifecycle, frozen rules, authorization, and result-disclosure rules. An ordinary match
result never mutates that aggregate.

## Authority boundary

### Server-authoritative

The backend owns and enforces:

- `PlayerId`, derived from the authenticated principal rather than accepted from the request;
- `MatchResultId`, generated when a new result is accepted;
- `CreatedAt`, taken from the server clock when the result is ingested;
- `(PlayerId, ClientResultId)` idempotency, including identical-replay success and conflicting-replay
  rejection;
- the catalog of modes that expose leaderboards;
- the ranking metric and ranking direction for every supported mode;
- modifier-filter interpretation, row ordering, page size bounds, and cursor scope/semantics; and
- exclusion of a result that lacks the server-selected ranking metric.

`CreatedAt` is server receipt time. It is not a client-reported played-at time and does not prove
which of two tied performances happened first. This distinction matters when a client drains
results that were queued while offline.

### Client-reported

The client reports:

- `ClientResultId`;
- `ModeId`;
- `LevelId`;
- `CharacterId`;
- `ClientVersion`;
- `Platform`;
- gameplay metrics; and
- modifier flags (`Hardcore`, `TrafficEnabled`, `EnemiesEnabled`, and `SniperEnabled`).

These fields are validated for the current wire/domain rules. For example, identifiers must be
present and within their bounds, metric names and values must be valid, and a result for a
leaderboard-backed mode must include the metric selected by the server policy. Validation makes a
payload structurally acceptable; it is not gameplay verification, attestation, or anti-cheat.
`ClientResultId` is an idempotency key, not proof that the result is genuine.

## Ingestion and idempotency

The authenticated player and the client-generated result id form the idempotency key:
`(PlayerId, ClientResultId)`.

- The first valid submission creates one immutable result.
- An identical replay returns that original result, including its original `MatchResultId` and
  `CreatedAt`.
- Reusing the key with any different client-reported persisted field is a conflict and never
  overwrites the accepted result.
- The same `ClientResultId` may be used by different authenticated players because the key is
  player-scoped.

For every mode in the server leaderboard-policy catalog, each accepted result is an independent
leaderboard candidate. A player may therefore have multiple rows on the same board. There is no
personal-best-per-player collapse or replacement rule. Results for modes without a leaderboard
policy may still be accepted as immutable records, but there is no leaderboard read for those
modes.

## Board scope and filtering

A board is scoped by:

1. `ModeId`; and
2. the optional values of the four modifier filters.

`LevelId`, `CharacterId`, `Platform`, and `ClientVersion` are projected or retained as result data,
but they do not currently partition a board. Results with different values for any of those fields
can coexist on the same mode/filter board.

An omitted modifier filter means "do not constrain this modifier." A supplied `true` or `false`
means "match this value exactly." In Unity, the all-four-toggles-off state omits all four modifier
parameters and therefore requests the unfiltered mode board. If at least one toggle is on, Unity
sends the complete four-value combination, including explicit `false` values for toggles left off.

The current server-owned ranking policies are:

| Mode IDs | Metric | Direction |
| --- | --- | --- |
| 1, 15, 16, 17, 18, 19, 23, 24, 26 | `TotalPoints` | Higher wins |
| 2, 3, 4 | `ShotsMade` | Higher wins |
| 6 | `TotalDistance` | Higher wins |
| 7, 8, 9, 25 | `CompletionTimeSeconds` | Lower wins |
| 14 | `LongestStreak` | Higher wins |
| 20, 21, 22 | `EnemiesKilled` | Higher wins |

Clients receive the resolved metric and direction in the leaderboard response. They do not choose
or override either value.

## Ordering and pagination

Rows are ordered by:

1. the server-selected ranking value in the policy direction;
2. `CreatedAt` ascending; then
3. `MatchResultId` ascending.

The final two keys make equal ranking values deterministic and make keyset pagination stable,
including when multiple rows have the same server receipt timestamp. They are deterministic
database ordering keys only. In particular, earlier `CreatedAt` does not establish that the match
was played earlier.

The cursor is opaque to clients and is bound to the complete ordered-row-set scope: mode, resolved
metric, resolved direction, and all modifier-filter states. A cursor from a different scope is
rejected. Clients must return the cursor verbatim rather than decode or reconstruct it.

## Unity submission behavior

Unity adapts a locally durable score snapshot into the general-result payload, reuses that
snapshot's `Scoreid` as `ClientResultId`, and sends all supported gameplay metrics so the server can
apply its own ranking policy. When a Backend V2 session exists, the result is durably queued under
that exact player before delivery.

The queue can retain several results across an offline period or restart. It drains eligible rows
sequentially for the currently authenticated owner and reuses the same idempotency keys. A queued
result is never reassigned to another player. General-result rate limiting is intentionally not
part of this contract: a naive per-minute limit could throttle legitimate backlog recovery.

## Security boundary and future high-integrity use

The backend must not describe ordinary match results as "server-verified scores." Today it
provides authenticated ownership, validation, immutable persistence, idempotency, server-owned
ranking policy, deterministic ordering, and bounded pagination over client-reported data.

If leaderboard position later controls economic rewards, ranked matchmaking, prizes, valuable
unlocks, or another high-integrity competitive outcome, that feature requires a separate
result-verification and anti-cheat threat model. Server-issued match authority, attestation,
plausibility controls, abuse controls, and retry/backlog behavior would need to be designed against
concrete requirements. None of those mechanisms is implied or implemented by this contract.
