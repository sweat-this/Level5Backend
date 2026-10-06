# Modular game boundaries and namespaced API evolution

- Status: Accepted
- Date: 2026-10-05
- Decision owner: Backend V2 issue #62
- Scope: current modular-monolith boundaries and the API evolution seam for future games

## Audited baseline

This decision describes source that existed at the following freshly fetched `dev` commits. It is
a current-state ownership map, not a request to rename or relocate that source.

| Repository | Commit |
| --- | --- |
| `sweat-this/Level5Backend` | `0e3ab8f734c25c48b97cfc44ed984d2fe2a0d516` |
| `sweat-this/platform` | `ab11764c4c366e80a8e320ddb5ca1b7234a07783` |
| `sweat-this/level5` | `c4353a905d845f65e83b0a1c253168e7264d4b8b` |
| `sweat-this/level5frontend` | `aa9636d2fa96e8f458ffed2096cbf65c3089715d` |

Backend PR #69 introduced Platform-owned product entitlements and PR #70 added its validation
follow-up. Both are merged into the Backend commit above, so the Platform/Product boundary is a
current capability rather than a speculative dependency.

## Decision

Backend V2 remains one modular monolith: one `Level5.Api` process, one PostgreSQL deployment, the
existing Clean Architecture projects, one `Level5V2DbContext`, and one migration stream. Logical
module ownership is expressed by the existing Domain/Application namespaces, explicit ports,
route ownership, table ownership, documentation, and architecture tests. It does not require a
repository rename, project split, per-module database infrastructure, or in-process networking.

### Current module ownership

The shared Platform module owns account, security, public-player, social, and product-access
behavior:

| Layer | Current shared Platform source |
| --- | --- |
| Domain | `Level5.Domain.Identity`, `.Players`, `.Social`, `.Platform` |
| Application | `Level5.Application.Identity`, `.Players`, `.Social`, `.Platform` |
| Application ports | Account, auth-session, email-verification, password-recovery, player-profile, friendship, and product-entitlement stores and policies in `Level5.Application.Abstractions` |

`Level5.Domain.Ids` and the Domain/Application `Common` namespaces are supporting concepts in the
current assemblies. `PlayerId` is the intentional gameplay/social identity contract available to
games. Physical co-location does not make every ID or helper a cross-game contract: for example,
`VersusSeriesId`, `AttemptId`, and `MatchResultId` remain Level5 concepts.

The current Level5 game module owns:

| Layer | Current Level5 source |
| --- | --- |
| Domain | `Level5.Domain.Competition`, `.Results`, `.Leaderboards` |
| Application | `Level5.Application.Competition`, `.Results`, `.Leaderboards` |
| Application ports | `IVersusSeriesStore`, `IRulesetCatalog`, `IChallengeExpiryPolicy`, `IMatchResultStore`, `ILeaderboardQuery`, and `ILeaderboardPolicyCatalog` |

These namespaces describe working Level5 behavior and remain unchanged. They are not moved below a
new `Games.Level5` namespace merely to make a future module look symmetrical. Existing Level5 use
cases may continue to consume intentionally shared Player/Profile/Friendship contracts when their
rules require them.

### Dependency direction

```text
Platform -> Game module        FORBIDDEN

Game module -> Platform
    allowed only through intentionally shared contracts

Game A -> Game B               FORBIDDEN

API -> Platform / all games    ALLOWED as composition root

Infrastructure
    implements module-owned ports
    but must respect table/query ownership
```

Platform identity and security must never depend on Level5 Competition, Results, or Leaderboards.
A future ZROMP, Secret Robot, or Blood Money module may use `PlayerId` and other deliberate shared
Platform contracts, but it must not consume Level5 domain, application, repository, row, or query
types. A shared process and database do not weaken this rule.

`Level5.Api` remains the composition root and may wire every module. In-process collaboration uses
normal typed application-facing contracts. This decision adds no HTTP, gRPC, message bus,
mediator/event bus, or runtime plugin loader between modules.

## Persistence ownership

The source-backed ownership of the current tables is:

| Owner | Tables |
| --- | --- |
| Platform | `accounts`, `auth_sessions`, `email_verification_challenges`, `password_reset_challenges`, `player_profiles`, `product_entitlements`, `friend_requests`, `friendships` |
| Level5 | `competitive_series`, `match_results` |

Leaderboard reads are Level5-owned projections over Level5-owned `match_results` persistence.
The current foreign keys from social and Level5 records to `player_profiles` preserve shared player
identity; they do not transfer mutation ownership of `player_profiles` to a game module.

A module owns mutations, invariants, stores, and query semantics for its own tables. A future game
must not query or mutate another game's tables or reuse that game's repository/persistence
implementation merely because the tables share PostgreSQL. Cross-module behavior must use shared
Platform contracts, explicit application-facing contracts, or API/composition orchestration when
genuinely required. Platform-owned public-player and social contracts are deliberately reusable.

The physical persistence shape remains one PostgreSQL deployment, `Level5V2DbContext`, and the
single migration directory under `Level5.Infrastructure/Persistence/Migrations`. Logical ownership
does not create separate DbContexts, schemas, databases, or migration executables today. When a
migration changes module-owned data, its review and tests must identify that owner even though the
migration stays in the common stream.

## API ownership

Route ownership follows the behavior behind the route, not whether the URL literally contains a
module name.

### Shared Platform routes

The current Platform-owned public resource families are:

- `/api/v2/auth/...`
- `/api/v2/me...`, including `/api/v2/me/sessions/...`
- `/api/v2/players/...`
- `/api/v2/friends/...`
- `/api/v2/email-verification/...`
- `/api/v2/platform/me/entitlements/...`

Their existing URLs remain stable; cosmetic insertion of `platform` would add client churn without
improving ownership.

### Existing Level5 routes

The current Level5-owned public resource families are:

- `/api/v2/series/...`
- `/api/v2/match-results`
- `/api/v2/leaderboards/...`

They remain unchanged. A new operation on the existing Series aggregate stays beneath
`/api/v2/series/...` while that is the stable Series resource family. Do not split one aggregate
across unrelated top-level paths.

### Future game namespace rule

A new independent game-specific resource family introduced after this decision starts at:

```text
/api/v2/games/{game-route-key}/...
```

For example, #63 may introduce `/api/v2/games/zromp/...` only after ZROMP has a concrete backend
requirement. No placeholder endpoint is created by this decision. A genuinely new Level5-specific
resource family may start below `/api/v2/games/level5/...`; that rule does not relocate or alias the
three established Level5 families above.

A game route key identifies which API/game module owns a resource. `ProductId` identifies which
access-controlled product an entitlement concerns. They may happen to share text such as `level5`,
but they are different concepts and neither is authoritative for the other. URL routing does not
justify a persisted `GameId`. If #63 demonstrates a need for a first-class module identifier beyond
a compile-time route slug, design it from that requirement.

### Compatibility procedure for an existing route family

If an established Level5 resource family is ever moved to a namespaced route:

1. Keep the existing route during migration.
2. Introduce the new route additively.
3. Route both paths to the same Application use case and domain authority.
4. Never duplicate persistence or business rules.
5. Certify identical authentication, error, and idempotency semantics.
6. Mark the legacy API deprecated when appropriate.
7. Update `sweat-this/platform`'s Backend resource adapter.
8. Update the Unity typed client.
9. Certify both clients against the compatibility window.
10. Remove the old route only after the supported-client and rollback window closes.

This is a migration procedure, not authorization to add route aliases now.

## Client and frontend alignment

At its audited commit, `sweat-this/platform` already expresses the desired frontend ownership
model:

```text
src/lib/backend-v2/resources/platform/*
src/lib/backend-v2/resources/games/level5/*
```

Platform resources cover account/auth/player/friend behavior. The Level5 series adapter lives in
`resources/games/level5/series.ts` even though it calls the historical `/api/v2/series/...` paths.
Higher UI/application layers therefore depend on resource ownership rather than Backend route
shape. That adapter is the frontend migration seam if a route family changes later.

Platform's `/games/zromp`, `/games/secret-robot`, and `/games/blood-money` pages are navigation and
product surfaces only; their presence does not authorize Backend modules. The transfer ledger at
`docs/migration/level5frontend-transfer.md` makes Platform authoritative for shared frontend
boundaries while retaining `level5frontend` as migration/rollback compatibility context.

Platform's audited contract metadata currently pins Backend commit
`6efa7946e551499e8f9d80c83490a7b04c34607a`. That pin predates #61's entitlement API and should be
advanced through Platform's normal snapshot/generation workflow when Platform consumes that
contract. It does not change the Backend ownership decision and is not updated by this Backend-only
architecture slice.

The Unity client currently has typed clients and wire DTOs for shared auth/player/friend resources
and Level5 series, match-result, and leaderboard resources. Its Level5 clients bind directly to
`api/v2/series`, `api/v2/match-results`, and `api/v2/leaderboards`; the compatibility procedure above
therefore requires an explicit Unity client update and certification before any future removal.
No Unity path or DTO changes are part of this decision.

## Enforcement now and at the first sibling game

`Level5.Architecture.Tests` certifies that each shared Domain namespace (`Identity`, `Players`,
`Social`, and `Platform`) has no dependency on the Level5 `Competition`, `Results`, or
`Leaderboards` domains. Equivalent Application tests reject dependencies from each shared
Application namespace to either the Level5 Application namespaces or their Domain namespaces.
These rules intentionally do not forbid Level5 Application code from consuming shared
Player/Social contracts.

There is no second backend game, so this decision creates no empty namespace or project to make a
vacuous sibling test pass. When #63 adds the first real sibling module, that change must add
architecture tests proving both directions:

```text
FutureGame !-> Level5
Level5 !-> FutureGame
```

Each later game must receive equivalent pairwise isolation coverage. A sibling game may depend
only on intentional shared Platform contracts and common primitives. At that concrete gate,
reassess whether module-specific assemblies would materially improve enforcement or extraction;
do not split the current Domain/Application/Infrastructure/API projects preemptively.

## Consequences and explicit non-goals

This decision changes documentation and architecture enforcement only. It does not change runtime
behavior, routes, OpenAPI, persistence, migrations, or clients. In particular, it does not:

- rename existing routes or add `/games/level5` aliases;
- couple a route key to `ProductId` or add `GameId` persistence;
- create product/module registry tables;
- split PostgreSQL, schemas, DbContexts, migrations, or current projects;
- create placeholder game modules or generic game abstractions;
- move existing namespaces;
- add inter-module networking or messaging;
- entitlement-gate Level5 APIs;
- rename `Level5Backend`;
- migrate further `level5frontend` capabilities; or
- implement #63.
