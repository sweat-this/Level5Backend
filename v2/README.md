# Level 5 Backend V2

Clean Architecture foundation for the next generation of the Level 5 backend, built alongside
the legacy backend in the same repository. V2's first player-facing goal is asynchronous
friend-vs-friend ("correspondence") competition. This document is the architecture decision
record for the foundation slice, plus everything needed to run it locally.

## Status

Foundation slice only. Domain: player identity/tags, friendships, and a full correspondence
`VersusSeries` lifecycle (challenge → accept → play best-of-N → complete, server-authoritative,
concurrency-safe, sealed results). Not yet built: leaderboards, richer profiles, notifications,
anything in the [Non-goals](#non-goals-for-this-slice) list below.

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

- **Identity** (`Level5.Domain.Identity`): `Account`, `Username`, `AccountStatus`, `Email`.
  Purely the private authentication identity - no display name, no tag. `AccountStatus` is
  `Active` (the default for every new account) or `Disabled`; this slice only defines and persists
  it, it does not enforce it - a disabled account can still authenticate until the
  authentication/session slice adds that check. `Email` is optional, normalized to a
  lower-invariant canonical form for case-insensitive uniqueness, and never exposed through any
  public player-facing API or DTO - only `RegisterAccountUseCase`/`AccountStore` ever see it.
- **Players** (`Level5.Domain.Players`): `PlayerProfile`, `PlayerTag`. The public in-game
  identity - deliberately holds nothing from the private account identity (no password hash,
  email, status, or IP address). `PlayerTag` is exact-match only (`Name#1234`; grammar: 2-20
  handle characters, `#`, then a 3-6 digit discriminator - see `PlayerTag.cs`); no partial/prefix
  search.
- **Social** (`Level5.Domain.Social`): `FriendRequest` (Pending/Accepted/Declined/Cancelled) and
  `Friendship` (a canonically-ordered pair, so a unique DB index prevents a duplicate in either
  direction).
- **Competition** (`Level5.Domain.Competition`): `VersusSeries`, the aggregate root for a
  correspondence match. See [Competition domain](#competition-domain-versusseries) below.

Deliberately not modeled yet: leaderboards, progression, matchmaking, notifications - see
[Non-goals](#non-goals-for-this-slice).

## Competition domain: `VersusSeries`

`VersusSeries` is the server-authoritative aggregate for one challenge/match between two players.

- **Frozen rules**: `SeriesFormat` (best-of-N) is captured once at `CreateChallenge` and never
  changes for that series' lifetime, even if future series are configured differently.
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
  `VersusSeries.StartAttempt`; regression-tested in `VersusSeriesTests`.)
- **Sealed results**: `VersusSeries.ToView(viewerId)` produces a viewer-specific `SeriesView`. An
  opponent's attempt shows its real `Status` (so the viewer knows they've submitted) but its
  `Score` stays `null` until *both* attempts in that game are complete. This lives in the domain,
  not the API layer or the client - see `GameRound.IsResolved` / `VersusSeries.ToRoundView`.
- **Non-participant = 404, not 403**: every series-scoped use case checks participation and throws
  the same `NotFoundException` a non-existent series id would produce (`SeriesLookup`), so probing
  random series ids can't be used to learn which ones are real.

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
(`GameRound`/`GameAttempt`) is a single `jsonb` column (`state_json`), versioned with an embedded
`schema_version` field so a future shape change can be detected and migrated in code rather than
silently misread. See `VersusSeriesRow`, `VersusSeriesStateJson`, `VersusSeriesStore`.

Everything else (`accounts`, `player_profiles`, `friend_requests`, `friendships`) is plain
relational EF Core, mapped through dedicated `*Row` types in `Level5.Infrastructure.Persistence.Rows`
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

Migrations are **not** applied automatically at startup (see `Program.cs` - there is no
`Database.Migrate()` call). Apply them explicitly:

```powershell
dotnet ef database update --project src/Level5.Infrastructure --startup-project src/Level5.Api
```

### Migration history

The initial migration (`InitialCreate`) was rebaselined once, in the same slice that added
`AccountStatus`, `Email`, and the `accounts`/`player_profiles` foreign key, rather than layered as
a second migration on top of an initial one already known to be missing them. This was verified
safe before doing it: no V2 deploy/CD workflow exists yet (`.github/workflows/` had no V2 job
until this same change added `build-v2`), and the only databases that had ever run the prior
migration were local dev instances and ephemeral Testcontainers instances - both disposable. Going
forward, once V2 is actually deployed anywhere, migrations must be additive, not rebaselined.

## Shared competition domain

The prompt driving this work assumed a pure-C# Unity versus/correspondence domain
(`VersusSeries`, `Attempt`, `IVersusSeriesRepository`, etc.) already exists in the Level 5 Unity
project, and asked for an audit of whether it could become a shared framework-independent
assembly used by both Unity and this backend.

**That Unity project is not part of this repository** and could not be inspected as part of this
work. Rather than force an extraction sight-unseen (risking coupling framework-independent Unity
code to backend-specific concerns, or vice versa), the fallback path from the original brief was
taken instead:

- Backend V2's `VersusSeries` is a new, independent, server-authoritative implementation. It does
  not assume Unity's rule representation, serialization format, or repository shape.
- The wire contract is whatever `Level5.Api`'s DTOs define (`SeriesResponseDto`, etc.) - Unity
  integrates against that HTTP contract, not against a shared assembly.
- **Follow-up required**: once the Unity repository is available, audit its versus domain for (a)
  genuine framework-independence, (b) compatibility of its result-comparison/rules-freezing
  semantics with this implementation, and (c) migration risk before deciding whether extraction is
  still worthwhile. Until then, add golden/compatibility tests that assert both sides interpret a
  completed series' outcome identically, using fixed example payloads.

## Preserved / replaced / deferred

**Preserved as-is (legacy, untouched):** the entire legacy `Level5Backend.csproj` - controllers,
models, migrations, `level5` database, Docker image, deployment. Legacy and V2 can run
side-by-side; nothing here changes legacy behavior.

**Replaced (V2 does this differently, not a straight port):** authentication (ASP.NET Core
Identity password hashing + minimal-claim JWTs vs. the legacy custom scheme), identifiers
(UUIDv7 vs. sequential ints), timestamps (`DateTimeOffset` vs. formatted strings), persistence
model (domain-derived hybrid schema vs. a scaffolded 1:1 table mapping).

**Deferred (explicitly out of scope for this slice):** highscores/leaderboards, `ServerStats`,
`ServerMessages`, `UserReport`, admin/dev endpoints, refresh-token rotation and session
revocation, rejecting login for a `Disabled` account (`AccountStatus` is defined and persisted
here, but not yet enforced - see the authentication/session slice), email verification, password
reset, account recovery, public profile fields beyond display name/tag/avatar-id, any of the
[non-goals](#non-goals-for-this-slice) below.

## Non-goals for this slice

Matchmaking, realtime/WebSocket play, notifications, chat, clans/guilds, economy, progression or
leaderboard migration, full legacy database migration, event sourcing, CQRS/MediatR ceremony,
GraphQL, microservices/Kubernetes/Redis/Kafka/RabbitMQ/SignalR. None of these are needed to
establish the architecture; adding them now would be scope creep against an unproven foundation.

## Suggested follow-up slices (in order)

1. **Unity V2 networking boundary** - `IApiTransport` + per-area API clients
   (`IAuthApiClient`, `IFriendsApiClient`, `ICorrespondenceApiClient`) backed by
   `UnityWebRequest`, replacing ad hoc calls into the legacy `APIHelper`.
2. **Refresh/session tokens** - short-lived access tokens already exist (`ITokenIssuer`); add
   rotating refresh sessions and logout/revocation once a real client needs long-lived sessions.
3. **Public player profile fields** - avatar id, richer display data, once there's a UI that needs
   them.
4. **Unity versus-domain audit** - see [Shared competition domain](#shared-competition-domain).
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
