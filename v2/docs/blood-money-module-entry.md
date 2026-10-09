# Blood Money audit and module-entry gate

- Status: #79 audit/ownership/compatibility decision complete; code-level module entry **deferred**.
- Audited: 2026-10-09.
- Owner: [Backend #79](https://github.com/sweat-this/Level5Backend/issues/79), under
  [#78](https://github.com/sweat-this/Level5Backend/issues/78).
- Implementation/certification owners: [#63](https://github.com/sweat-this/Level5Backend/issues/63)
  with [#80](https://github.com/sweat-this/Level5Backend/issues/80).

## Evidence baseline and landed dependencies

All three `origin/dev` branches were fetched and all open PRs inspected before the audit.
There were no open PRs in these repositories at this baseline.

| Repository | Exact audited `dev` SHA |
| --- | --- |
| `sweat-this/Level5Backend` | `ab27af269c97f6667c64597b5bcf1a8326ada4fe` |
| `patrickrcharles/bloodmoney` | `95c92cc3c0c3c188fe7f8cf6502042194a768d80` |
| `sweat-this/platform` | `9f2ba2c8836fb9ca9534a921755586bf0b0a239b` |

Backend links below are relative to this document and describe that Backend SHA; cross-repository
source links are pinned to the corresponding audited SHA. No required repository was unavailable.

Treat #61/#62 as repository-landed dependencies despite their still-open tracker state:

- #61: [shared kernel/entitlement decision](platform-kernel-and-product-entitlements.md),
  working entitlements, persistence, API and tests exist. Ancestor commit
  [`ba8dad950392a373fa129e68d86c9d83280cc95f`](https://github.com/sweat-this/Level5Backend/commit/ba8dad950392a373fa129e68d86c9d83280cc95f)
  explicitly records `Closes #61`.
- #62: [accepted modular-game contract](modular-game-boundaries.md) and
  [current architecture guards](../tests/Level5.Architecture.Tests/DependencyRuleTests.cs) establish
  shared Platform ownership, Level5 ownership and the future game API namespace. Neither dependency
  needs reimplementation.

At this SHA, searches of `v2/src`, `v2/tests` and `v2/openapi` find no Blood Money module, credit
account/ledger, game table or route. The complete table map in
[`Level5V2DbContext`](../src/Level5.Infrastructure/Persistence/Level5V2DbContext.cs), its
[migration stream/model snapshot](../src/Level5.Infrastructure/Persistence/Migrations),
[controllers](../src/Level5.Api/Controllers) and
[OpenAPI document](../openapi/level5-v2.openapi.json) confirm that absence. A future capability,
not this audit, must establish sibling-module certification.

## Current shared Platform authority

Physical `Level5.*` naming does not make these shared contracts Level5 game authority.

| Contract and exact source | Current semantics and permitted Blood Money use |
| --- | --- |
| [`Level5.Domain.Ids.PlayerId`](../src/Level5.Domain/Ids/PlayerId.cs), [`AccountId`](../src/Level5.Domain/Ids/AccountId.cs) | `PlayerId` is the opaque shared gameplay/social identity. `AccountId` is private authentication/security identity; it must not become game identity. |
| [Identity application](../src/Level5.Application/Identity), [`JwtTokenIssuer`](../src/Level5.Infrastructure/Identity/JwtTokenIssuer.cs) | Shared registration/login/session authority. JWT `sub` identifies the account and `sid` its auth session; neither a match participant nor a caller-supplied player ID is an authenticated principal. |
| [`ICurrentPlayerProvider`](../src/Level5.Api/Security/ICurrentPlayerProvider.cs), [`CurrentPlayerProvider`](../src/Level5.Api/Security/CurrentPlayerProvider.cs) | HTTP-specific API boundary resolves the authenticated account's profile via `IPlayerProfileStore.FindByAccountIdAsync` and returns its `PlayerId`. Future endpoints use this server-side resolution; Domain/Application must not import this API interface. |
| [`Players.PlayerProfile`](../src/Level5.Domain/Players/PlayerProfile.cs), [`PlayerTag`](../src/Level5.Domain/Players/PlayerTag.cs), [`IPlayerProfileStore`](../src/Level5.Application/Abstractions/IPlayerProfileStore.cs) | One public identity per account (database uniqueness), stable ID/tag and mutable validated display name. Tag lookup is exact, case-insensitive through uppercase canonicalization. Public projections exclude private authentication data. This profile is distinct from Unity's economy profile. |
| [`Social.FriendRequest`](../src/Level5.Domain/Social/FriendRequest.cs), [`Friendship`](../src/Level5.Domain/Social/Friendship.cs), [`IFriendshipStore`](../src/Level5.Application/Abstractions/IFriendshipStore.cs) | Pending requests: recipient accepts/declines; sender cancels. Revision concurrency and canonical-pair uniqueness prevent conflicting/duplicate relationships. Accepted friendship is symmetric. `AreFriendsAsync(PlayerId, PlayerId, ...)` is the deliberate application-boundary authorization seam. |
| [`INotificationWriter` / `NotificationDraft`](../src/Level5.Application/Abstractions/INotificationWriter.cs), [`NotificationWriter`](../src/Level5.Application/Platform/NotificationWriter.cs), [`PlayerNotification`](../src/Level5.Domain/Platform/PlayerNotification.cs) | Trusted producers stage recipient-scoped inbox entries in the current unit of work; the writer does not commit independently. Shared persistence owns read state and uniqueness by `(RecipientPlayerId, Source, SourceEventKey)`. This is an inbox contract, not chat storage or delivery infrastructure. |
| [`Platform.ProductId`](../src/Level5.Domain/Platform/ProductId.cs), [`ProductEntitlement`](../src/Level5.Domain/Platform/ProductEntitlement.cs), [`IProductEntitlementStore`](../src/Level5.Application/Abstractions/IProductEntitlementStore.cs), [`PlatformEntitlementsController`](../src/Level5.Api/Controllers/PlatformEntitlementsController.cs) | Current grant keyed by `(PlayerId, ProductId)` in `product_entitlements`; access is `RevokedAt == null && (ExpiresAt == null || now < ExpiresAt)`. Revision-conditioned mutations; authenticated self-query reads; no player-facing grant/revoke endpoint. |

Shared Platform owns `accounts`, `auth_sessions`, `email_verification_challenges`,
`password_reset_challenges`, `player_profiles`, `friend_requests`, `friendships`,
`product_entitlements` and `player_notifications`. Games consume deliberate contracts without
taking ownership of those tables. Identity, Players, Social and Platform remain shared in both
Domain and Application.

Blood Money owns future challenge participants; Platform owns social friendship. Check friendship
through the existing shared application contract when the challenge policy requires it. Do not
duplicate friendship membership inside Blood Money or make its Domain depend on Level5 competition.

API/module existence is separate from store/product ownership. This decision does not declare a
`blood-money` ProductId or entitlement gate. Enforcement requires a concrete separately approved
requirement, including product mapping and compatibility policy.

## Current Level5 game authority: unavailable for reuse

| Owner and source | Existing authority |
| --- | --- |
| [`Level5.Domain.Competition`](../src/Level5.Domain/Competition), [`VersusSeries`](../src/Level5.Domain/Competition/VersusSeries.cs), [`GameAttempt`](../src/Level5.Domain/Competition/GameAttempt.cs), [`Level5.Application.Competition`](../src/Level5.Application/Competition) | Level5 correspondence series, frozen rules, per-player attempts, result disclosure and transitions; persisted through `IVersusSeriesStore` / `competitive_series`. |
| [`Level5.Domain.Results.MatchResult`](../src/Level5.Domain/Results/MatchResult.cs), [`Level5.Application.Results`](../src/Level5.Application/Results) | Level5 mode/level/character result submissions and metrics; `IMatchResultStore` owns `match_results`. |
| [`Level5.Domain.Leaderboards`](../src/Level5.Domain/Leaderboards), [`Level5.Application.Leaderboards`](../src/Level5.Application/Leaderboards), [`StaticLeaderboardPolicyCatalog`](../src/Level5.Infrastructure/Leaderboards/StaticLeaderboardPolicyCatalog.cs), [`LeaderboardQuery`](../src/Level5.Infrastructure/Persistence/Repositories/LeaderboardQuery.cs) | Level5 mode-specific ranking policies and queries over Level5 `match_results`. |

Blood Money must not depend on, inherit from, query through or persist through these Domain,
Application, repository, row or query contracts. `VersusSeries` is not a Blood Money friend challenge;
`GameAttempt` and `MatchResult` are not Blood Money result-verification or settlement contracts.
Co-located `IVersusSeriesStore`, `IMatchResultStore`, `ILeaderboardQuery`, `IRulesetCatalog` and
related policy ports in `Application.Abstractions` remain Level5-owned, not shared primitives.

## Current Unity Blood Money authority

All sources in this table are from `patrickrcharles/bloodmoney` at the audited SHA.

| Exact source | Current local authority |
| --- | --- |
| [`Assets/Game/Core/Economy/PlayerProfile.cs`][unity-profile] | Immutable local economy snapshot: `Balance`, one `ActiveStake`, lifetime statistics, `CurrentRun`, history and career records. `DefaultInitialBalance = 1000`; `CreateDefault` starts the local profile/run with 1,000 BLOOD. No Backend `PlayerId` key. |
| [`Assets/Game/Runtime/Persistence/ProfileService.cs`][unity-profile-service], [`JsonProfileStore.cs`][unity-store] | Runtime creates/loads the local profile and publishes transition candidates only after saving. `profile.json` schema is currently v5 with temporary/backup recovery and atomic replacement. Interrupted stakes enter recovery; persistence failure does not publish the candidate. |
| [`Assets/Game/Core/Economy/MatchStake.cs`][unity-stake], [`PlayerProfileTransitions.cs`][unity-transitions] | Positive integer local wager, opaque commitment, canonical configuration and recorded payout terms; pure commit/rollback/settlement transitions update local balance, run, history and records. |
| [`Assets/Game/Runtime/Economy/MatchAdmissionService.cs`][unity-admission] | Revalidates local payout/admission, durably commits before starting wagered gameplay, coordinates local settlement and recovery. Practice has a separate non-wagered admission path. |
| [`Assets/Game/Core/Economy/BankrollRun.cs`][unity-run], [`MatchHistory.cs`][unity-history], [`CareerRecords.cs`][unity-records] | Local run identity/baselines/peak; bounded recent career resolutions with unique commitment IDs; lifetime wagered records (unknown historical detail remains nullable). None is a remote account or balanced ledger. |
| [`Assets/Game/Core/Economy/LastChanceRunState.cs`][unity-last-state], [`LastChancePolicyV1.cs`][unity-last-policy] | Persisted per-run Available/Consumed/Recovered attempt state; local `last-chance-v1` recovery policy uses 500 BLOOD. Admission/ProfileService own its durable local lifecycle. |
| [`Assets/Game/Core/ChallengeContract.cs`][unity-challenge], [`ChallengeEvaluator.cs`][unity-evaluator] | Authored local configuration/completion contract; evaluates a completed snapshot for the sole human's win. No persistent friend roster, Backend authorization, message membership or trusted online result proof. |
| [`docs/multiplayer-economy-v1.md`][unity-economy] | Approved initial multiplayer is casual/non-wagered: no stakes, reservations, transfers, payouts or local profile mutations. A player-controlled listen host cannot authorize durable cross-player economy outcomes. |

`profile.json` therefore remains the local/offline single-player authority for balance, active
stake, wagers/settlement, bankroll runs, history/records and Last Chance. Local match `ParticipantId`,
session membership and connection identity do not establish shared Backend `PlayerId` ownership.

## Planned Backend authority and economy compatibility

| Authority | Decision for #79 |
| --- | --- |
| Unity local BLOOD | Preserve existing local single-player authority and behavior. |
| Future Backend Blood Credits | Blood Money-owned authoritative online accounts and balanced ledger, keyed to shared `PlayerId`. |
| Automatic synchronization | None approved. These are separate writable authorities. |
| Automatic import of `profile.json` balance | None approved. A local save is not trusted issuance evidence. |
| Initial online issuance / migration | #80 owns the explicit product/issuance/migration decision. No default grant/import of 1,000 credits is established here. |

#80 must deliver the complete account/ledger slice: integer credits, immutable balanced postings,
atomic postings and balance projections, no-negative-balance behavior, ownership/authorization,
constraints, controlled corrections, failure-atomic migrations and reconciliation proof. Do not
introduce a partial mutable balance in #79 or establish account authority before its ledger exists.
Later Blood Money issues own reservations, challenge state, trusted results and settlement. Unity's
local evaluator and a listen-host winner are not substitutes for trusted result evidence.

No existing API, Unity schema, local persistence or client behavior changes in this audit. New online
operations will be additive. Existing shared and Level5 route families remain stable under #62's
compatibility procedure. Any later save/account migration requires an explicit approved trust,
issuance, replay and rollback policy under #80; the local starting balance is not that policy.

## API, layout and persistence decision

New independent Blood Money resources belong under `/api/v2/games/blood-money/...`. Credits,
challenges and messages are possible future families; their implementation owners define actual
routes/DTOs/errors/idempotency contracts. No route or health/status placeholder is added here.
Authenticated endpoints derive the acting `PlayerId` through `ICurrentPlayerProvider`; a request
may name another participant only as an authorized target, never as proof of caller identity.
Do not expose `AccountId` as gameplay identity.

When real code lands, use existing projects and these namespaces:

```text
Level5.Domain.BloodMoney
Level5.Application.BloodMoney
Level5.Infrastructure.BloodMoney
Level5.Api composition/controllers
```

Blood Money owns its future tables, invariants, mutations, repositories and query semantics within
one PostgreSQL deployment, one `Level5V2DbContext`, one migration stream and one API process. Existing
shared infrastructure can compose those capabilities; it does not authorize access through Level5
game stores. Table names and migrations belong to the implementing slice. No second database,
DbContext, backend service, message bus, plugin loader or speculative project/assembly is needed.

## #63/#80 sequencing and non-vacuous certification

The approved sequence resolves #79's tracker dependency on #63 without placeholders:

```text
#79 current-state audit + authority decision + compatibility plan
  -> #63 + #80 complete first real Blood Credit account / balanced ledger slice
  -> remaining #79 module-entry acceptance and #63 code-level certification
```

#80 is the recommended first concrete non-Level5 capability. Complete #63 certification in the
same coherent implementation window/PR as #80 unless a fresh audit identifies an earlier real
capability. Current source does not contradict this sequence. Do not close full #79/#63 acceptance
or report sibling-module certification PASS based on this document.

Extend [`DependencyRuleTests`](../tests/Level5.Architecture.Tests/DependencyRuleTests.cs) in that real
implementation, with recursive namespace coverage and assertions that required concrete Domain
and Application types exist. Empty selections, a module marker, or a disappearing module must fail
certification. Prove all of the following:

| Source | Forbidden dependencies |
| --- | --- |
| `Level5.Domain.BloodMoney` | `Level5.Domain.Competition`, `.Results`, `.Leaderboards` |
| Each Level5 game Domain namespace above | `Level5.Domain.BloodMoney` |
| `Level5.Application.BloodMoney` | Level5 Competition/Results/Leaderboards Application **and** Domain namespaces |
| Each Level5 game Application namespace | BloodMoney Application **and** Domain namespaces |
| Each shared Identity/Players/Social/Platform Domain namespace | `Level5.Domain.BloodMoney` |
| Each shared Identity/Players/Social/Platform Application namespace | BloodMoney Application **and** Domain namespaces |

Retain existing layer/shared guards; also reject reuse of Level5-owned ports in Abstractions and
game-specific IDs in Ids. Namespace-only checks must not overlook those co-located contracts.
#63/#80 integration evidence must exercise real authenticated account/ledger persistence, financial
invariants, migration/reconciliation and existing Level5 regressions on the shared deployment.
Architecture success alone does not prove financial or runtime correctness.

## Downstream delivery gates and client ownership

| Owner | Dependency / remaining delivery |
| --- | --- |
| #80 | Re-audit dev/PRs; implement full account/ledger authority and record issuance/migration policy; complete #63 and remaining #79 code acceptance. |
| [#81](https://github.com/sweat-this/Level5Backend/issues/81) | Requires ledger: atomic creator/joiner reservations, eligible release, persisted idempotency/fingerprints and concurrency proof. |
| [#82](https://github.com/sweat-this/Level5Backend/issues/82) | Requires reservations and shared identity/friends: persistent challenge participants, versioned lifecycle and funding-gated activation. Preserve eventual five-player capability. |
| [#83](https://github.com/sweat-this/Level5Backend/issues/83), [#84](https://github.com/sweat-this/Level5Backend/issues/84), [#85](https://github.com/sweat-this/Level5Backend/issues/85) | Trusted results/exactly-once settlement, expiry/reconciliation/observability and integrated wager-security certification. |
| [MSG-001 / #86](https://github.com/sweat-this/Level5Backend/issues/86), [MSG-002 / #87](https://github.com/sweat-this/Level5Backend/issues/87), [MSG-003 / #88](https://github.com/sweat-this/Level5Backend/issues/88) | Approve chat policy first; durable challenge membership/lifecycle comes from #82, never Unity's local evaluator. Then message persistence/API, ordering, idempotency, read/report authorization. |
| [MSG-004 / #89](https://github.com/sweat-this/Level5Backend/issues/89), [MSG-005 / #90](https://github.com/sweat-this/Level5Backend/issues/90) | Shared Platform inbox via `INotificationWriter`; approved coalescing, abuse/evidence/operator policies. No duplicate inbox or financial effects. |
| [MSG-009 / #91](https://github.com/sweat-this/Level5Backend/issues/91), [MSG-010 / #92](https://github.com/sweat-this/Level5Backend/issues/92) | Separate live free-text gate: independently trusted session-member-to-PlayerId evidence, approved delivery/security ADR and upstream messaging/abuse capabilities. Transient casual quick chat does not satisfy it. |

Current Platform [public product page][platform-public] and [private account portal][platform-portal]
are navigation/product surfaces, not Backend game authority. The [dashboard][platform-dashboard]
reports `not-implemented` for Blood Money online summary. Future consumers should use the intended
`src/lib/backend-v2/resources/games/blood-money/*` seam (not present yet), the existing
[resource/transport boundary][platform-integration] and [pinned OpenAPI workflow][platform-contracts].
Platform's [current contract metadata][platform-pin] already pins this audited Backend SHA.
The [portal architecture test][platform-guard] currently requires the Blood Money adapter directory
to be absent. Replace that absence assertion with concrete ownership/contract coverage when real
APIs and adapters land; it is a placeholder guard, not a permanent integration prohibition.
This issue changes no Platform production code or generated contracts.

Unity's `ChallengeEvaluator`, `ProfileService` and `MatchAdmissionService` retain their current
local flows. No Backend Unity client, token handling, online friend-challenge UI or profile migration
is delivered here. Downstream integrations must certify their real contracts independently.

## Validation, blockers and explicit non-goals

Source audit verified the cited paths/types at the three SHAs, landed #61/#62, shared/Level5 table
and contract ownership, Unity local economy and Backend Blood Money absence. No required source
was blocked. Validation passed: 38 Backend source/document links and 21 pinned client source links
resolve through `git cat-file -e <sha>:<path>`; reference links, README entry, Markdown whitespace and
fences were checked. The pinned-source absence search was
`git grep -n -i -E 'blood.?money|blood.?credit|wallet|ledger' <Backend-sha> -- v2/src v2/tests v2/openapi`
(exit 1, no matches). `git diff --check` passed. The current
[CI workflow](../../.github/workflows/ci.yml) defines no documentation/link checker.
No production/test files change, so architecture/build/Unity execution is not required for this
slice and is not claimed as sibling-module evidence.

Code-level #63 and remaining #79 acceptance are **deferred**, with the actual module and non-vacuous
tests required from #80. Online issuance/migration policy remains owned by #80. Trusted wager-result
policy and messaging product/security/retention/operations decisions remain downstream gates.

Review pass 1: no empty module, marker, dummy endpoint/table, vacuous sibling test, VersusSeries or
MatchResult reuse, merged local/online balance authority, automatic save import or generic wallet.
Review pass 2: no #80 ledger implementation/partial mutable balance, unapproved entitlement gate,
new database/DbContext/service, Unity/Platform production change, generic module framework or rename.

Explicit non-goals: implementing credit accounts, postings, issuance, reservations, challenges,
settlement, chat, Backend Unity clients, Platform/Unity UI, entitlement enforcement, save migration
or placeholder infrastructure. This decision authorizes none of those implementations within #79.

[unity-profile]: https://github.com/patrickrcharles/bloodmoney/blob/95c92cc3c0c3c188fe7f8cf6502042194a768d80/Assets/Game/Core/Economy/PlayerProfile.cs
[unity-profile-service]: https://github.com/patrickrcharles/bloodmoney/blob/95c92cc3c0c3c188fe7f8cf6502042194a768d80/Assets/Game/Runtime/Persistence/ProfileService.cs
[unity-store]: https://github.com/patrickrcharles/bloodmoney/blob/95c92cc3c0c3c188fe7f8cf6502042194a768d80/Assets/Game/Runtime/Persistence/JsonProfileStore.cs
[unity-stake]: https://github.com/patrickrcharles/bloodmoney/blob/95c92cc3c0c3c188fe7f8cf6502042194a768d80/Assets/Game/Core/Economy/MatchStake.cs
[unity-transitions]: https://github.com/patrickrcharles/bloodmoney/blob/95c92cc3c0c3c188fe7f8cf6502042194a768d80/Assets/Game/Core/Economy/PlayerProfileTransitions.cs
[unity-admission]: https://github.com/patrickrcharles/bloodmoney/blob/95c92cc3c0c3c188fe7f8cf6502042194a768d80/Assets/Game/Runtime/Economy/MatchAdmissionService.cs
[unity-run]: https://github.com/patrickrcharles/bloodmoney/blob/95c92cc3c0c3c188fe7f8cf6502042194a768d80/Assets/Game/Core/Economy/BankrollRun.cs
[unity-history]: https://github.com/patrickrcharles/bloodmoney/blob/95c92cc3c0c3c188fe7f8cf6502042194a768d80/Assets/Game/Core/Economy/MatchHistory.cs
[unity-records]: https://github.com/patrickrcharles/bloodmoney/blob/95c92cc3c0c3c188fe7f8cf6502042194a768d80/Assets/Game/Core/Economy/CareerRecords.cs
[unity-last-state]: https://github.com/patrickrcharles/bloodmoney/blob/95c92cc3c0c3c188fe7f8cf6502042194a768d80/Assets/Game/Core/Economy/LastChanceRunState.cs
[unity-last-policy]: https://github.com/patrickrcharles/bloodmoney/blob/95c92cc3c0c3c188fe7f8cf6502042194a768d80/Assets/Game/Core/Economy/LastChancePolicyV1.cs
[unity-challenge]: https://github.com/patrickrcharles/bloodmoney/blob/95c92cc3c0c3c188fe7f8cf6502042194a768d80/Assets/Game/Core/ChallengeContract.cs
[unity-evaluator]: https://github.com/patrickrcharles/bloodmoney/blob/95c92cc3c0c3c188fe7f8cf6502042194a768d80/Assets/Game/Core/ChallengeEvaluator.cs
[unity-economy]: https://github.com/patrickrcharles/bloodmoney/blob/95c92cc3c0c3c188fe7f8cf6502042194a768d80/docs/multiplayer-economy-v1.md
[platform-public]: https://github.com/sweat-this/platform/blob/9f2ba2c8836fb9ca9534a921755586bf0b0a239b/src/app/games/blood-money/page.tsx
[platform-portal]: https://github.com/sweat-this/platform/blob/9f2ba2c8836fb9ca9534a921755586bf0b0a239b/src/app/%28account%29/account/games/blood-money/page.tsx
[platform-dashboard]: https://github.com/sweat-this/platform/blob/9f2ba2c8836fb9ca9534a921755586bf0b0a239b/src/app/%28account%29/account/games/page.tsx
[platform-integration]: https://github.com/sweat-this/platform/blob/9f2ba2c8836fb9ca9534a921755586bf0b0a239b/docs/architecture/backend-v2-integration.md
[platform-contracts]: https://github.com/sweat-this/platform/blob/9f2ba2c8836fb9ca9534a921755586bf0b0a239b/contracts/README.md
[platform-pin]: https://github.com/sweat-this/platform/blob/9f2ba2c8836fb9ca9534a921755586bf0b0a239b/contracts/backend-v2.source.json
[platform-guard]: https://github.com/sweat-this/platform/blob/9f2ba2c8836fb9ca9534a921755586bf0b0a239b/src/app/%28account%29/account/games/blood-money/architecture.test.ts
