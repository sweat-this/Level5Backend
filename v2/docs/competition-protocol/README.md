# Competition Protocol V1 — Unity/Backend compatibility decision

**Issue:** `sweat-this/Level5Backend#8` — Competition Domain Compatibility Audit: Shared Core vs
Contract Fixtures.

**Status:** Decision made and recorded. This is an architecture/contract document, not a
production rewrite of either competition system. It does not implement issues #9-#11.

## 1. Repositories and commits audited

| Repository | Branch | SHA |
|---|---|---|
| `sweat-this/Level5Backend` | `dev` | `5687850adb1aed00b3cbb4ac1edb7b2551243a26` |
| `sweat-this/level5` (Unity) | `dev` | `4719b3a2d3b1e2d07097dee0b3897d049fd58de1` |

Both were fetched fresh from `origin/dev` immediately before this audit; local `dev` matched
`origin/dev` exactly on both repositories. Source (not either repository's docs) is authoritative
wherever the two disagree — every claim below cites the file and, where useful, the line range it
came from.

This is the first time both repositories have been available together for this audit. The
Backend's own `v2/README.md` already flagged the gap this document closes (see
["Shared competition domain"](../../README.md#shared-competition-domain) before this change):
*"once the Unity repository is available, audit its versus domain for (a) genuine
framework-independence, (b) compatibility of its result-comparison/rules-freezing semantics with
this implementation, and (c) migration risk before deciding whether extraction is still
worthwhile."* That audit is this document.

## 2. The five questions, answered

1. **Should Unity and Backend physically share competition-domain code?** No — see [§4](#4-shared-runtime-domain-option-a--why-it-is-rejected-now).
2. **What protocol defines semantic compatibility?** Competition Protocol V1 — see [§6](#6-competition-protocol-v1).
3. **Which current semantic differences must downstream Backend issues resolve?** See the [compatibility matrix](#3-compatibility-matrix) and [§10](#10-required-changes-for-9-10-11).
4. **What is authoritative on the server vs. interpreted locally by Unity?** See [§5](#5-authority-boundaries).
5. **How will compatibility be proven over time?** Canonical fixtures + tests on both sides — see [§8](#8-canonical-compatibility-fixtures) and [§9](#9-tests-added-and-run).

## 3. Compatibility matrix

"Unity" cites `Assets/Level5/Core/Versus/**` and `Assets/Scripts/versus/**` at the audited SHA.
"Backend" cites `v2/src/Level5.Domain/Competition/**` and neighboring Application/Infrastructure
files at the audited SHA.

| Concept | Unity | Backend | Status | Required adapter / downstream change |
|---|---|---|---|---|
| Player/participant identity | `ParticipantId` — opaque `string` wrapper, no shape constraint (`VersusIdentity.cs`) | `PlayerId` — `readonly record struct(Guid Value)`, `Guid.CreateVersion7()` (`Ids/PlayerId.cs`) | **Compatible** with adapter | Protocol id is an opaque string; Backend adapter does `Guid.ToString()` / `Guid.Parse(...)`, Unity adapter treats it as already-opaque. Neither side changes its internal type ([§7](#7-protocol-identity-semantics)). |
| Series identity | `SeriesId` — opaque string, e.g. `"series-" + Guid.NewGuid("N")` (`VersusServices.cs:44-57`) | `VersusSeriesId(Guid)` | **Compatible** with adapter | Same pattern as above. |
| Attempt identity | `AttemptId` — opaque string; used as the anti-replay key on `Attempt` | `AttemptId(Guid)` — doc comment: *"used as the idempotency key for CompleteAttempt retries"* (`Ids/AttemptId.cs`) | **Compatible** with adapter | Same opaque-id pattern. Both sides already use this id as their replay/idempotency key — the *purpose* matches even though the *mechanism* differs ([§13](#9-frozen-rule-semantics) vs [§9 retry](#13-result-retryreplay-behavior)). |
| Series statuses | `Invited, Active, Completed, Declined, Forfeited` (`VersusSeries.cs:14-30`, 5 values) | `PendingAcceptance, Active, Completed, Declined, Cancelled` (`SeriesStatus.cs`, 5 values) | **Partial** | Names differ 1:1 for 4 of 5 (`Invited`≈`PendingAcceptance`, `Active`=`Active`, `Completed`=`Completed`, `Declined`=`Declined`); protocol maps these by meaning, not name. The 5th does **not** correspond: Unity's 5th state is `Forfeited` (mid-series abandonment); the Backend's is `Cancelled` (pre-acceptance withdrawal by the **challenger**, which Unity has no dedicated status for — Unity's `Decline()` is a single, actor-unchecked method covering both directions). See "Forfeit semantics" and "Invitation/accept/decline/cancel semantics" rows below — this is two separate gaps, not one. |
| Game/round status | `VersusGameStatus`: `Pending, Active, Resolved, Forfeited, Cancelled` (`VersusGame.cs:12-26`) | No explicit enum — `GameRound.IsResolved` is computed from both attempts' status (`GameRound.cs:28-29`); rounds are created lazily, never explicitly "Pending" | **Partial** | Backend needs no explicit per-game status for the MVP (it only ever has one active game at a time, created on demand) — Unity's richer per-game status supports a full pre-built playlist view Backend doesn't need to mirror. Not a blocker; document as intentional narrowing, not an oversight. |
| Attempt statuses | `AttemptState`: `Created, Ready, Started, Completed, Abandoned` (`Attempt.cs:13-29`, 5 values) | `AttemptStatus`: `NotStarted, Completed` (`AttemptStatus.cs`, 2 values) | **Incompatible** (narrower on Backend) | Backend collapses "issued / ready / mid-flight" into one `NotStarted` bucket and has **no `Abandoned`** at all. Required decision for #11 — see [§12](#12-reconcile-attempt-lifecycle-semantics). |
| Invitation/accept/decline/cancel semantics | `Accept()`/`Decline()` — **no actor parameter, no authorization check** in the domain method itself (`VersusSeries.cs:304-316`) | `Accept(actingPlayerId,...)` requires `OpponentId`; `Decline(actingPlayerId,...)` requires `OpponentId`; `Cancel(actingPlayerId,...)` requires `ChallengerId` — all throw `SeriesAuthorizationException` otherwise | **Partial** | The Backend is stricter and is authoritative for the remote flow — this is correct and required (server-authoritative per [§4](#4-shared-runtime-domain-option-a--why-it-is-rejected-now) of the issue). Unity's local domain has no equivalent split between "challenger withdraws" and "opponent declines" — that distinction only needs to exist in the remote command vocabulary (`CancelChallenge` vs `DeclineChallenge`, [§8](#8-define-challenge-semantics) below), not in Unity's local aggregate. |
| Forfeit semantics | `VersusSeries.Forfeit(participantId, clock)` — `Active → Forfeited`; if called while `Invited`, redirects to `Decline()` (`VersusSeries.cs:412-435`) | **No forfeit method exists on `VersusSeries` at all.** An active series can only ever reach `Completed` by every game either being won, drawn on, or exhausted. | **Incompatible** | Real gap, not a naming difference. If mid-series forfeit is required for the remote MVP, it is **net-new Backend domain work** (a new `SeriesStatus` value, a new aggregate method, new authorization rule, new API endpoint) — out of scope for #8, required decision for #9/#10/#11 (see [§12](#12-reconcile-attempt-lifecycle-semantics) and [§18](#10-required-changes-for-9-10-11)). |
| Best-of-N support | `SeriesFormat` — **hard-validated to exactly 1, 3, 5, or 7**; the exception message says so literally: *"a series must be a best of 1, 3, 5 or 7"* (`SeriesFormat.cs:29-38`); `SeriesFormat.All()` returns exactly those four | `SeriesFormat.BestOf(int)` — any **odd** value in **[1, 25]** (`SeriesFormat.cs:21-34`) | **Partial** (Backend is a superset) | Remote MVP must restrict to the Unity-supported subset `{1,3,5,7}` regardless of what the Backend's own validator allows today — see [§7 required subset](#7-define-the-supported-remote-competition-subset). This is a **required rejection at the remote challenge boundary**, not a Backend domain rewrite (`SeriesFormat.BestOf` keeps its existing [1,25] range for any other future callers; only the remote-challenge use case narrows it). |
| Draws | Game-level (`GameOutcomeKind.Draw`) and series-level (`SeriesOutcomeKind.Draw`), each with a constructor invariant that a `Draw` carries no winner (`VersusGame.cs`, `VersusSeries.cs:33-86`) | Game-level (`GameRound.WinnerId == null` on equal scores) and series-level (`CompleteWith(null, now)` when the playlist is exhausted without a majority) (`GameRound.cs:44-47`, `VersusSeries.cs:256-264`) | **Compatible** | Implemented completely independently, same outcome shape. No adapter needed beyond the identity mapping. Locked by [fixture 06](fixtures/06-true-draw.json). |
| Early termination | `VersusSeries.Advance` completes as soon as `RequiredWins` is reached; remaining games stay `Pending` and are never activated (`VersusSeries.cs:480-525`, tested by `BestOf3EndsAtTwoNilAndNeverActivatesGameThree`) | `AdvanceAfterRoundResolved` completes as soon as `GamesToWin` is reached; later games are never created (`VersusSeries.cs:239-268`, tested by `Winning_enough_games_completes_the_series`) | **Compatible** | Same clinch-and-stop behavior, independently implemented. Locked by [fixture 07](fixtures/07-best-of-3-early-termination.json). |
| Ruleset identity | `RulesetId` (opaque string) + `Version` (int) per `CompetitiveRuleset`, one ruleset **per game** in the series playlist (`SeriesSnapshot.Games`) — a series can mix modes across games (`Level5VersusCorrespondenceTests.APlaylistCanMixDifferentModesAcrossTheSeries`) | **No concept of a ruleset at all.** `SeriesFormat` (game count) is the only "rules" concept in the Backend. | **Incompatible** | Central gap. Required for #9 (persist a frozen ruleset reference) and #10 (resolve/freeze it server-side at creation) — see [§9](#9-frozen-rule-semantics). |
| Ruleset versioning | `CompetitiveRuleset.Version` (int) + `MinimumCompatibleVersion` (int, default 1); `CanPlayVersion(v) => v >= MinimumCompatibleVersion && v <= Version` (`CompetitiveRuleset.cs:200-221`) | None. | **Incompatible** | Required for #9/#10 — see [fixture 09](fixtures/09-unsupported-ruleset-version.json). |
| Minimum-compatible version | Per-ruleset field, checked by `VersusSeriesValidator.ValidatePlayable` at **attempt-issue time**, not series-load time (`VersusSeriesValidator.cs:108-137`) | None. | **Incompatible** | See above; the *timing* (creation-time rejection, not issue-time) is also a deliberate protocol choice — see [§8](#8-define-challenge-semantics). |
| Game mode identity | `GameModeId` (from `Level5.Core.Match`), one per `CompetitiveRuleset` (`CompetitiveRuleset.cs:189`) | None. | **Incompatible** | Backend needs to carry an opaque mode reference alongside the ruleset reference so Unity can reconstruct the right `MatchConfiguration` on attempt launch — see [§14](#14-define-remote-attempt-launch-semantics). Backend does **not** need to understand what the mode *means*. |
| Asynchronous capability | `VersusMode.OnlineRealtime` exists in the enum but is explicitly rejected: *"real-time online series are not implemented yet"* (`VersusSeriesValidator.cs:42-47`, tested by `RealTimeOnlineIsRefusedByNameRatherThanHalfWorking`) | No realtime concept exists at all; every series is inherently asynchronous (no timeout/expiry field, no live-session concept) (`v2/README.md:3-5`) | **Compatible** | Remote MVP = asynchronous-only on both sides, by construction. Nothing to reconcile. |
| Result metrics | `AttemptResult` — positional `float[8]` indexed by the `AttemptMetric` enum (`Score, ShotsMade, ShotsAttempted, Accuracy, CompletionTimeSeconds, LongestStreak, TotalDistance, BonusPoints`) (`AttemptResult.cs`, `CompetitiveRuleset.cs:46-71`) | `Score` — a single non-negative `int` (`Score.cs`) | **Incompatible** | Central gap alongside ruleset identity. Required for #11 — see [§10](#10-define-attempt-result-semantics). |
| Comparison-key ordering | `CompetitiveRuleset.ComparisonKeys` — an **ordered** array of `(AttemptMetric, MetricDirection)`, evaluated in order with fallthrough-on-tie (`CompetitiveRuleset.cs:162-184`) | None — `GameRound.WinnerId` does one hard-coded `>` comparison on `Score.Value` (`GameRound.cs:41-49`) | **Incompatible** | Required for #11 — see [fixture 05](fixtures/05-ordered-multi-key-tiebreak.json). |
| Higher-wins / lower-wins semantics | `MetricDirection.HigherWins \| LowerWins`, per comparison key (`CompetitiveRuleset.cs:74-78`) | Hard-coded higher-wins only; `Score.cs` doc comment: *"Higher is better; comparison policy lives on GameRound"* | **Incompatible** | Required for #11 — see [fixture 04](fixtures/04-lower-is-better-result.json). |
| Tie-breaks | Ordered `ComparisonKeys` scan, skip-on-tie, decide-on-first-difference, `0` (draw) if every key ties (`CompetitiveRuleset.Compare`, `CompetitiveRuleset.cs:230-262`) | None (a tie on the single `Score` metric is immediately a round-level draw; no secondary key) | **Incompatible** | Same gap as comparison-key ordering. |
| Frozen rules | `SeriesSnapshot` — an array of **full immutable `CompetitiveRuleset` copies**, one per game, captured at series creation; the live catalog is consulted only to check *whether this build can still play these versions*, never to resolve an active game (`SeriesSnapshot.cs`, doc comment lines 20-23) | `SeriesFormat` (`TotalGames`/`GamesToWin`) captured once at `CreateChallenge` and never mutated — but that is the **entire** frozen payload; no ruleset, no comparison keys, no information policy is frozen because none of those concepts exist yet | **Partial** (same *pattern*, far narrower *payload*) | Required for #9 — see [§9](#9-frozen-rule-semantics). The freeze-at-creation *discipline* itself already matches; only the *content* needs to grow. |
| SealedAttempt | `InformationPolicy.SealedAttempt` — opponent `AttemptState` always visible; opponent `AttemptResult` (every metric) hidden until the game is `Resolved`/`Forfeited` (`VersusGame.cs:12-26`, `VersusGame.ViewFor`, `VersusGame.cs:441-481`) | Same behavior, independently implemented and hard-coded as the *only* policy: `ToRoundView` always reveals the viewer's own score, reveals the opponent's score only `if (round.IsResolved)`, and always reveals the opponent attempt's `Status` (`VersusSeries.cs:203-224`) | **Compatible** | The one concept both sides converged on identically without sharing a line of code — good evidence *for* the contract-not-code approach: two independent implementations arrived at the same wire-visible behavior because the *semantic* was specified (even if only informally) the same way both times. Locked by [fixture 01](fixtures/01-sealed-attempt-one-completed.json). |
| OpenTarget | `InformationPolicy.OpenTarget` — before the first-mover (`SeriesSnapshot.FirstAttemptParticipantIndex`) completes, the other participant cannot even start (`VersusGame.CanIssueTo`, `VersusGame.cs:226-261`); once they do, the responder sees **only** `Ruleset.PrimaryMetric` (`ComparisonKeys[0].Metric`) of the completed result, nothing else (`VersusGame.ViewFor`, comment: *"Handing over the whole result would leak accuracy, shot count and completion time"*) | **Does not exist.** No issuance-order gating of any kind; both participants may `StartAttempt`/`CompleteAttempt` for the current game in any order or interleaving. | **Incompatible** | Required for #9 (freeze the policy + first-attempt ordering), #10 (challenge-time policy selection), #11 (issuance gating + partial-target projection) — see [§11](#11-define-information-policy-semantics) and [fixture 02](fixtures/02-open-target-primary-only.json). |
| First-attempt ordering | `SeriesSnapshot.FirstAttemptParticipantIndex(gameIndex) = AlternatesFirstAttempt ? gameIndex % 2 : 0`, frozen per game at creation, enforced by `VersusGame.CanIssueTo` (`SeriesSnapshot.cs:93-96`) | No concept — order of `StartAttempt` calls is unconstrained | **Incompatible** | Same required work as OpenTarget, above; this *is* the OpenTarget enforcement mechanism. |
| Opponent-result visibility | See SealedAttempt/OpenTarget rows. | See SealedAttempt row; no OpenTarget equivalent. | **Partial** | — |
| Attempt issue/start/abandon/complete lifecycle | `Issue→Created`, `MarkReady→Ready`, `Start→Started`, `Complete→Completed` (rejects if already `Completed` or `Abandoned`), `Abandon→Abandoned` ("a new attempt may be issued in its place") (`Attempt.cs:99-277`) | `Start` creates `NotStarted` directly (idempotent — a second `StartAttempt` for the same player/game returns the existing attempt); `Complete→Completed` (idempotent no-op if already `Completed`); **no abandon path** | **Incompatible** (narrower, not just different) | Backend's idempotent `StartAttempt`/`CompleteAttempt` already cover "reconnect and resume the same attempt." **Missing**: any way to discard a botched attempt and get a fresh one. Required explicit decision for #11 — see [§12](#12-reconcile-attempt-lifecycle-semantics). |
| Duplicate result submission | `Attempt.Complete` **always throws** on a second completion, identical payload or not: *"a completed attempt cannot be resubmitted"* (`Attempt.cs:221-257`, tested by `SubmittingTheSameFinishedTurnTwiceIsRefused`) | `GameAttempt.Complete` / `VersusSeries.CompleteAttempt` **always silently keeps the first result** on a second completion, identical payload or not — no comparison against the new payload, no error, no distinction from a genuine conflict (`GameAttempt.cs:42-52`, `VersusSeries.cs:155-184`, tested by `CompleteAttempt_twice_is_idempotent_and_keeps_the_original_result`, which submits `50` then `999` and asserts **both** calls return `50`) | **Incompatible** | Neither side implements the protocol's required distinction (identical-retry succeeds idempotently; conflicting-replay is rejected) — see [§13](#13-result-retryreplay-behavior) and [fixtures 10](fixtures/10-identical-result-retry.json)/[11](fixtures/11-conflicting-result-replay.json). This is a **required Backend change for #11**, confirmed by a test added in this issue that pins today's actual (non-compliant) behavior — see [§9](#9-tests-added-and-run). |
| Retry/idempotency behavior | Anti-replay only (rejects all resubmission); no HTTP-retry concept since gameplay is local | `AttemptId`/`VersusSeriesId` idempotency is explicit design intent (doc comments say so directly); `TrySaveAsync` returns `false` (not an exception) on a lost optimistic-concurrency race, and callers turn that into `ConflictException` → HTTP 409 (`IVersusSeriesStore.cs:18-23`, `SeriesLookup.cs:29-37`) | **Partial** | Backend's concurrency-conflict handling (409 on a lost race between two *different* requests) is unrelated to, and does not fix, the identical-vs-conflicting-resubmission gap above — those are two different mechanisms and only one of them (concurrency) is solid today. |
| Timestamps | `CreatedAtUtc` (series); `IssuedAtUtc`/`StartedAtUtc`/`CompletedAtUtc` (attempt); `ResolvedAtUtc` (game/series result) — ISO-8601 `"o"` strings on the wire | `CreatedAt`/`UpdatedAt`/`CompletedAt` (series, `DateTimeOffset`); `StartedAt`/`CompletedAt` (attempt) — no distinct `AcceptedAt` on either side | **Compatible** with adapter | Field-name mapping only; no semantic gap. |
| Serialization/versioning | Two existing, currently-unenforced version fields: `VersusSeriesDocument.documentVersion` (persistence-document shape) and `SeriesSnapshot.FormatVersion`/`CurrentFormatVersion` (frozen-snapshot shape) (`VersusSeriesDocument.cs:26,34`, `SeriesSnapshot.cs:24`) | One existing, currently-unenforced version field: `VersusSeriesStateJson.SchemaVersion` (JSONB payload shape) (`VersusSeriesStateJson.cs`); HTTP API versioned separately via the `/api/v2` route prefix | **Compatible** (confirms the need for a 4th, independent axis) | None of these three fields is a wire/protocol version — all three are internal persistence-format versions, on their own release cadence, unenforced today. This confirms issue #8 §5's requirement: `CompetitionProtocolVersion` must be a genuinely new, independent axis, not reuse of any of these. See [§6](#6-competition-protocol-v1). |

## 4. Shared runtime domain (Option A) — why it is rejected now

The issue's gate for Option A is: *"acceptable only if it can be extracted without creating
disproportionate migration/coupling."* Four independent, concrete blockers were found in the
audited source — any one of them alone would be a real cost; together they make extraction
disproportionate to a "should we share code" decision that the contract approach below can answer
without paying any of them.

1. **`Level5.Core.Versus` is not actually framework-independent today, contrary to what its own
   tests claim.** `CompetitiveRulesetDefinition : ScriptableObject` (authoring asset,
   `[CreateAssetMenu]`, `[SerializeField]`) and `VersusSeriesSerializer`/
   `InMemoryVersusSeriesRepository` (`UnityEngine.JsonUtility.ToJson`/`FromJson`) all live inside
   the `Level5.Core.Versus` namespace and reference `UnityEngine` directly. Unity's own
   architecture tests (`Level5VersusArchitectureTests.TheDomainHasNoSceneDependencies`,
   `TheDomainHasNoNetworkingAndNoFileSystem`) regex-scan for `MonoBehaviour`/`GameObject`/
   `UnityWebRequest`/`System.IO` etc. but **do not** check for `ScriptableObject` or
   `JsonUtility`, so they pass green despite this coupling. A shared assembly would need this
   split out first, and the fact that it wasn't caught by the domain's own ratchets is itself a
   finding worth fixing independently of this issue.
2. **A hard runtime-compatibility blocker, not just a style difference.** The Backend's
   `PlayerId`/`VersusSeriesId`/`AttemptId` all call `Guid.CreateVersion7()`
   (`v2/src/Level5.Domain/Ids/*.cs`), a **.NET 9+ BCL API**. Unity's scripting runtime does not
   ship that API. Sharing these exact types is not merely undesirable, it does not compile today
   under Unity's scripting backend.
3. **The identifier *representations* are incompatible by design on both sides, and the issue
   explicitly forbids changing either to match the other** (issue #8 §6: don't migrate Unity to
   Guid-backed ids, don't migrate the Backend to string-backed ids). Unity's ids are opaque,
   locally-generated strings with no canonical shape beyond `RulesetId.IsWellFormed()`; the
   Backend's ids are `Guid`-backed value types wired into EF Core column mapping and Postgres
   indexes (`competitive_series` table, indexed on `(OpponentId, Status)`/`(ChallengerId,
   Status)`). A shared type would force one side to change; the issue rules that out, and rightly
   so — it would ripple into Unity's save-file format and the Backend's already-migrated schema.
4. **The two domains already encode materially different, already-shipped semantics** — not
   hypothetical future differences, but the *current, tested, production* shape of each side: 5
   Backend `SeriesStatus` values vs. 5 different Unity ones (only 4 of 5 correspond by meaning);
   2 Backend `AttemptStatus` values vs. 5 Unity ones; a single hard-coded-higher-wins `int Score`
   vs. an 8-metric ordered-comparison-key engine; zero ruleset/mode/frozen-rules concept vs. a
   full per-game frozen `CompetitiveRuleset` array; no forfeit path vs. a dedicated `Forfeit`
   transition. Sharing one assembly would require rewriting the Backend's already-tested,
   already-deployed `VersusSeries`/`Score`/`AttemptStatus` (with real Postgres migrations behind
   them) to adopt Unity's richer shape wholesale, simultaneously with equivalent work on the Unity
   side to drop its Unity-specific persistence coupling — a full rewrite of both systems at once,
   which is exactly the "disproportionate migration/coupling" the issue's Option A gate excludes.

None of this means shared code could never become appropriate later — if the semantic gaps in
[the matrix](#3-compatibility-matrix) above are closed by #9-#11 and the two domains converge
substantially, extraction could be revisited on its own merits then. It is rejected **now**, on
**today's source**, for the four concrete reasons above — not on a general DRY objection.

## 5. Authority boundaries

**Local Unity competition is untouched.** `VersusMatchCoordinator → IVersusSeriesRepository →
FileVersusSeriesRepository / InMemoryVersusSeriesRepository` continues to serve local/offline
play exactly as it does today. Nothing in this issue changes that stack, its persistence format,
or its tests.

**Remote competition is command/query based, never a networked `IVersusSeriesRepository`.** A
remote store must **not** implement `Save(VersusSeries)`/`Load(SeriesId)` against a server — that
would require the client to construct and the server to accept a whole authoritative aggregate,
which is exactly the client-authoritative shape the issue prohibits. Instead:

```
Unity
  ↓
typed remote correspondence client (new — not IVersusSeriesRepository)
  ↓
HTTPS command/query (CreateChallenge, AcceptChallenge, DeclineChallenge, CancelChallenge,
                      StartAttempt, CompleteAttempt, GetSeries, ListSeries)
  ↓
Backend application use case (Level5.Application.Competition.*UseCase — these already exist
                               and already follow exactly this shape)
  ↓
Backend loads the authoritative VersusSeries (IVersusSeriesStore.FindByIdAsync)
  ↓
Backend performs the legal transition (a VersusSeries domain method call)
  ↓
Backend persists (IVersusSeriesStore.TrySaveAsync, optimistic-concurrency checked)
  ↓
participant-safe response (SeriesView / ToView(viewerId) — already sealed today for Score)
```

The Backend's existing `SeriesController` + `*UseCase` classes already implement this exact shape
for the one concept they currently know about (best-of-N, single `Score`). Nothing here asks for
a new architecture on the Backend side — it asks for the payload each step carries to grow (see
[§6](#6-competition-protocol-v1) onward), not for the shape of the pipeline to change.

**The server owns:** series lifecycle, participants, frozen rules, attempt identity, accepted
result, game resolution, series resolution, information-policy projection. **The client never
uploads a replacement aggregate** — every mutation is a narrow command (`CompleteAttempt(seriesId,
gameNumber, result)`, never `SaveSeries(wholeSeriesObject)`).

## 6. Competition Protocol V1

```
CompetitionProtocolVersion = 1
```

This is a **new, independent axis**, distinct from all four other version concepts already in
either codebase (confirmed to be genuinely separate today — see the "Serialization/versioning"
matrix row):

| Axis | Owner | Changes when… |
|---|---|---|
| `CompetitionProtocolVersion` | this document | the *meaning* of a command/query, or a field the wire contract requires, changes in a way old and new clients/servers would disagree about (e.g. adding a new required command, changing what "conflict" means, changing the information-policy projection rules) |
| HTTP API version (`/api/v2`) | `Level5.Api` | the whole V2 API's routing/versioning strategy changes — unrelated to competition semantics specifically |
| Unity `SeriesSnapshot.FormatVersion` / `VersusSeriesDocument.documentVersion` | Unity | the *shape of Unity's own local save file* changes — never crosses the wire |
| `VersusSeriesStateJson.SchemaVersion` | Backend | the *shape of the Backend's own JSONB persistence payload* changes — never crosses the wire |
| Ruleset version (`RulesetId` + `RulesetVersion`, [§9](#9-frozen-rule-semantics)) | whoever owns ruleset content | the *rules of a specific game mode* change — independent of how the protocol carries a ruleset reference |

A change requires bumping `CompetitionProtocolVersion` when it changes what a **conforming
implementation on the other side must do differently** to interoperate (new required command,
new required field, changed rejection semantics, changed projection rule). It does **not** need a
bump for: adding an optional field either side may ignore, a ruleset's content or version
changing, either side's internal persistence schema changing, or the HTTP route version changing.
The protocol defines only what the client and server must agree on — it does not require every
internal field of either domain to appear on the wire (issue #8 §5).

## 7. Protocol identity semantics

Protocol ids are **opaque strings**. Neither side changes its internal identifier type:

```
Backend PlayerId(Guid)   <-->  protocol player id (string)  <-->  Unity ParticipantId (string)
Backend VersusSeriesId   <-->  protocol series id (string)  <-->  Unity SeriesId
Backend AttemptId(Guid)  <-->  protocol attempt id (string) <-->  Unity AttemptId
```

The Backend adapter formats/parses `Guid.ToString()` / `Guid.Parse(...)`. The Unity adapter treats
the protocol id as already being in its own opaque-string shape (no reformatting needed — Unity's
`ParticipantId`/`SeriesId`/`AttemptId` impose no format beyond non-empty/trim, per
`VersusIdentity.cs`). Neither `Guid.CreateVersion7()` nor Unity's `"kind-" + Guid.NewGuid("N")`
convention is part of the protocol — both are internal generation strategies, free to change
independently, exactly as the "Unity IDs: opaque strings / Backend IDs: UUID-backed" line in the
issue anticipated (confirmed true by the audit, not assumed).

## 8. Supported remote competition subset (MVP)

Per issue #8 §7, the remote MVP is scoped to what Unity's *shipped* competitive semantics already
support, not the Backend's current prototype allowances:

- `VersusMode.Asynchronous` only (both sides already agree — see the matrix's "Asynchronous
  capability" row).
- `BestOf1`, `BestOf3`, `BestOf5`, `BestOf7` only. The Backend's `SeriesFormat.BestOf` continues to
  accept any odd value in [1,25] as a domain type (no reason to break that general-purpose
  validator), but the **remote challenge use case** (#10) must reject any requested format outside
  `{1,3,5,7}` — Best-of-9 through Best-of-25 are Backend-only surplus, not a real Level 5
  competitive format, and must not be exposed remotely.
- `SealedAttempt` and `OpenTarget` only (both are named Unity concepts with real, tested behavior;
  no other information policy exists on either side to support).

Rulesets selectable remotely must be ones Unity's own catalog marks as
`VersusCapability.Asynchronous`-eligible (per `CompetitiveRulesetCatalog.Supporting(...)`,
mirroring the existing `EveryGameSupports(VersusCapability)` check Unity already runs on a
snapshot) — a ruleset built only for local same-device or realtime play must not be selectable in
a remote challenge, even once #10 exists.

## 9. Frozen-rule semantics

The minimum immutable state the server must preserve at series creation, so a later balance/content
change cannot retroactively alter an active series, mirrors what Unity's `SeriesSnapshot` already
freezes per game — **not** Unity's persistence classes verbatim (`VersusSeriesDocument`,
`VersusRulesetDocument` etc. stay Unity-only, per the issue's explicit non-goal):

**Required, confirmed by actual use in Unity's `SeriesSnapshot`/`CompetitiveRuleset`:**
- `RulesetId`
- `RulesetVersion`
- `ModeId` (opaque to the Backend — carried, not interpreted)
- ordered `ComparisonKeys` (metric + direction, in order)
- `InformationPolicy` (`SealedAttempt` | `OpenTarget`)
- first-attempt ordering, when `InformationPolicy == OpenTarget` (Unity's
  `AlternatesFirstAttempt`/`FirstAttemptParticipantIndex(gameIndex)`)

**Determined by actual use, per issue #8 §9's instruction not to copy blindly:**
- `MinimumCompatibleVersion` — **required**. Unity already uses it to refuse an aged-out ruleset
  version at attempt-issue time (`CompetitiveRuleset.CanPlayVersion`); the remote protocol needs
  the equivalent check at *creation* time instead (server-side, since the server resolves the
  ruleset — see [§8](#8-define-challenge-semantics) below), which requires knowing the minimum
  compatible version to compare against. See [fixture 09](fixtures/09-unsupported-ruleset-version.json).
- `Capabilities` — **not required for the protocol payload itself**. Capabilities
  (`VersusCapability.Asynchronous`, `.OnlineRealtime`, ...) gate *which rulesets may be selected
  remotely at all* ([§8](#8-supported-remote-competition-subset) above); once a ruleset is
  selected and frozen, the frozen snapshot does not need to re-carry its capability set — a
  completed remote series' correctness never depends on re-checking capabilities after creation.
- A **persistence schema-version field is required, but on the Backend's own frozen-rules storage
  representation, not on the wire** — this is #9's own concern (`VersusSeriesStateJson` already
  has exactly this pattern with `SchemaVersion`); it is independent of
  `CompetitionProtocolVersion` (see the table in [§6](#6-competition-protocol-v1)).

Consulting a live ruleset catalog to resolve or re-score an **already-started** game is explicitly
disallowed (issue #8 §9) — once frozen, a series' rules never change, matching Unity's own
documented invariant (`SeriesSnapshot.cs` doc comment: *"the live catalog is consulted for exactly
one thing... whether this build can still play these versions"*, never to resolve an active game).

#9 owns the final persistence representation; this section only fixes what must be *semantically*
preserved, not the column/JSONB shape.

## 10. Attempt-result semantics

**Why the Backend's current integer-only `Score` is not sufficient**, confirmed by source, not by
assumption: Unity's `AttemptResult` carries up to 8 named metrics
(`Score, ShotsMade, ShotsAttempted, Accuracy, CompletionTimeSeconds, LongestStreak, TotalDistance,
BonusPoints`) and `CompetitiveRuleset.Compare` evaluates an **ordered list** of
`(metric, direction)` comparison keys with tie-break fallthrough — a shipped Unity ruleset
(`DefaultCompetitiveRulesets.Contest`) already uses `Highest(Score) → Lowest(CompletionTimeSeconds)
→ Highest(Accuracy)`, a scenario the Backend's single non-negative `int` literally cannot
represent, let alone compare correctly. This is not a hypothetical future need; it is what Unity's
*shipped* rulesets already do.

**Protocol representation required:** a set of **stable, named** protocol metrics (`Score`,
`CompletionTimeSeconds`, `Accuracy`, ...; see [the fixture schema](fixtures/SCHEMA.md)) plus an
ordered list of `(metric, direction)` comparison keys, submitted or resolved per attempt. Naming
metrics rather than relying on either side's enum **ordinal** value is required specifically
because Unity's own domain comment says never to renumber `AttemptMetric` (*"the numeric value
indexes the stored metric array"*, `CompetitiveRuleset.cs:44`) — ordinals are a private storage
detail on the Unity side, not a stable cross-system contract, and the Backend has no ordinal
scheme of its own to align with anyway.

Required directions, confirmed against Unity's actual shipped rulesets: `Score → HigherWins`,
`CompletionTimeSeconds → LowerWins`, `Accuracy → HigherWins`, plus ordered tie-breaks (fixture
[05](fixtures/05-ordered-multi-key-tiebreak.json)).

Unity may keep its positional `float[8]` array for local persistence unchanged — **that storage
representation is not the remote protocol**; the adapter converts to/from named metrics only at
the network boundary. **#11 owns the full comparison engine** (this document only specifies the
semantics it must satisfy, via the fixtures — it does not implement `Compare()` for the Backend).

## 11. Information-policy semantics

**SealedAttempt** (already correctly implemented by the Backend today, independently — see the
matrix's SealedAttempt row and [fixture 01](fixtures/01-sealed-attempt-one-completed.json)):
before both attempts resolve, opponent attempt *state* may be visible but opponent *result
metrics* must not be; after resolution, both permitted results become visible.

**OpenTarget** (not implemented by the Backend at all today — required for #9/#10/#11):
before the designated first participant finishes, the responder cannot begin (issuance-time
rejection, mirroring `VersusGame.CanIssueTo`); after the first participant finishes, the responder
may see **only** the primary comparison metric (`ComparisonKeys[0]`) of that result — no other
metric; after both complete, full permitted results may be revealed. First-attempt ordering is
selected per game at series-freeze time and may alternate by game index
(`SeriesSnapshot.AlternatesFirstAttempt`) or stay pinned to one participant — the protocol carries
whichever was chosen as part of the frozen rules ([§9](#9-frozen-rule-semantics)).

**The Backend must enforce these projections itself** — `ToView`/`SeriesView` must never include a
hidden metric in the payload at all, relying on Unity's UI to simply not render it is explicitly
disallowed by the issue and would defeat the entire point of a server-authoritative
information policy. This is exactly how the Backend already treats `SealedAttempt` today
(`ToRoundView` sets `Score = null`, it does not send `77` and trust the client to hide it) — #11
must extend that same "never construct the payload with hidden data in it" discipline to the
richer metric set and to `OpenTarget`'s partial reveal.

## 12. Reconcile attempt lifecycle semantics

Confirmed lifecycle gap: Unity's 5-state `Created/Ready/Started/Completed/Abandoned` vs. the
Backend's 2-state `NotStarted/Completed`. Required behaviors for the remote MVP, and which of
Unity's states each maps to:

| Required remote behavior | Unity state(s) it corresponds to | Backend today |
|---|---|---|
| Issued/outstanding attempt | `Created`/`Ready` | `NotStarted` (created by `StartAttempt`) |
| Gameplay launch | `Started` | still `NotStarted` (Backend does not track a distinct "in flight" state) |
| Completion | `Completed` | `Completed` |
| Reconnect/resume | idempotent re-issue | already correct: `StartAttempt` is idempotent, returns the same `AttemptId` |
| Opponent progress visibility | opponent `AttemptState` exposed pre-reveal | already correct: `AttemptStatus` is exposed on the opponent's view pre-reveal |
| Retry safety | anti-replay on `Complete` | idempotent-vs-conflicting distinction **not yet correct** — see [§13](#13-result-retryreplay-behavior) |

**`Abandoned` is explicitly not required for the initial remote MVP** — stated here, not silently
dropped: the Backend's idempotent `StartAttempt`/`CompleteAttempt` already give a client a way to
resume the *same* attempt after a crash/disconnect, which covers "I got interrupted, let me pick
up where I left off." What `Abandoned` covers that this does **not** replace is "I don't like this
run, throw it away and give me a fresh attempt" — Unity's domain explicitly supports discarding and
reissuing (`AttemptState.Abandoned` doc comment: *"a new attempt may be issued in its place"*); the
Backend remote MVP has no equivalent, meaning a botched remote attempt cannot currently be
discarded and retried with a new `AttemptId` — only resumed as the same one. This is a real,
named feature reduction versus local play, not an oversight — #11 must decide whether the MVP
needs it or can defer it.

## 13. Result retry/replay behavior

The protocol distinguishes, and **neither side currently implements the distinction**:

```
same AttemptId + semantically identical result payload  → idempotent success
same AttemptId + different result payload                → conflict / reject
```

"Semantically identical" means equal under the canonical protocol result representation
([§10](#10-attempt-result-semantics) — named metrics, not raw bytes), so e.g. metric key order in
a JSON body must not affect the comparison.

- **Unity today:** `Attempt.Complete` always throws on any second completion, identical payload or
  not (`Attempt.cs:221-257`). It does not need the distinction locally, because a correspondence
  turn is only ever submitted once from the device that played it — retries are an HTTP transport
  concern the *client adapter* must absorb (e.g. cache the last accepted response and short-circuit
  a resend before calling into local domain logic a second time), not something Unity's own
  `VersusGame`/`VersusSeries` domain needs to solve.
- **Backend today:** confirmed, by a test added in this issue
  (`Conflicting_result_replay_is_not_yet_rejected_known_gap_for_issue_11`, see [§9](#9-tests-added-and-run)),
  that `CompleteAttempt` does **not** make this distinction either — it treats *any* second
  completion (identical or different) as "already done, return the original," with no comparison
  against the incoming payload. Submitting `50` then `999` for the same attempt returns `50` both
  times, silently, with no error — this both prevents replaying a better run (good) but does *not*
  surface that a conflicting resubmission occurred (required, per this section).

**Required for #11:** `CompleteAttempt` must compare the incoming result against the stored one
*before* taking the idempotent-return path — equal → fixture 10's success path; different → fixture
11's conflict-rejected path (HTTP 409, not a silent 200). See
[fixture 10](fixtures/10-identical-result-retry.json) and
[fixture 11](fixtures/11-conflicting-result-replay.json).

## 14. Remote attempt launch semantics

The minimum fields #11 will need to reconstruct ordinary Level 5 gameplay from a remote attempt
descriptor, following Unity's existing `MatchRequest`/`MatchConfigurationBuilder`/
`MatchConfiguration` chain (`Assets/Scripts/versus/VersusLauncher.cs:103-109`) — **not** a separate
"remote gameplay" path:

```
Remote Attempt Descriptor { AttemptId, RulesetId, RulesetVersion, ModeId, [frozen rules needed
                             to reconstruct the exact match Unity would build for a local attempt
                             under this ruleset/mode] }
    ↓ (Unity adapter reads this, resolves RulesetId/Version against its own CompetitiveRulesetCatalog
       the same way it already does for local play, per CompetitiveRuleset.CanPlayVersion)
MatchRequest(ruleset.ModeId, ...)
    ↓
MatchConfigurationBuilder.Build(request)
    ↓
MatchConfiguration
```

The audit could not fully enumerate every field `MatchRequest`/`MatchConfiguration` need beyond
`ModeId` without going deeper into `Assets/Level5/Core/Match/**` than this issue's scope covers
(that file tree was audited only for its `Versus` coupling points, per issue #8's file list) — per
the issue's explicit instruction, this document does **not** finalize a production DTO here.
**#11 owns the production API contract** and must re-audit `MatchRequest`/`MatchConfigurationBuilder`
directly before finalizing the descriptor shape.

## 15. Define challenge semantics

A remote challenge identifies: `Opponent` (protocol player id), `RulesetId` (client may *request*
a known ruleset identity — see below), `SeriesFormat` (one of `{1,3,5,7}`, [§8](#8-define-challenge-semantics)),
`InformationPolicy` (`SealedAttempt` | `OpenTarget`).

**The server resolves and freezes; the client never dictates.** The client may name a ruleset it
wants (e.g. "play Contest"), but the server resolves the *actual* rules definition (comparison
keys, metric directions, capabilities, current version) from its own authoritative source and
freezes that into the series at creation — the client is never authoritative for comparison keys,
metric directions, ruleset version selection, capabilities, or arbitrary frozen rules (issue #8
§8). Concretely, this means the Backend needs its own authoritative ruleset definitions (or a
synchronized copy of Unity's) to resolve against — it cannot simply store whatever the client
claims a ruleset's rules are, or a malicious/buggy client could dictate its own win condition. This
requirement — the Backend needing *some* server-side source of truth for ruleset content, not just
an opaque pass-through id — is the single largest piece of net-new infrastructure this audit
surfaces for #9/#10, and is called out explicitly here so #10 does not have to rediscover it.

Unsupported/non-async ruleset selection must be rejected at creation
([fixture 09](fixtures/09-unsupported-ruleset-version.json)); an unsupported `SeriesFormat` must
also be rejected at creation ([§8](#8-supported-remote-competition-subset)). This document does not
finalize the exact REST DTO shape — #10 owns production endpoint implementation.

## 16. Canonical compatibility fixtures

Eleven fixtures, one canonical set, stored at
[`v2/docs/competition-protocol/fixtures/`](fixtures/) (schema: [`fixtures/SCHEMA.md`](fixtures/SCHEMA.md)),
covering exactly the issue's required list. Each fixture states its `backendExecutable` flag
explicitly rather than leaving it implicit:

| # | Fixture | Backend-executable today? |
|---|---|---|
| 01 | [SealedAttempt, one player completed](fixtures/01-sealed-attempt-one-completed.json) | ✅ |
| 02 | [OpenTarget, only primary target visible](fixtures/02-open-target-primary-only.json) | ❌ (#9/#10/#11) |
| 03 | [Higher-is-better result](fixtures/03-higher-is-better-result.json) | ✅ |
| 04 | [Lower-is-better result](fixtures/04-lower-is-better-result.json) | ❌ (#11) |
| 05 | [Ordered multi-key tie-break](fixtures/05-ordered-multi-key-tiebreak.json) | ❌ (#11) |
| 06 | [True draw](fixtures/06-true-draw.json) | ✅ |
| 07 | [Best-of-3 early termination](fixtures/07-best-of-3-early-termination.json) | ✅ |
| 08 | [Best-of-7 full run](fixtures/08-best-of-7-full-run.json) | ✅ |
| 09 | [Unsupported ruleset version](fixtures/09-unsupported-ruleset-version.json) | ❌ (#9/#10) |
| 10 | [Identical result retry](fixtures/10-identical-result-retry.json) | ✅ |
| 11 | [Conflicting result replay](fixtures/11-conflicting-result-replay.json) | ❌ (#11 — see [§13](#13-result-retryreplay-behavior)) |

Fixtures are protocol-level JSON (participants, commands, expected participant-visible outcomes)
— never coupled to EF rows, Unity save documents, or API-controller DTOs, per the issue's
requirement.

## 17. Tests added and run

**Backend (`sweat-this/Level5Backend`, this repository) — changed and executed in this session:**

- `v2/tests/Level5.Domain.Tests/Competition/CompatibilityFixtureTests.cs` — loads the fixture
  files directly (by walking up from the test assembly's output directory - or, as a fallback, the
  current working directory - to `Level5BackendV2.sln`, then into
  `docs/competition-protocol/fixtures/`) and drives the real `VersusSeries` domain type, not a
  spike or a mock. Covers:
  - the 6 fixtures marked `backendExecutable: true` (01, 03, 06, 07, 08, 10), each asserting the
    fixture's actual expected outcome (sealed visibility, higher-wins winner, draw, best-of-3
    clinch-and-stop-with-the-rejection-of-the-now-unnecessary-3rd-game-asserted-generically, full
    best-of-7 run, idempotent identical retry) - a command named `*ExpectRejected` (e.g. fixture
    07's 3rd-game `startAttemptExpectRejected`) is asserted to throw by the shared fixture runner
    itself, not re-derived by hand afterwards in the calling test;
  - a manifest test (`Fixture_manifest_identity_and_executability_flag_are_unchanged`) that loads
    all 11 fixture files and pins each one's `fixtureId` and `backendExecutable` flag, so neither a
    swapped/renamed fixture nor a silent flip of that flag (in either direction) without a matching
    runner/removal is caught by CI rather than drifting unnoticed;
  - `Conflicting_result_replay_is_not_yet_rejected_known_gap_for_issue_11` — deliberately asserts
    **today's actual, non-compliant** behavior (a differing resubmission is silently accepted with
    the original value kept) so this exact gap has a red flag ready to flip once #11 fixes it,
    instead of being rediscovered later. Drives the series from fixture 11's own commands/result
    payloads directly (never a separately hand-authored literal), so an edit to the fixture's
    content is reflected here automatically rather than silently diverging from what runs.
- **Executed:** `dotnet build v2/Level5BackendV2.sln` — succeeded, 0 errors (6 pre-existing,
  unrelated warnings: an `SSH.NET` advisory and two ASP.NET Core `KnownNetworks`/`IPNetwork`
  deprecation warnings, none touched by this change).
- **Executed:** `dotnet test` on `Level5.Domain.Tests` (129 tests, including the 18 new/changed
  ones above), `Level5.Application.Tests` (62 tests), and `Level5.Architecture.Tests` (11 tests —
  including the existing "Domain never references Unity" assertion) — **all passed, 0 failures**.
- **Not executed in this session:** `Level5.Infrastructure.IntegrationTests` and
  `Level5.Api.IntegrationTests` both require a live Docker daemon (Testcontainers spins up
  Postgres); Docker was not available in this environment
  (`DockerUnavailableException: Failed to connect to Docker endpoint at 'npipe://./pipe/docker_engine'`).
  This is a pre-existing environmental limitation, not something this change introduced or could
  route around — nothing in this issue's diff touches Infrastructure or Api integration test
  code, so this gap is disclosed rather than worked around.

**Unity (`sweat-this/level5`) — changed in this session, execution requires the Unity Editor
which this environment does not have:**

- `docs/versus-architecture.md` and `docs/versus-correspondence-plan.md` corrected (see
  [§18](#18-documentation-corrections)); `IVersusSeriesRepository.cs`'s misleading doc comment
  corrected. These are documentation/comment-only changes with no compiled-code impact, so "not
  executed" does not apply to them.
- **No new Unity test file was added in this session.** Unity's EditMode tests run inside the
  Unity Editor/Test Runner, which this environment cannot launch (no Unity install, no license, no
  GUI/batchmode invocation attempted or claimed). Per the issue's instruction not to claim
  cross-repository certification unless both sides were actually executed: **only the Backend side
  above was executed.** The mirrored coverage still required on the Unity side, to be added and run
  through the repository's own EditMode/CI workflow, not by this issue:
  - a fixture-driven test analogous to `CompatibilityFixtureTests` that drives
    `VersusSeries`/`VersusGame`/`CompetitiveRuleset.Compare` from the same 11 JSON fixtures (all
    11 are backend-*inexecutable* fixtures 02/04/05/09/11 are fully Unity-executable today, since
    Unity already implements OpenTarget, lower-wins, tie-breaks, and its own — different —
    duplicate-submission rejection; only fixture 11's *specific* "reject a differing resubmission
    with a distinguishable conflict signal, while still accepting an identical resubmission
    idempotently" is not something Unity's domain does today either, since `Attempt.Complete`
    rejects both cases uniformly);
  - a regression test locking the corrected `IVersusSeriesRepository` doc comment's claim (that a
    remote implementation of it is *not* how remote play works) so the misleading framing cannot
    silently return.

## 18. Documentation corrections

**Corrected in `sweat-this/level5` (this session):**

- **`Assets/Level5/Core/Versus/IVersusSeriesRepository.cs`** — the doc comment claimed *"A remote
  implementation replaces this interface and nothing above it... the coordinator, the series, the
  games and gameplay itself all stay exactly as they are when a server starts owning the
  documents."* This is the exact misleading claim issue #8 §4/§19 asks to correct: it implies
  remote play = a networked `Save(VersusSeries)`/`Load(SeriesId)` implementation uploading/
  downloading the whole aggregate. Corrected to state that `IVersusSeriesRepository` is **local
  persistence only**, and remote correspondence uses a separate typed client speaking
  command/query semantics against the Backend's HTTP API (per [§5](#5-authority-boundaries) of
  this document), with a pointer to this document for the actual remote architecture.
- **`docs/versus-architecture.md` §10 ("Where a backend plugs in")** — corrected the diagram and
  table that showed `IVersusSeriesRepository` branching directly to *"a remote store, later"* and
  the claim *"Nothing about this design assumes the store is local."* Replaced with a corrected
  section stating the local/remote boundary explicitly (`IVersusSeriesRepository` = local
  persistence; remote correspondence = a separate client + Backend HTTP API, per Competition
  Protocol V1) and linking to this document.
- **`docs/versus-correspondence-plan.md`** — corrected the line *"Can a backend later become
  authoritative without moving gameplay code? Yes. Authority lives in whoever implements
  `IVersusSeriesRepository` plus the coordinator"* — this technically already hedged with "plus
  the coordinator," but read in isolation it still implies the repository interface is the
  intended server integration seam. Added an explicit correction pointing at the actual decision.

**Not changed:** the underlying claim that gameplay simulation stays client-owned, that
`ParticipantKind`/`IVersusClock`/`IVersusIdSource` are injection seams, and that local persistence
via `IVersusSeriesRepository` is unaffected — all of that remains accurate and is unrelated to the
one misleading claim above. No production code was refactored; only documentation and one doc
comment were corrected, per the issue's explicit "avoid unrelated production refactoring"
instruction.

## 19. Required changes for #9 (correspondence persistence)

- Persist a frozen-rules snapshot per series: `RulesetId`, `RulesetVersion`,
  `MinimumCompatibleVersion`, `ModeId` (opaque), ordered `ComparisonKeys` (metric + direction),
  `InformationPolicy`, first-attempt ordering (when `OpenTarget`) — see [§9](#9-frozen-rule-semantics).
- Persist `CompetitionProtocolVersion` where the persisted row needs to record which protocol
  contract a series was created under (relevant mainly for series that outlive a protocol
  version bump).
- Extend the attempt-result persistence shape from a single `int Score` to the named-metric
  representation from [§10](#10-attempt-result-semantics) (still the Backend's own internal
  shape — not required to mirror Unity's positional array).
- The frozen-rules/result persistence schema needs **its own version field**, independent of
  `CompetitionProtocolVersion` — following the existing `VersusSeriesStateJson.SchemaVersion`
  pattern, which #9 should also make this time actually enforce on read (today's `SchemaVersion`
  is written but never consulted for migration — a gap worth closing while #9 touches this code
  anyway, though that's #9's call).
- The existing participant-safe projection (`ToView`) must grow to redact the new richer metric
  set the same way it already redacts `Score` today, plus implement `OpenTarget`'s partial reveal
  ([§11](#11-information-policy-semantics)).

## 20. Required changes for #10 (remote challenge API)

- `CreateChallenge` must accept `Opponent`, requested `RulesetId`, `SeriesFormat` (validated
  against `{1,3,5,7}` only — [§8](#8-supported-remote-competition-subset)), and
  `InformationPolicy`.
- The server must resolve the requested ruleset against its own authoritative rules source (not
  trust client-supplied comparison keys/directions/capabilities/version — [§8 challenge
  semantics](#8-define-challenge-semantics)) and freeze the result at creation.
- Reject unsupported ruleset versions ([fixture 09](fixtures/09-unsupported-ruleset-version.json))
  and non-asynchronous-capable rulesets at creation time, not at first-attempt time (unlike
  Unity's local `VersusSeriesValidator`, which checks at attempt-issue time — the remote contract
  checks earlier, at creation, since there is no reason to let two players accept a challenge the
  server already knows it cannot honor).
- `AcceptChallenge`/`DeclineChallenge`/`CancelChallenge` keep the Backend's existing
  role-gated authorization (opponent-only accept/decline, challenger-only cancel) — this is
  already correct and stricter than Unity's local, actor-unchecked `Decline()`; #10 does not need
  to loosen it to match Unity.
- Decide, explicitly (not by omission), whether the remote MVP needs a `Forfeit`
  command/mid-series-abandonment path — the Backend has **no** forfeit concept today at any layer
  ([§12](#12-reconcile-attempt-lifecycle-semantics) / matrix "Forfeit semantics" row). If yes, this
  is net-new domain work (new `SeriesStatus` value, new aggregate transition, new authorization
  rule), not a #10-only change.

## 21. Required changes for #11 (remote attempt API)

- Attempt identity and the remote attempt descriptor: `AttemptId`, `RulesetId`, `RulesetVersion`,
  `ModeId`, plus whatever `MatchRequest`/`MatchConfiguration` re-audit determines is still missing
  ([§14](#14-define-remote-attempt-launch-semantics) — deliberately left open here per the issue's
  instruction not to prematurely finalize this).
- Implement the named-metric comparison engine: ordered comparison keys, per-key direction
  (`HigherWins`/`LowerWins`), tie-break fallthrough, equal-on-every-key → draw
  ([fixtures 04](fixtures/04-lower-is-better-result.json)/[05](fixtures/05-ordered-multi-key-tiebreak.json)).
- Implement `OpenTarget` issuance gating (refuse `StartAttempt` for the non-first-mover until the
  first-mover's attempt is complete) and partial-target projection (expose only the primary
  metric) — [fixture 02](fixtures/02-open-target-primary-only.json).
- **Fix the identical-vs-conflicting resubmission gap**, confirmed present today by this issue's
  added test: compare the incoming result against the stored one before taking the idempotent-
  return path; equal → success, different → reject as a conflict (HTTP 409, not a silent 200) —
  [§13](#13-result-retryreplay-behavior), [fixtures 10](fixtures/10-identical-result-retry.json)/
  [11](fixtures/11-conflicting-result-replay.json).
- Explicitly decide (and document, whichever way it goes) whether the remote MVP needs an
  `Abandon`/reissue path, per [§12](#12-reconcile-attempt-lifecycle-semantics).
- Enforce the richer information-policy projection at every response that could carry attempt
  results (`GetSeries`, `ListSeries`, and the response to `CompleteAttempt` itself) — never
  construct a payload containing a hidden metric, matching the discipline the Backend already
  applies to `Score` under `SealedAttempt` today.

## 22. Remaining risks / unresolved decisions

- **Ruleset content authority is the single largest open question this audit surfaces.** Whether
  the Backend maintains its own independent ruleset catalog, mirrors Unity's catalog via some data
  sync process, or takes another approach entirely was **not decided here** — the issue scopes #8
  to establishing that the server must be authoritative for ruleset content
  ([§15](#15-define-challenge-semantics)), not to designing how that authority is populated. #9/#10
  will need to resolve this before frozen-rules persistence can be finalized.
- **Mid-series forfeit** is a real capability gap (Unity has it end-to-end; the Backend has none)
  that this document deliberately leaves as an open decision for #10/#11 rather than silently
  assuming either "yes, add it" or "no, MVP doesn't need it."
- **Attempt abandon/reissue** is a real, named feature reduction versus local play for the remote
  MVP unless #11 decides to add it — see [§12](#12-reconcile-attempt-lifecycle-semantics).
- **The exact `MatchRequest`/`MatchConfiguration` field list** needed for remote attempt launch
  was not fully re-derived here, per the issue's own instruction not to finalize a production DTO
  prematurely — #11 must re-audit `Assets/Level5/Core/Match/**` directly.
- **Unity-side compatibility tests were not executed in this session** (no Unity Editor available
  in this environment) — see [§17](#17-tests-added-and-run) for exactly what mirrored coverage is
  still required and why it could not be run here. This audit's conclusions rest on static reading
  of Unity's source and its existing (Unity-CI-run, not this-session-run) test names/assertions,
  not on tests this session executed against Unity.
- **Unity's own architecture-test blind spot** (regex scans that don't catch `ScriptableObject`/
  `JsonUtility`, [§4](#4-shared-runtime-domain-option-a--why-it-is-rejected-now) point 1) is a
  latent risk independent of this issue — worth a follow-up in the Unity repository, but out of
  scope to fix here.
