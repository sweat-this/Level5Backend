# Level 5 Backend V2

Clean Architecture foundation for the next generation of the Level 5 backend, built alongside
the legacy backend in the same repository. V2's first player-facing goal is asynchronous
friend-vs-friend ("correspondence") competition. This document is the architecture decision
record for the foundation slice, plus everything needed to run it locally.

## Status

Domain: player identity/tags (exact case-insensitive lookup, self-scoped display-name update -
see [Public player identity and self-update](#public-player-identity-and-self-update)), a
database-enforced, concurrency-safe friend request/friendship lifecycle (see
[Friends: requests and friendship lifecycle](#friends-requests-and-friendship-lifecycle)), a full
correspondence `VersusSeries` lifecycle (challenge → accept → play best-of-N
→ complete, server-authoritative, concurrency-safe, sealed results, Competition Protocol V1
frozen rules + named-metric attempt results - see
[Competition domain: `VersusSeries`](#competition-domain-versusseries)), a finalized, retry-safe
remote challenge API (Best-of-`{1,3,5,7}` enforcement, create idempotency, completed-series
history, accept retry-safety - see
[Remote challenge API](#remote-challenge-api-issue-10)), relational-only, paginated correspondence
list projections that never hydrate the full aggregate (see
[Correspondence list projections and pagination](#correspondence-list-projections-and-pagination-issue-21)),
and a complete authentication/session
vertical (register, login, persistent rotating refresh sessions, logout/revocation,
`AccountStatus` enforcement, `GET /api/v2/me`, centralized password policy - see
[Authentication and sessions](#authentication-and-sessions)). Not yet built: leaderboards,
richer profiles, notifications, anything in the [Non-goals](#non-goals-for-this-slice) list below.

## Why a separate solution

- **Zero coupling to legacy.** V2 has no project reference to `Level5Backend.csproj` and shares no
  code with it (see [Shared competition domain](#shared-competition-domain) for why the Unity
  versus-domain isn't shared either, yet). Architecture tests enforce this (see
  `tests/Level5.Architecture.Tests`).
- **Zero risk to the running legacy API.** The legacy project, its Postgres database
  (`level5`), Docker image, and deployment are all untouched. V2 is a second, independently
  deployable ASP.NET Core app with its own database (`level5_v2`).
- **The legacy project's SDK-style `.csproj` globs `**/*.cs` under its own directory by
  default.** Since `v2/` is nested inside the legacy project's folder, `Level5Backend.csproj`
  explicitly excludes `v2/**` (see the `<Compile Remove>` block at the top of that file) - without
  it, the legacy build would silently try to compile every V2 source file as its own. This was
  caught by actually building the legacy project after adding V2, not assumed.

## Audit of the existing repository (what shaped these decisions)

| Area | Finding | Decision |
|---|---|---|
| Runtime | .NET 10, ASP.NET Core, EF Core 10, Npgsql 10, Postgres 18 (already migrated from MySQL) | Reuse the same stack for V2 - no reason to diverge |
| Auth | Hand-rolled `TokenController` issuing JWTs, password hashed with a custom scheme, no ASP.NET Identity | V2 uses `Microsoft.AspNetCore.Identity`'s `PasswordHasher<T>` behind an `IPasswordHasher` port; JWT issuance behind `ITokenIssuer`; claims kept to `sub` only (no email/name) |
| Identity model | Sequential `int` PKs; `Signupdate`/`Lastlogin` stored as `varchar(45)` strings, not real timestamps | V2 uses UUIDv7 (`Guid.CreateVersion7()`) for all IDs and `DateTimeOffset` everywhere; no int PKs, no string timestamps. `Account` carries a minimal `AccountStatus` (`Active`/`Disabled`, defaulting `Active`) and an optional private, normalized `Email` - see [Domain boundaries](#domain-boundaries-this-slice) |
| Persistence | Scaffolded EF model 1:1 with a MySQL-derived schema (e.g. `Highscores.Difficulty` defaults hidden in `HasDefaultValueSql`) | V2's schema is derived from the domain, not reverse-engineered from a table; see [Persistence](#persistence) |
| Tests | Legacy has a single controller-level test project (`Level5Backend.Tests`), added on `dev` after this audit began; no domain or integration coverage | V2 ships with domain, application, infrastructure-integration, API-integration, and architecture tests from the start |
| Unity versus domain | Not present in this repository at all | Could not be audited here - see [Shared competition domain](#shared-competition-domain) |
| Coexistence | Single deployable app on `/api/...` | V2 is a second deployable app; its own routes are namespaced `/api/v2/...` for when/if they do share a host or gateway later |

## Dependency graph

```
Level5.Domain        (no dependencies beyond the BCL)
      ^
      |
Level5.Application   (-> Domain only; ports, use cases, DTOs)
      ^
      |
Level5.Infrastructure(-> Application, Domain; EF Core, Npgsql, JWT, password hashing)
      ^
      |
Level5.Api           (-> Application, Infrastructure; composition root, controllers)
```

Enforced by `tests/Level5.Architecture.Tests` (NetArchTest): Domain never references
Application/Infrastructure/Api/EF Core/ASP.NET Core/Unity; Application never references
Infrastructure/Api/EF Core/ASP.NET Core; no V2 assembly references the legacy `Level5Backend`
assembly.

## Domain boundaries (this slice)

- **Identity** (`Level5.Domain.Identity`): `Account`, `Username`, `AccountStatus`, `Email`,
  `AuthSession`. Purely the private authentication identity - no display name, no tag.
  `AccountStatus` is `Active` (the default for every new account) or `Disabled`, and is enforced at
  login and at refresh (a `Disabled` account can do neither - see
  [Authentication and sessions](#authentication-and-sessions)). `Email` is optional, normalized to
  a lower-invariant canonical form for case-insensitive uniqueness, and never exposed through any
  public player-facing API or DTO - only `RegisterAccountUseCase`/`AccountStore` ever see it.
  `AuthSession` is the persistent, rotating refresh session backing long-lived login - see below.
- **Players** (`Level5.Domain.Players`): `PlayerProfile`, `PlayerTag`. The public in-game
  identity - deliberately holds nothing from the private account identity (no password hash,
  email, status, or IP address). `PlayerTag` is exact-match only (`Name#1234`; grammar: 2-20
  handle characters, `#`, then a 3-6 digit discriminator - see `PlayerTag.cs`); no partial/prefix
  search, and it is **immutable** - assigned once at registration and never editable by any
  endpoint in this slice. `DisplayName` is the one mutable field on a profile
  (`PlayerProfile.ChangeDisplayName`, validated with the same trim/length rules as creation - see
  [Public player identity and self-update](#public-player-identity-and-self-update) below);
  `PlayerId`, `AccountId`, `PlayerTag`, and `CreatedAt` never change after creation.
- **Social** (`Level5.Domain.Social`): `FriendRequest` (Pending/Accepted/Declined/Cancelled, with a
  `Revision` optimistic-concurrency token) and `Friendship` (a canonically-ordered pair, so a
  unique DB index prevents a duplicate in either direction). See
  [Friends: requests and friendship lifecycle](#friends-requests-and-friendship-lifecycle).
- **Competition** (`Level5.Domain.Competition`): `VersusSeries`, the aggregate root for a
  correspondence match. See [Competition domain](#competition-domain-versusseries) below.

Deliberately not modeled yet: leaderboards, progression, matchmaking, notifications - see
[Non-goals](#non-goals-for-this-slice).

## Competition domain: `VersusSeries`

`VersusSeries` is the server-authoritative aggregate for one challenge/match between two players.

- **Frozen rules**: `SeriesFormat` (best-of-N) *and* `FrozenRules` (Competition Protocol V1's
  ruleset id/version, minimum compatible version, mode id, information policy, first-attempt
  ordering, and ordered comparison keys) are captured once at `CreateChallenge` and never change
  for that series' lifetime, even if the live ruleset catalog or future series' configuration
  changes. `FrozenRules` is resolved server-side, from `IRulesetCatalog`, never from client-supplied
  comparison keys/directions/mode data (issue #9; see
  [`v2/docs/competition-protocol/README.md`](docs/competition-protocol/README.md) §9/§15). The
  catalog itself is a single hardcoded in-memory entry (`StaticRulesetCatalog`) - deliberately
  minimal, since how it is administered/populated long-term is issue #10's concern, not #9's; #9
  only needed *some* server-owned seam so a created series is never rules-less.
- **Legal transitions only**: `PendingAcceptance -> Active -> Completed`, or
  `PendingAcceptance -> Declined/Cancelled`. Every mutating method validates the acting player and
  the current status before applying anything (`SeriesAuthorizationException`,
  `IllegalSeriesTransitionException`).
- **Optimistic concurrency**: every mutation increments `Revision`. Persistence writes with
  `WHERE id = @id AND revision = @expectedRevision`; a 0-row update means someone else moved first,
  and the use case surfaces a `409 Conflict` rather than silently overwriting (see
  `IVersusSeriesStore.TrySaveAsync`, `SeriesLookup.SaveOrThrowAsync`).
- **Idempotent by construction**: `StartAttempt`/`CompleteAttempt` check "does this attempt already
  exist / is it already completed" *before* checking whether the series is still `Active` - a
  retried request whose response was lost still returns the original result, even if that very
  completion is what just finished the series. (`VersusSeries.CompleteAttempt`,
  `VersusSeries.StartAttempt`; regression-tested in `VersusSeriesTests`.) `CompleteAttempt` also
  binds the submission to a specific `AttemptId` and compares an already-accepted result against a
  retry's incoming payload before returning early: an identical payload is the idempotent-success
  path above, but a *materially different* payload is rejected as `409`/`conflict`
  (`ConflictingAttemptResultException`) rather than silently replacing the accepted result (issue
  #11, Competition Protocol V1 §13).
- **Named, ordered comparison engine**: `GameAttempt.Result` is an `AttemptResult` - a named-metric
  bag (`ResultMetric`: `Score`, `Accuracy`, `CompletionTimeSeconds`, ...) with order-independent
  semantic equality. Round resolution (`GameRound.ResolveWinner`) walks `FrozenRules.ComparisonKeys`
  in order, skipping tied keys and deciding on the first key that differs per its
  `MetricDirection` (`HigherWins`/`LowerWins`); every key tying is a true draw. `CompleteAttempt`
  validates every comparison-key metric is present in the submitted result *before* accepting it
  (`MissingRequiredMetricException`, `400`) so a completed attempt can never end up unresolvable
  later (issue #11).
- **Sealed and OpenTarget results**: `VersusSeries.ToView(viewerId)` produces a viewer-specific
  `SeriesView`, which also carries the series' `FrozenRules` (safe to expose in full - it's the
  agreed contract both participants already know). Under `SealedAttempt`, an opponent's attempt
  shows its real `Status` (so the viewer knows they've submitted) but *every* metric of its `Result`
  stays `null` until both attempts in that game are complete - never just `Score`. Under
  `OpenTarget`, the non-first-mover cannot even `StartAttempt` until the designated first mover
  (`FrozenRules.AlternatesFirstAttempt`-aware) completes theirs
  (`OpenTargetIssuanceOrderException`, `409`); once they do, the responder's view reveals only
  `ComparisonKeys[0]` of the first mover's result - no other metric - until the round itself
  resolves, at which point the ordinary full reveal applies. All of this lives in the domain, not
  the API layer or the client (`GameRound.IsResolved`, `VersusSeries.ToRoundView`,
  `PartialRevealMetricFor`) - issue #11.
- **Non-participant = 404, not 403**: every series-scoped use case checks participation and throws
  the same `NotFoundException` a non-existent series id would produce (`SeriesLookup`), so probing
  random series ids can't be used to learn which ones are real.

## Remote challenge API (issue #10)

Hardens the challenge lifecycle already described above into a finalized, retry-safe remote
contract, behind `SeriesController` (`/api/v2/series/...`, `[Authorize]`). No second
challenge/invitation aggregate was introduced - a challenge is still just `VersusSeries` in its
`PendingAcceptance` lifecycle; this issue only hardens the API surface, idempotency, and list
coverage around the existing aggregate/store from issue #9.

- **Create contract**: `POST /api/v2/series` accepts `opponentId`, `totalGames`, `rulesetId`,
  optional `rulesetVersion`, optional `informationPolicy`, and a required `clientRequestId`.
  Everything else about the created series - `challengerId` (always the authenticated caller),
  frozen `rules`/`comparisonKeys`, `status`, `revision`, `games`/`attempts`, raw state - is
  server-owned; any such field present in the request body is silently ignored by model binding
  (`CreateChallengeDto` simply has no such property), never trusted (`CorrespondenceFlowTests`'
  overposting coverage).
- **Best-of-`{1,3,5,7}` enforcement**: remote challenge creation rejects any `totalGames` outside
  that set with `400`/`validation_failed`, even though the underlying `SeriesFormat.BestOf` domain
  validator still accepts any odd value in `[1,25]` - the narrower remote-MVP subset
  (Competition Protocol V1 §8) is enforced in `CreateChallengeUseCase`, at the application
  boundary, not in the general-purpose domain type.
- **Information policy**: optional on create. If supplied, it must equal the resolved ruleset's own
  catalog policy (`RulesetDefinition.InformationPolicy`) or the request is rejected with
  `400`/`validation_failed` - a client can request the ruleset's own policy for clarity/logging but
  can never freeze an arbitrary policy the ruleset doesn't actually use. The catalog today has one
  entry (`score-only`, `SealedAttempt`); issue #11 implements `OpenTarget` issuance-gating and
  partial-reveal projection in full at the domain layer (`VersusSeries`/`GameRound`), but selecting
  `OpenTarget` through the live remote `CreateChallenge` path still requires a catalog entry with
  that policy, which remains issue #10's ruleset-catalog-administration concern, not #11's.
- **Create idempotency**: `clientRequestId` (a client-generated GUID, required) is the retry-safety
  key, scoped to the authenticated challenger (`(ChallengerId, ClientRequestId)`, unique in
  `competitive_series` - Postgres unique indexes treat `NULL` as distinct from every other value,
  so rows without a key, e.g. from lower-level tests, don't collide). A create retried with the
  same challenger, same key, and the same opponent/format/ruleset/(resolved-or-requested) version/
  information-policy returns the originally created series unchanged; the same key reused for a
  materially different request is `409`/`conflict`. No separate fingerprint is persisted - the
  comparison is made directly against the already-persisted series' own fields
  (`CreateChallengeUseCase.EnsureMatchesExistingRequest`), since everything needed for the
  comparison is already part of the aggregate. A missing `clientRequestId` is rejected outright
  (`400`/`validation_failed`) rather than silently accepted as non-idempotent, since there is no
  established convention in this codebase yet for exempting a create endpoint from retry-safety.
  This is deliberately local to challenge creation, not a generic cross-API idempotency platform.
- **Accept retry-safety**: `VersusSeries.Accept` is idempotent for the opponent once the series is
  already `Active` (the only way to reach `Active` is that same opponent's own prior `Accept`), so
  a retried accept call (e.g. a lost HTTP response) returns the current view instead of a
  `409`/`illegal_transition`. The challenger and any non-participant are still rejected regardless
  of status (`403`), and every other terminal state (`Declined`/`Cancelled`/`Completed`) still
  conflicts. Decline/cancel were **not** made idempotent in this slice - deliberate, not an
  oversight: nothing in issue #10's required behavior calls for it, and the existing
  revision-based optimistic-concurrency conflict already gives a well-defined `409` for a repeated
  decline/cancel; extending idempotency there is deferred until a concrete need appears.
- **Completed list**: `GET /api/v2/series/completed`
  (`ListCompletedSeriesUseCase`/`IVersusSeriesStore.ListCompletedSeriesSummariesAsync` - see issue
  #21 below for the summary/pagination contract) returns the same sparse `SeriesSummaryDto` shape
  as `incoming`/`outgoing`/`active`, scoped to `SeriesStatus.Completed` only -
  `Declined`/`Cancelled` series never played out and are excluded, since this issue does not add a
  separate "all history" endpoint.
- **List isolation**: `incoming`/`outgoing`/`active`/`completed` are each scoped to the
  authenticated player only; an unrelated third player sees none of another pair's series through
  any of the four (`CorrespondenceFlowTests`).
- **Status naming**: the public API still exposes the Backend's own status names
  (`PendingAcceptance`/`Active`/`Completed`/`Declined`/`Cancelled`) verbatim, not Unity's `Invited`
  terminology - no explicit Protocol V1/API requirement demands a rename, and Competition Protocol
  V1 §3 already documents the name mapping for a future Unity adapter to consume, so renaming here
  would only create a second source of truth for that mapping.
- **Deferred, explicitly (not by omission)**: mid-series `Forfeit` is still not implemented at any
  layer (Competition Protocol V1 §20 flags this as an open decision for #10/#11) - it is out of
  this issue's required scope and is left for a future issue if a concrete need appears. Ruleset
  catalog administration (beyond the existing hardcoded `StaticRulesetCatalog`) is similarly
  out of scope here.

## Remote attempt API (issue #11)

Hardens `StartAttempt`/`CompleteAttempt` into the authoritative, retry-safe, metric-complete
attempt boundary Unity needs to launch a match and submit its result. No second attempt
aggregate/store was introduced - an attempt is still just a `GameAttempt` inside the existing
`VersusSeries`/`IVersusSeriesStore`; this issue hardens the descriptor, completion contract,
comparison engine, idempotency, concurrency, and projection around the existing aggregate.

- **`StartAttempt` descriptor contract**: `POST /api/v2/series/{seriesId}/games/{gameNumber}/attempts/start`
  returns an `AttemptDescriptorDto` derived only from the series' persisted, frozen state - never
  raw aggregate/persistence JSON, and never caller-supplied rules. It carries `seriesId`,
  `attemptId`, `gameNumber`, `playerId`, `competitionProtocolVersion`, `rulesetId`,
  `rulesetVersion`, `minimumCompatibleVersion`, `modeId`, `informationPolicy`, `totalGames`,
  `gamesToWin`, ordered `comparisonKeys` (metric + direction), and `requiredResultMetrics` (the
  same metrics, in the same order, as a flat name list). Retrying `StartAttempt` for the same
  player/game before completion returns the same `attemptId` and an identical descriptor
  (`StartAttemptUseCase`, `AttemptDescriptor`), and a backend restart/new `DbContext` between start
  and any later call does not change it (frozen rules and attempt identity are both
  persisted, not recomputed).
- **`CompleteAttempt` is bound to a specific attempt**:
  `POST /api/v2/series/{seriesId}/games/{gameNumber}/attempts/{attemptId}/complete` - the attempt
  id is part of the route, not an optional body field, so a client can never complete an attempt it
  did not start. A mismatched `attemptId` fails with `400`/`AttemptIdentityMismatchException`
  before any state is touched.
- **Protocol V1 named-metric result payload**: the request body is `{ "metrics": { "<ResultMetric
  name>": <number>, ... } }` - stable names (`Score`, `Accuracy`, `CompletionTimeSeconds`, ...),
  never a positional index. An empty payload, a numeric/unknown metric name, or duplicate aliases
  for the same canonical metric is `400`/`validation_failed` at the API boundary
  (`SeriesController.ParseResult`). `AttemptResult` rejects undefined metrics, non-finite or
  negative values, accuracy outside `0..100`, and `ShotsMade > ShotsAttempted`; a payload missing a
  metric the series' frozen `ComparisonKeys` require is `400`/`MissingRequiredMetricException` at
  the domain boundary (`VersusSeries.EnsureResultSatisfiesFrozenRules`). All checks happen before
  completion is applied, so malformed input cannot mutate the attempt and a completed attempt can
  never end up unresolvable. A result may carry additional named metrics beyond what
  `ComparisonKeys` require (e.g. a full multi-metric Unity result where only some metrics are used
  for comparison); those extra, recognized metrics are accepted and persisted, not rejected. The
  earlier score-only `{ "score": <int> }` contract has been fully replaced, not kept as a
  transitional dual path.
- **Ordered, direction-aware comparison engine**: `GameRound.ResolveWinner` walks
  `FrozenRules.ComparisonKeys` in declared order, skipping a tied key and deciding on the first key
  that differs per its `MetricDirection` (`HigherWins`/`LowerWins`); every key tying is a true
  draw. This replaced the old hard-coded, `Score`-only, higher-wins-only comparison. Consulted only
  against each attempt's own already-accepted `AttemptResult` - never the live ruleset catalog - so
  an already-started or already-resolved game's outcome can never shift under a later catalog
  change.
- **Duplicate-completion idempotency vs. conflict**: `VersusSeries.CompleteAttempt` compares an
  incoming result against an already-accepted one *before* taking the idempotent-return path -
  semantically equal (order-independent) → `200` with the original accepted state unchanged;
  materially different → `409`/`conflict` (`ConflictingAttemptResultException`), leaving the
  originally accepted result untouched. See fixtures
  [10](docs/competition-protocol/fixtures/10-identical-result-retry.json) (identical retry) and
  [11](docs/competition-protocol/fixtures/11-conflicting-result-replay.json) (conflicting replay),
  both now backend-executable.
- **Bounded reload-and-reevaluate on a lost optimistic-concurrency race**: `StartAttemptUseCase` and
  `CompleteAttemptUseCase` no longer fail outright the first time `TrySaveAsync` loses a revision
  race. Each retries up to 3 times: reload the current authoritative series, reapply the same
  domain call against the fresh state (which itself decides idempotent-success / new-mutation /
  conflict / illegal-transition), and attempt to save again. A concurrent duplicate start, both
  participants starting or completing simultaneously, and the last required completion resolving a
  round while another request is in flight all converge correctly through this path rather than
  needing last-write-wins or an unbounded retry loop. Exhausting all attempts (persistent, unusual
  contention) still surfaces `409`/`conflict`.
- **`OpenTarget` issuance gating and partial reveal**: implemented in full at the domain layer.
  `VersusSeries.StartAttempt` rejects the non-first-mover with `409`/
  `OpenTargetIssuanceOrderException` until the designated first mover
  (`FrozenRules.AlternatesFirstAttempt`-aware: pinned to the challenger, or alternating by game
  number) has completed their own attempt for that game. Once they have, `VersusSeries.ToView`
  reveals only `ComparisonKeys[0]` of the first mover's result to the responder - no other metric -
  until the round itself resolves. See [fixture
  02](docs/competition-protocol/fixtures/02-open-target-primary-only.json), now
  backend-executable at the domain level. Selecting `OpenTarget` through the live remote
  `CreateChallenge` path still needs a catalog entry with that policy (issue #10's concern).
- **Sealed projection hides every metric, not just `Score`**: `AttemptView.Result` is `null` (or,
  under `OpenTarget`, a single-key partial dictionary) until disclosure is legal - never the full
  metric dictionary with the client trusted to hide fields. Enforced at every response that can
  carry attempt results: `GetSeries`, the list endpoints' sparse summaries (which never carry
  attempt data at all), and `CompleteAttempt`'s own response.
- **Reconnect/reload behavior**: attempt identity, the frozen descriptor, an accepted completion,
  the current game/series state, and sealed/`OpenTarget` projection are all reconstructed from
  persisted state alone - none of it is cached in memory across requests, so a backend restart or a
  fresh `DbContext` between lifecycle steps is indistinguishable from the same process continuing
  (`VersusSeriesStoreTests`, `CorrespondenceFlowTests`).
- **Deferred, explicitly (not by omission)**: attempt `Abandon`/reissue (discarding a botched
  attempt for a fresh one) is not implemented - Competition Protocol V1 §12 names this as a real,
  optional feature reduction versus local Unity play, and no concrete requirement for it exists
  yet. Server-simulated gameplay, anti-cheat/result attestation, and #21's list
  projection/pagination hardening remain explicit non-goals of this issue.

## Correspondence list projections and pagination (issue #21)

Separates the four correspondence list endpoints (`incoming`/`outgoing`/`active`/`completed`)
from full `VersusSeries` aggregate reconstruction, and adds bounded pagination to all four. No new
persistence or aggregate was introduced - `IVersusSeriesStore` simply exposes a
summary/paged query per list instead of returning `IReadOnlyList<VersusSeries>`.

- **Relational-only projection, no `state_json` deserialization**: each list method
  (`ListIncomingChallengeSummariesAsync`/`ListOutgoingChallengeSummariesAsync`/
  `ListActiveSeriesSummariesAsync`/`ListCompletedSeriesSummariesAsync` on `IVersusSeriesStore`,
  implemented in `VersusSeriesStore`) projects an EF `Select` straight from `competitive_series`
  columns (`Id`, `ChallengerId`, `OpponentId`, `Status`, `CurrentGameNumber`, `TotalGames`,
  `Revision`, `CreatedAt`) into `SeriesSummary`. The generated SQL never selects `StateJson`, never
  calls `ToDomain()`, and never constructs a `VersusSeries` - so a malformed or
  unsupported-schema-version document on any row (in or out of the requested page) cannot fail an
  otherwise-valid list response (`VersusSeriesStoreTests`). Detail (`GetSeries`) and every
  state-changing command still hydrate and validate the full aggregate exactly as before, and still
  reject malformed/unsupported aggregate documents outright - this issue narrows what the *list*
  path touches, not what `FindByIdAsync`/`TrySaveAsync` are allowed to accept.
- **Bounded pagination, keyset-based**: every list endpoint takes optional `limit`/`cursor` query
  parameters and returns `{ items, limit, nextCursor }` (`SeriesSummaryPageDto`) instead of a bare
  array. `limit` defaults to 20 and is clamped (never rejected) to a maximum of 100
  (`SeriesListPaging.DefaultLimit`/`MaxLimit`) - there is no way to request an unbounded list.
  Ordering is `CreatedAt DESC, Id DESC` for `incoming`/`outgoing`/`active`, and
  `CompletedAt DESC, Id DESC` for `completed` (falling back to `CreatedAt` if `CompletedAt` were
  ever absent, which the `Completed` status invariant does not allow in practice). Keyset, not
  offset/page, was chosen because the existing ordering already has a natural, indexable
  tiebreaker (`Id`) and keyset pagination gives stable page boundaries under concurrent
  inserts/completions for free, which offset pagination does not. `cursor` is an opaque token
  (`KeysetCursor`, base64 of `"{scope}:{sortKeyTicks}:{id}"`) encoding which list query issued it
  plus the last item's sort key and id - clients must treat it as opaque and pass it back verbatim;
  a hand-edited/unparseable cursor, **or a well-formed cursor from a different list endpoint**
  (e.g. an `/outgoing` cursor replayed against `/active`), is rejected with
  `400`/`validation_failed` rather than being silently reinterpreted against the wrong sort key.
- **No aggregate-vs-summary confusion**: `IVersusSeriesStore`'s list methods return
  `PagedResult<SeriesSummary>`, never `VersusSeries` - there is no signature through which a list
  call could accidentally reach aggregate/sealed state, independent of how any implementation
  behaves. `FindByIdAsync`/`FindByIdempotencyKeyAsync`/`TrySaveAsync` are unchanged and remain the
  only aggregate-hydrating paths on the interface.

## Authentication and sessions

The complete first V2 authenticated vertical: register, login, an authenticated `GET /api/v2/me`,
persistent rotating refresh sessions, logout/revocation, `AccountStatus` enforcement, and a
centralized password policy.

- **Access tokens**: short-lived JWTs (`Jwt:AccessTokenLifetimeMinutes`, default 15 minutes),
  issued by `ITokenIssuer`/`JwtTokenIssuer`. Claims stay minimal - `sub` is the V2 `AccountId` and
  nothing else identifying (no email, username, or status) - this was already true before this
  slice and is unchanged by it.
- **Refresh sessions**: `AuthSession` (`Level5.Domain.Identity`) is a persistent, rotating
  session - stable id, owning `AccountId`, a one-way hash of the current refresh credential
  (`RefreshTokenHash`), `CreatedAt`/`ExpiresAt`/`RevokedAt`, and a `Revision` used exactly like
  `VersusSeries.Revision` for optimistic concurrency. Default lifetime is 30 days
  (`Sessions:RefreshTokenLifetimeDays`), extended on every successful rotation.
- **Refresh credential security**: the raw refresh secret is 256 bits from
  `RandomNumberGenerator` (a CSPRNG), base64url-encoded (`RefreshTokenGenerator`) - never derived
  from any account/session id. Only a SHA-256 hex digest of it is ever persisted or looked up
  (`IAuthSessionStore.FindByRefreshTokenHashAsync`); the raw value is returned exactly once, in the
  register/login/refresh response body, and is never logged. This is deliberately not the same
  port as `IPasswordHasher`: a refresh token is already a high-entropy random secret, not
  low-entropy human input, so a fast one-way hash is the right tool for exact-match lookup rather
  than a slow, salted password-hashing algorithm.
- **Rotation and replay prevention**: `POST /api/v2/auth/refresh` looks up the session by the
  presented credential's hash, checks `AuthSession.CanRefresh` (not revoked, not expired) and that
  the owning account is still `Active`, then calls `AuthSession.Rotate` and persists it via
  `IAuthSessionStore.TrySaveAsync(session, expectedRevision, ...)` - a conditional
  `UPDATE ... WHERE id = @id AND revision = @expectedRevision`, the same pattern
  `VersusSeriesStore.TrySaveAsync` uses. A 0-row update (someone already rotated or revoked this
  exact session) is treated as an invalid/replayed credential, not a generic conflict - so at most
  one of several concurrent refresh requests presenting the same token can ever succeed, and the
  old credential is unusable immediately afterward. Covered by
  `AuthSessionStoreTests.TrySaveAsync_rotation_wins_on_the_correct_revision_and_loses_on_a_stale_one`
  against real Postgres.
- **One stable failure contract for refresh**: unknown, expired, revoked, already-rotated
  (replayed), and "owned by a non-`Active` account" all surface as the same `401` /
  `invalid_refresh_token` - a client can never learn which one occurred.
- **Logout/revocation** (`POST /api/v2/auth/logout`): revokes the session owning the presented
  refresh credential (`AuthSession.Revoke`), so it can never be exchanged for another access token
  again. Does not require a valid access token (logout must work even after the access token has
  already expired - the caller authenticates by possessing the refresh credential itself, not a
  bearer token). Idempotent: an unknown, already-rotated, or already-revoked credential is treated
  as a no-op success, since the caller's intent ("this credential should not work anymore") is
  already satisfied; a genuine infrastructure failure still propagates as an error rather than
  being swallowed into a fake success.
- **Access-token behavior after logout/revocation (explicit policy)**: revoking a refresh session
  stops it from minting *future* access tokens; it does **not** invalidate an access token already
  issued from it, which remains valid until its own short expiry. There is deliberately no
  database lookup on the bearer-authenticated request path to check session state - only the
  refresh/logout endpoints touch `auth_sessions`. If a product requirement later demands immediate
  revocation of already-issued access tokens, that is a stateful-JWT-validation decision to make
  deliberately (e.g. a token denylist), not something to introduce silently.
- **Disabled accounts**: checked in `LoginUseCase` (after password verification, so a disabled
  account takes the same password-hashing cost as an active one and isn't distinguishable by
  response timing) and in `RefreshSessionUseCase`. A `Disabled` account can neither log in nor
  refresh; both fail with the same generic contract (`invalid_credentials` / `invalid_refresh_token`)
  used for every other failure mode in that endpoint. There is no account-status-changing endpoint
  in this slice (still a non-goal) - integration tests flip `Status` directly in the database, the
  same way a future admin tool eventually would.
- **`GET /api/v2/me`**: the private authenticated-account view - `AccountId`, `Username`, `Status`,
  `PlayerId`, `CreatedAt`. Deliberately separate from `GET /api/v2/players/me` (the public in-game
  `PlayerProfile`). Claim parsing (`sub` -> `AccountId`) lives in exactly one place,
  `Level5.Api.Security.ICurrentAccountAccessor`, shared by this endpoint and
  `CurrentPlayerProvider` so it is never duplicated; `GetCurrentAccountUseCase` itself takes a
  plain `AccountId` and has no dependency on `HttpContext`/`ClaimsPrincipal`/ASP.NET Core types.
- **Password policy**: centralized behind `IPasswordPolicy`, enforced in `RegisterAccountUseCase`
  before any account/profile/session state is created. No established policy existed anywhere in
  this repository or its issues, so `PasswordPolicy` (`Level5.Infrastructure.Identity`) applies
  conservative, NIST 800-63B-aligned defaults: a minimum length of 8 characters and a maximum of
  128 (bounding hashing cost on pathological input, not a security control), and deliberately *no*
  forced character-class complexity (uppercase/digit/symbol) - current guidance treats those rules
  as pushing users toward predictable patterns without materially improving resistance to
  guessing, and no concrete V2 product requirement calls for them. Revisit only when a concrete
  requirement (e.g. a breached-password check) exists; do not add complexity rules speculatively.
- **Registration/login durability**: both use one `IUnitOfWork.SaveChangesAsync` call committing
  account + player profile + initial/new `AuthSession` together, and only issue the access token
  (and return the raw refresh credential) after that commit succeeds - so a partial failure never
  hands out credentials for state that was not actually persisted.
- **Configuration**: `Sessions:RefreshTokenLifetimeDays` (default 30, range 1-365), validated at
  startup the same way `Jwt:*` is (`ValidateDataAnnotations().ValidateOnStart()`), set via
  `dotnet user-secrets` locally or `Sessions__RefreshTokenLifetimeDays` in production. No new
  required configuration beyond this - refresh-token hashing and password-policy limits are fixed
  constants, not configuration surface, since nothing in this slice needs them tunable.
- **Rate limiting**: `/api/v2/auth/refresh` and `/api/v2/auth/logout` sit under the same
  `AuthController`/`AuthPolicy` rate limiter as register/login (see
  [Local development](#local-development) below) - refresh is as much a credential-guessing/replay
  surface as login, and logout shares the policy rather than getting a separate, more restrictive
  one it doesn't need.

## Public player identity and self-update

The public in-game identity surface, behind `PlayersController` (`/api/v2/players/...`,
`[Authorize]`):

- **`GET /api/v2/players/me`**: the caller's own `PlayerId`, derived from the authenticated
  account via `ICurrentPlayerProvider`/`ICurrentAccountAccessor` - never from anything the client
  supplies.
- **`GET /api/v2/players/by-tag/{tag}`**: exact, case-insensitive lookup only
  (`ResolvePlayerByTagUseCase` -> `IPlayerProfileStore.FindByTagAsync`) - no partial/prefix search.
  Because the tag grammar includes `#`, and `#` is the URL fragment delimiter, a client must
  percent-encode it in the path (`Patrick#4821` -> `Patrick%234821`, e.g.
  `Uri.EscapeDataString(tag)`); this is exercised end-to-end against the real ASP.NET routing
  stack in `PlayersFlowTests`. A well-formed but unknown tag returns `404`/`not_found`; a
  malformed tag (grammar violation) returns `400`/the domain validation code - never a
  partial/fuzzy match either way.
- **`PATCH /api/v2/players/me`**: self-scoped display-name update
  (`UpdateMyPlayerProfileUseCase`). The profile mutated is always the one owned by the
  authenticated account (`ICurrentAccountAccessor.GetCurrentAccountId()` ->
  `IPlayerProfileStore.FindByAccountIdAsync`) - the request body
  (`UpdatePlayerProfileRequestDto`) carries only `DisplayName`, so there is no `PlayerId`,
  `AccountId`, or `Tag` field a client could supply to redirect the update to another player's
  profile or change stable identity; any such fields in a raw request body are simply ignored by
  model binding, not merely rejected by authorization logic. `PlayerTag` is immutable and cannot
  be changed through this or any endpoint. Returns the same safe public representation as
  `GET .../by-tag/{tag}` (`PlayerId`, `DisplayName`, `Tag`). An account with no profile is treated
  as `NotFoundException`, the same application-error convention used elsewhere, rather than an
  internal exception leaking to the client.
- **Public/private boundary**: every response from `PlayersController` exposes only `PlayerId`,
  `DisplayName`, and `Tag` - never `AccountId`, `Username`, `Email`, `AccountStatus`,
  `PasswordHash`, or any `AuthSession`/token data. That private data lives behind the separate
  `GET /api/v2/me` contract (`AccountController`) and is never merged into a players response.
  Asserted directly against the serialized JSON (not just DTO shape) in `PlayersFlowTests`.

## Friends: requests and friendship lifecycle

The social graph behind correspondence challenges, behind `FriendsController`
(`/api/v2/friends/...`, `[Authorize]`). Modeled entirely by `FriendRequest` and `Friendship`
(`Level5.Domain.Social`).

- **Lifecycle**: a `FriendRequest` is created `Pending` (`SendFriendRequestUseCase`) and can only
  leave that state once - `Pending -> Accepted/Declined/Cancelled`. Only the recipient may accept
  or decline; only the sender may cancel; a request cannot friend a player to themselves
  (`InvalidFriendRequestException`). There is deliberately no expiration or additional state -
  a resolved request simply never transitions again
  (`IllegalFriendRequestTransitionException`). Accepting produces a `Friendship`
  (`FriendRequest.Accept` returns it), written in the same `SaveChanges` call as the request's own
  `Accepted` status - see Atomicity below.
- **Authorization is always the authenticated player**: every use case takes an `ActingPlayerId`
  resolved from `ICurrentPlayerProvider`, never a request-body field - a caller cannot accept,
  decline, or cancel on another player's behalf, and gets the same `403`/`FriendRequestAuthorizationException`
  a stranger to the request would (`FriendsFlowTests`).
- **Duplicate/conflicting requests - two layers of defense**: `SendFriendRequestUseCase` checks for
  an existing pending request between the pair (either direction) and for an existing friendship
  before inserting, returning a friendly `409`/`ConflictException`. That check alone cannot close a
  race between two concurrent sends, so the database enforces the same invariant independently: two
  unique partial indexes on `friend_requests` (`WHERE "Status" = 'Pending'`), one on
  `(FromPlayerId, ToPlayerId)` for a same-direction duplicate and a second on the canonical
  `(LowerPlayerId, UpperPlayerId)` pair (mirroring `Friendship`'s own canonical ordering) so a
  crossed `A->B` / `B->A` race is blocked too - a unique index on the directional columns alone
  cannot express that. A unique-violation from either index is translated to `ConflictException`
  by `ConflictTranslatingSave`, never leaked as raw SQL. Covered against real Postgres in
  `FriendshipStoreConstraintTests` (including a genuinely concurrent crossed-send race across two
  separate `DbContext`s/connections).
- **Optimistic concurrency on `FriendRequest`**: `Revision` (`long`) starts at `0` and increments by
  exactly one on a successful `Accept`/`Decline`/`Cancel`; a rejected transition (bad actor, wrong
  status) leaves it untouched. It is configured as an EF Core concurrency token
  (`FriendRequestRow.Revision`, `IsConcurrencyToken()`), so `SaveChanges` conditions the `UPDATE` on
  the revision that was originally loaded and throws `DbUpdateConcurrencyException` if another
  transition already committed first - translated to the same `409`/`ConflictException` as a unique
  violation (`ConflictTranslatingSave`). There is no automatic reload-and-retry: the losing request
  simply surfaces the conflict, exactly like `VersusSeries.Revision` /
  `IVersusSeriesStore.TrySaveAsync` for competition state. Covered against real Postgres in
  `FriendRequestConcurrencyTests` - a stale transition loaded before a winner committed, and the
  `Accept vs Decline` / `Accept vs Cancel` / `Accept vs Accept` races, each asserting the final
  persisted state has no mixed outcome (e.g. never `Declined` *and* a `Friendship`).
- **Atomicity of Accept**: `AcceptFriendRequestUseCase` stages the request's `Accepted`
  status/`Revision` update and the new `Friendship` insert on the same tracked `DbContext`, then
  commits both in one `SaveChangesAsync` call - a single implicit transaction. If the concurrency
  check on the request fails, EF Core rolls back the whole batch, so the `Friendship` insert never
  survives a losing `Accept`; there is no intermediate state where the request is `Accepted` but no
  `Friendship` exists, or vice versa.
- **List isolation**: `ListIncomingFriendRequestsUseCase`/`ListOutgoingFriendRequestsUseCase`/`ListFriendsUseCase`
  each scope their query to the authenticated player only (`ToPlayerId`/`FromPlayerId`/participant
  respectively) - a third player never sees another pair's requests or friendship through any of
  these endpoints (`FriendsFlowTests`).
- **Remove-friend**: `RemoveFriendUseCase` deletes the `Friendship` row; removing a pair that isn't
  actually friends is `404`/`NotFoundException` (not idempotent) - either participant may initiate
  a removal. After removal, the pair disappears from both players' `GET /api/v2/friends` and no
  longer satisfies the accepted-friend prerequisite for a *new* `CreateChallengeUseCase` call -
  already-created `VersusSeries` from before the removal are untouched (`FriendsFlowTests`).
- **Friend-required challenges unchanged**: `CreateChallengeUseCase` still requires an accepted
  `Friendship` (`IFriendshipStore.AreFriendsAsync`) before creating a `VersusSeries`, returning
  `403`/`FriendshipRequiredException` otherwise - this slice hardens the friends system underneath
  that check without changing its contract.

## Persistence

`competitive_series` is a deliberate relational/JSONB hybrid, chosen after considering (and
rejecting) two alternatives:

- **Fully normalized** (separate tables for series/games/attempts): would require 3+ tables and
  joins for state that has no independent query patterns of its own (nobody queries "attempts with
  score > X" directly) - premature normalization for this slice.
- **Fully document-based** (no relational columns at all): would make the actual query patterns
  (list my incoming challenges, list my active series) require scanning/parsing JSON instead of
  simple indexed `WHERE` clauses.

The hybrid: `challenger_id`, `opponent_id`, `status`, `total_games`, `current_game_number`,
`revision`, and timestamps are real indexed columns; the nested per-game attempt state
(`GameRound`/`GameAttempt`) *and* the series' frozen Protocol V1 rules snapshot are a single
`jsonb` column (`state_json`), versioned with an embedded `schema_version` field. Nothing else was
promoted to a relational column for #9 - there is no current query need to filter/index on
ruleset id, comparison keys, or information policy, so they stay inside the JSONB document per the
issue's "don't prematurely normalize" constraint. See `VersusSeriesRow`, `VersusSeriesStateJson`,
`VersusSeriesStore`.

Issue #10 adds one more relational column: `client_request_id` (nullable `uuid`), the create
idempotency key, unique together with `challenger_id`. It is metadata about *how* a series was
created (a retry-safety concern), not business state, so it lives on the row rather than on the
domain aggregate itself - `VersusSeries` has no concept of it.

**Schema version is enforced on read, not decorative.** `VersusSeriesStore` reads
`schemaVersion` from the JSONB document before committing to a full deserialize; any value other
than the current version (`2`) throws `UnsupportedSeriesSchemaVersionException` rather than being
silently misread as the current shape (falls through to a `500`, since it indicates a genuine
server-side data problem, not a client mistake). Schema v2 is the Competition Protocol V1 shape
(adds the frozen `FrozenRules` snapshot; replaces each attempt's single integer score with a
named-metric `Result` bag) added by issue #9. **Schema v1 rows (pre-#9) are rejected outright, not
migrated**: a v1 row has no `RulesetId`/`ComparisonKeys`/`InformationPolicy` at all, so there is
nothing to fabricate a `FrozenRules` snapshot from, and - per the migration-history note below -
no V2 environment has ever run with real user data, so recreating the (disposable) affected series
is safe and strictly simpler than a migration that would have to invent data it doesn't have.
Persisted metric/comparison-key/information-policy identifiers are validated against the current
enums on read too (`CorruptSeriesStateException` on an unrecognized value) - an unknown identifier
never silently becomes valid domain data.

**Resolved blast radius (issue #21):** the four list methods on `IVersusSeriesStore`
(`ListIncomingChallengeSummariesAsync`/`ListOutgoingChallengeSummariesAsync`/
`ListActiveSeriesSummariesAsync`/`ListCompletedSeriesSummariesAsync`) used to deserialize every
matching row eagerly (`rows.Select(ToDomain)`) with no per-row isolation - one row that failed
schema-version or identifier validation failed the *entire* list call for that player. They now
project straight from relational columns and never touch `StateJson`/`ToDomain()` at all (see
[Correspondence list projections and pagination](#correspondence-list-projections-and-pagination-issue-21)),
so a malformed or unsupported-schema-version row can no longer affect any list result, sibling or
own. `FindByIdAsync` still has, and is still meant to have, the narrower blast radius of only the
requested series - it must keep failing loudly on invalid aggregate state, which the "fail loudly,
never migrate silently" choice above still governs for detail/command paths.

Everything else (`accounts`, `auth_sessions`, `player_profiles`, `friend_requests`, `friendships`)
is plain relational EF Core, mapped through dedicated `*Row` types in `Level5.Infrastructure.Persistence.Rows`
so the domain entities never need EF-friendly parameterless constructors or public setters. Each
`*Store` maps explicitly between rows and domain aggregates - `Rehydrate` factory methods on the
domain side handle reconstruction without re-validating already-persisted state as if it were
new input.

**`accounts` <-> `player_profiles`**: `player_profiles.account_id` is a real, database-enforced
foreign key to `accounts.id` (`ON DELETE RESTRICT` - this slice defines the relationship, not
account-deletion semantics, so a stray delete fails loudly instead of silently cascading or
orphaning data), plus the pre-existing unique index on `account_id` that keeps it to one profile
per account. `accounts.username_canonical` is uniquely indexed as before; `accounts.email_canonical`
is also uniquely indexed, but since email is optional and Postgres unique indexes treat `NULL` as
distinct from every other value, any number of accounts with no email can coexist - uniqueness only
applies once an email is actually set.

**`accounts` <-> `auth_sessions`**: `auth_sessions.account_id` is a real, database-enforced foreign
key to `accounts.id` (`ON DELETE RESTRICT`, for the same reason as `player_profiles` above - there
is no account-deletion feature yet, so a stray delete must fail loudly). `auth_sessions.refresh_token_hash`
is uniquely indexed - the database itself enforces that two sessions never share a stored refresh
credential representation - and `revision` is the optimistic-concurrency token
`AuthSessionStore.TrySaveAsync` conditions its writes on, exactly like `competitive_series.revision`.

**`player_profiles` <-> social/correspondence (issue #20)**: every persisted player reference in
`friend_requests`, `friendships`, and `competitive_series` is a real, database-enforced foreign key
to `player_profiles.id`, all `ON DELETE RESTRICT`:

- `friend_requests.from_player_id`, `to_player_id`, `lower_player_id`, `upper_player_id`
- `friendships.lower_player_id`, `upper_player_id`
- `competitive_series.challenger_id`, `opponent_id`, `winner_id` (nullable - a series has no
  winner until it completes - but FK-constrained whenever it is set)

Before this, the database allowed orphan player references in these tables; application code
always supplied valid ids, but nothing at the schema level stopped a migration, repair script, or
administrative tool from inserting one. `RESTRICT` (not `CASCADE`) throughout, same reasoning as
`accounts` above: this slice defines referential integrity, not a player-deletion feature, so a
stray delete of a referenced profile fails loudly instead of silently erasing social/correspondence
history.

Migrations are **not** applied automatically at startup (see `Program.cs` - there is no
`Database.Migrate()` call). Apply them explicitly:

```powershell
dotnet ef database update --project src/Level5.Infrastructure --startup-project src/Level5.Api
```

### Migration history

The migration chain was rebaselined a second time for issue #20 (**Harden Persistence Integrity
and Rebaseline Pre-Production Schema**), squashing the four migrations that previously existed
(`InitialCreate`, `AddAuthSessions`, `HardenFriendshipInvariants`, `AddSeriesCreateIdempotency`)
into a single new `InitialCreate` that produces the same final schema plus the player-profile
foreign keys above, with the `HardenFriendshipInvariants` canonicalization hazard (below) removed
rather than carried forward.

This was verified safe the same way the first rebaseline (noted below) was: no V2 deploy/CD
workflow exists (`.github/workflows/ci.yml`'s `build-v2` job builds and tests V2, it does not
deploy it or run migrations against any persistent database), and the only databases that have ever
run any V2 migration are local dev instances and ephemeral Testcontainers instances spun up and
torn down per test run - both disposable, with no durable V2 data anywhere to preserve. Going
forward, once V2 is actually deployed anywhere, migrations must be additive, not rebaselined - the
same rule the first rebaseline established and this one continues to follow.

The pre-rebaseline chain, for reference (no longer present in `Migrations/`): `InitialCreate`
(itself already a rebaseline of an earlier version, folding in `AccountStatus`, `Email`, and the
`accounts`/`player_profiles` foreign key) → `AddAuthSessions` (added `auth_sessions`) →
`HardenFriendshipInvariants` (added `friend_requests.Revision` and the canonical
`LowerPlayerId`/`UpperPlayerId` columns/index) → `AddSeriesCreateIdempotency` (added
`competitive_series.ClientRequestId`). `HardenFriendshipInvariants` backfilled any pre-existing
row's canonical pair using Postgres's native `uuid` comparison (`LEAST`/`GREATEST`), which is not
guaranteed to agree with `Friendship.Order`'s `.NET` `Guid.CompareTo` ordering that the application
itself uses - safe only because nothing had ever been backfilled by it (see above), but a hazard
worth removing from the durable path rather than leaving for a future migration to trip over. The
rebaselined `InitialCreate` needs no such backfill: `LowerPlayerId`/`UpperPlayerId` are populated
exclusively by `Friendship.Order` from the moment the columns exist, so no SQL-level
canonicalization - correct or otherwise - is needed at all.

## Shared competition domain

The prompt driving the original V2 foundation work assumed a pure-C# Unity versus/correspondence
domain (`VersusSeries`, `Attempt`, `IVersusSeriesRepository`, etc.) already existed in the Level 5
Unity project, and asked for an audit of whether it could become a shared framework-independent
assembly used by both Unity and this backend. At that time the Unity project was not part of this
repository and could not be inspected, so Backend V2's `VersusSeries` was built as a new,
independent, server-authoritative implementation with a follow-up flagged: audit the Unity domain
once available, and decide shared-code-vs-contract from real source on both sides.

That audit is done — see
[`v2/docs/competition-protocol/README.md`](docs/competition-protocol/README.md) (issue #8) for the
full compatibility matrix, decision rationale, and Competition Protocol V1 specification. The
decision: **keep the two implementations separate** (a physically shared assembly was rejected on
four concrete, source-confirmed blockers - not on a general DRY objection - see that document's
§4) and **govern remote correspondence through a versioned protocol plus canonical compatibility
fixtures** (`v2/docs/competition-protocol/fixtures/`), not a shared runtime domain. Remote
competition is command/query based against `Level5.Api`'s existing DTOs/use cases, never a
networked implementation of a repository interface that uploads a whole aggregate. The audit also
surfaces the concrete semantic gaps (result metrics, ruleset/mode identity, `OpenTarget`
information policy, retry-vs-conflict handling, and others) that issues #9-#11 must close before
remote correspondence can support Unity's actual shipped competitive semantics - see that
document's §19-§21 for exactly what each of those issues needs to do.

## Preserved / replaced / deferred

**Preserved as-is (legacy, untouched):** the entire legacy `Level5Backend.csproj` - controllers,
models, migrations, `level5` database, Docker image, deployment. Legacy and V2 can run
side-by-side; nothing here changes legacy behavior.

**Replaced (V2 does this differently, not a straight port):** authentication (ASP.NET Core
Identity password hashing + minimal-claim JWTs + rotating refresh sessions vs. the legacy custom
scheme), identifiers (UUIDv7 vs. sequential ints), timestamps (`DateTimeOffset` vs. formatted
strings), persistence model (domain-derived hybrid schema vs. a scaffolded 1:1 table mapping).

**Deferred (explicitly out of scope for this slice):** highscores/leaderboards, `ServerStats`,
`ServerMessages`, `UserReport`, admin/dev endpoints (including any account-status-changing
endpoint - disabling an account is still a direct-database operation, see
[Authentication and sessions](#authentication-and-sessions)), email verification, password reset,
account recovery, MFA, OAuth/social login, device/session-management UI, refresh-token
families/reuse-compromise tracking, a token denylist, public profile fields beyond display
name/tag/avatar-id, any of the [non-goals](#non-goals-for-this-slice) below.

## Non-goals for this slice

Matchmaking, realtime/WebSocket play, notifications, chat, clans/guilds, economy, progression or
leaderboard migration, full legacy database migration, event sourcing, CQRS/MediatR ceremony,
GraphQL, microservices/Kubernetes/Redis/Kafka/RabbitMQ/SignalR. None of these are needed to
establish the architecture; adding them now would be scope creep against an unproven foundation.

## Suggested follow-up slices (in order)

1. **Unity V2 networking boundary** - `IApiTransport` + per-area API clients
   (`IAuthApiClient`, `IFriendsApiClient`, `ICorrespondenceApiClient`) backed by
   `UnityWebRequest`, replacing ad hoc calls into the legacy `APIHelper`.
2. **Account administration** - an endpoint to disable/re-enable an account, once there's an actual
   admin surface; today `AccountStatus` is enforced but only ever changed directly in the database.
3. **Public player profile fields** - avatar id, richer display data, once there's a UI that needs
   them.
4. ~~Unity versus-domain audit~~ - done, see [Shared competition domain](#shared-competition-domain).
   Its output (`v2/docs/competition-protocol/`) defines the required follow-up slices:
   ~~correspondence persistence (frozen rules, named metrics, information-policy projection)~~ -
   done (issue #9, see [Competition domain: `VersusSeries`](#competition-domain-versusseries) and
   [Persistence](#persistence) above) - ~~remote challenge API (finalized `CreateChallenge`
   contract, Best-of-`{1,3,5,7}` subset enforcement, create idempotency, completed-series list,
   accept retry-safety)~~ - done (issue #10, see
   [Remote challenge API](#remote-challenge-api-issue-10) above; ruleset catalog administration and
   a mid-series forfeit command remain explicitly deferred, not silently dropped) - ~~then the
   remote attempt API (ordered/direction-aware comparison engine, `OpenTarget` issuance gating +
   partial reveal, identical-vs-conflicting resubmission handling, multi-metric submission)~~ -
   done (issue #11, see [Remote attempt API](#remote-attempt-api-issue-11) above; attempt
   `Abandon`/reissue and #21's list projection/pagination hardening remain explicitly deferred).
5. **Observability** - correlation/request IDs are not yet wired into `Level5.Api`'s middleware
   pipeline; add them alongside structured logging once there's a log aggregation target to send
   them to.
6. **Leaderboards/highscores** - only after the correspondence vertical slice is proven in
   production; do not migrate the legacy `Highscores` table wholesale.

## Local development

### One-time setup

```powershell
./v2/scripts/setup-local-dev.ps1
```

Starts the same local Postgres container the legacy backend uses
(`docker-compose.local-db.yml`, one level up), creates the separate `level5_v2` database inside it
if it doesn't already exist, generates a JWT signing key, stores both via `dotnet user-secrets`
(never in a file in this repo), and applies EF Core migrations. Safe to re-run.

Then:

```powershell
cd v2/src/Level5.Api
dotnet run
```

- Swagger UI: `http://localhost:5000/swagger` (Development environment only)
- Liveness: `GET /health/live` - always healthy if the process is up, no dependency checks
- Readiness: `GET /health/ready` - checks Postgres connectivity

### Running the tests

```powershell
cd v2
dotnet test Level5BackendV2.sln
```

Requires Docker to be running - `Level5.Infrastructure.IntegrationTests` and
`Level5.Api.IntegrationTests` spin up ephemeral Postgres containers via Testcontainers (real
Postgres semantics: JSONB, partial unique indexes, `ExecuteUpdate`-based optimistic concurrency -
deliberately not SQLite, since none of that behavior would be representative there).

### Running behind a reverse proxy

The auth rate limiter partitions by the caller's IP, so if this service is deployed behind a
proxy or load balancer it needs to be told which forwarders to trust - otherwise every request
appears to come from the proxy and shares one bucket. List the proxy's address (or CIDR range)
in configuration:

```json
"ForwardedHeaders": {
  "KnownProxies": [ "10.0.0.7" ],
  "KnownNetworks": [ "10.0.0.0/8" ]
}
```

Leave these empty when there is no proxy. Anything listed here is trusted to dictate the client
IP via `X-Forwarded-For`, so don't add ranges wider than the actual infrastructure.

### Adding a migration

```powershell
cd v2
$env:ConnectionStrings__DefaultConnection = "Host=localhost;Port=5432;Database=level5_v2;Username=level5;Password=localdevpassword"
dotnet ef migrations add <Name> --project src/Level5.Infrastructure --startup-project src/Level5.Api -o Persistence/Migrations
```

(`Level5V2DbContextFactory` lets this run without booting the full Api host, so it doesn't fail on
missing runtime-only secrets like `Jwt:Key`.)

## Known deviations from the brief

- `dotnet new sln` in the installed .NET 10 SDK defaults to the new `.slnx` XML format; this was
  regenerated with `--format sln` to match the requested `Level5BackendV2.sln` filename.
- An extra test project, `tests/Level5.Architecture.Tests`, was added beyond the four listed in
  the suggested tree - the brief's own "Architecture tests" section requires them, and they don't
  fit cleanly inside any of the other four without giving a test project a compile-time reference
  to all four source projects for reflection purposes alone.
- `Testcontainers.PostgreSql` (a test-only dependency) transitively pulls in `SSH.NET`, which NuGet
  flags with a high-severity advisory (`GHSA-q939-rpr3-3284`) for its SSH-tunnel-to-Docker feature.
  It is not used - these tests connect to a local Docker daemon directly - and it is not a
  production dependency. Left as-is pending an upstream Testcontainers update; worth revisiting.
