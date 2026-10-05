# Account lifecycle and cross-game identity

Status: current architecture baseline through issue #59

Audited: 2026-10-02

This document is the canonical requirements baseline for Backend V2 account lifecycle and
cross-game identity. It records current repository facts first, identifies the product decisions
that are already supported by repository evidence, and leaves unresolved lifecycle choices
explicit. It does not authorize lifecycle endpoints, schema changes, or a new service boundary.

The labels below are normative:

- **Current fact** describes code or documentation at the audited commits.
- **Selected requirement** is supported by an existing product requirement and may guide later
  implementation.
- **Decision required** is unresolved and blocks implementation of the affected capability.
- **Deferred proposal** describes a possible future slice, not approved behavior.

## Audited baseline and overlap check

The three repositories were fetched from `origin/dev` before this review. Each working-copy HEAD
matched the fetched `origin/dev` tip with zero divergence:

| Repository | Audited `dev` SHA |
| --- | --- |
| `sweat-this/Level5Backend` | `842f38eb2588e94ca01a81467729ea5bb484a300` |
| `sweat-this/level5` | `d90e5e0bc800338db270762a213074ed2107163d` |
| `sweat-this/level5frontend` | `aa9636d2fa96e8f458ffed2096cbf65c3089715d` |

GitHub issues and pull requests in all three repositories were searched for account recovery,
deletion, export, email verification, and cross-game identity work. No open pull request or
dedicated lifecycle issue duplicates this requirements work. The relevant existing requirements
are:

- Backend issues
  [#4](https://github.com/sweat-this/Level5Backend/issues/4),
  [#5](https://github.com/sweat-this/Level5Backend/issues/5), and
  [#6](https://github.com/sweat-this/Level5Backend/issues/6), which establish separate private
  account, session, and public player-profile concepts.
- Frontend epic
  [#20](https://github.com/sweat-this/level5frontend/issues/20), whose product rule is that Sweat
  This owns the account while games own their data.
- Frontend issue
  [#25](https://github.com/sweat-this/level5frontend/issues/25), which makes identity, profile,
  PlayerTag, friends, and player discovery platform-level; treats friends as Sweat This-wide; and
  rejects speculative per-game usernames or PlayerTags.

Generic search hits about persistence deletion behavior, failure recovery, generated-data export,
or email privacy were inspected as neighboring work, not lifecycle implementations.

## 1. Current identity model

### Backend V2 `Account`

**Current fact.** `Account` is the private authentication identity and security principal. It owns
`AccountId`, `Username`, optional private `Email`, `AccountStatus`, password hash, and creation time.
It deliberately contains no display name or PlayerTag. JWT `sub` contains `AccountId`; username,
email, and public profile fields are not token claims.

`Username` is normalized case-insensitively for uniqueness and is used for login. `Email` is also
normalized case-insensitively, but it is optional, unused by registration, and not exposed through a
public player API. `AccountStatus` currently has only `Active` and `Disabled`.

### `PlayerProfile`

**Current fact.** `PlayerProfile` is the public player identity. It owns `PlayerId`, its private
link to `AccountId`, mutable `DisplayName`, immutable `PlayerTag`, and creation time. It contains no
password, email, account status, IP address, or token. Other players find it by exact PlayerTag.

The database currently enforces one `PlayerProfile` per `Account` with a unique
`player_profiles.AccountId` index. Registration creates the account and its one profile atomically.

### `AuthSession`

**Current fact.** `AuthSession` is a persistent refresh-session record, not the account itself. It
belongs to one `AccountId` and stores an opaque session ID, a one-way refresh-token hash, creation and
expiry times, optional revocation time, and an optimistic-concurrency revision. A successful refresh
rotates the credential; logout revokes only the refresh session identified by the supplied refresh
token.

Access JWTs remain short-lived and stateless. Revoking a refresh session or disabling an account
prevents future login/refresh but does not synchronously invalidate an already-issued access JWT;
that token remains usable until its `exp`. The current default bound is approximately 15 minutes,
subject to deployment configuration.

### Unity local profile identity

**Current fact.** `LocalAccountIdentity.UserId` and `UserName` select a local save/progression scope
on one device. They are populated by local-profile UI and SQLite. They are not credentials, do not
prove authentication, and are not Backend V2 `AccountId` or `PlayerId` values. Local-profile
creation, selection, or deletion must not create, replace, revoke, or delete Backend V2 identity.

### Unity Backend V2 session

**Current fact.** `BackendV2SessionStore` is Unity's sole online authenticated identity boundary. A
`BackendV2Session` wraps the access token and expiry, `PlayerId`, refresh token, and refresh expiry.
The session is held in memory and mirrored to a separate local JSON file for restart restoration.
It never derives identity from `LocalAccountIdentity` or stores credentials in `GameOptions` or
`UserModel`.

The Unity wrapper currently receives `PlayerId`, not `AccountId`, from register/login/refresh
responses. The access token's server-issued `sub` remains the authentication authority.

### Frontend opaque web session and BFF

**Current fact.** The browser receives only a random opaque `HttpOnly` session cookie. The Next.js
BFF hashes that identifier for lookup and keeps Backend V2 access/refresh credentials in a
server-side web-session store. Browser code never receives Backend bearer credentials. The BFF
coordinates one-time refresh rotation with compare-and-swap and calls Backend V2 server-to-server.

The BFF web session is a session wrapper, not a second durable account or profile. Backend V2
remains authoritative for `/api/v2/me` and public player data.

| Concept | Category | Authority and purpose |
| --- | --- | --- |
| Backend V2 `Account` | Authentication identity | Private security principal and JWT subject |
| `PlayerProfile` | Public identity | Player-facing name, tag, and stable `PlayerId` |
| `AuthSession` | Refresh-session wrapper | Rotating long-lived login credential for one account |
| Unity local profile | Local save scope | On-device save/progression selector only |
| Unity Backend V2 session | Online client session wrapper | Holds Backend-issued session material and `PlayerId` |
| Frontend web session | BFF session wrapper | Maps an opaque browser cookie to server-held Backend credentials |

## 2. Cross-game identity decision

### Authentication account scope

**Selected requirement.** One Backend V2 `Account` represents one player across Sweat This games,
not only Level 5. Repository evidence is the accepted frontend product model: “Sweat This owns the
account. Games own their data,” with global account routes and per-game data below
`/account/games/*`.

This is a product-scope decision, not approval for a migration. The current `Account` schema is
already game-neutral enough for the requirement: it has no `GameId` and no Level 5 gameplay fields.
Keep identity inside the existing modular monolith. Do not extract a shared-account microservice or
add game scoping to every table without a concrete deployment or domain need.

### Public player identity scope

**Selected requirement.** `DisplayName` and `PlayerTag` are currently one shared Sweat This public
profile. Frontend requirements place profile, PlayerTag, and player discovery at platform scope and
explicitly reject per-game usernames/PlayerTags without a demonstrated requirement.

This decision is narrower than “shared authentication implies shared public identity”: it is
selected because the repository contains an explicit product requirement, not because the two
concepts are assumed equivalent.

Consequences of the selected shared-profile model:

- The current one-Account/one-PlayerProfile schema matches the requirement.
- `PlayerId`, DisplayName, and PlayerTag remain stable across games.
- A display-name update changes the platform identity visible in all games.
- Game-specific data should reference the shared `PlayerId` or an explicit game-data owner; it
  should not duplicate account credentials.

If product later requires game-specific public profiles, the model would change to one Account with
multiple profiles keyed by a stable game identifier. That would require removing the unique
AccountId-only constraint, defining `(AccountId, GameId)` uniqueness, deciding whether tags are
global or per-game, migrating every `PlayerId` reference, and versioning affected APIs and clients.
That alternative is **not selected or partially implemented here**.

## 3. Social identity scope

**Selected requirement.** Friendships and player discovery are global Sweat This relationships.
Frontend issue #25 explicitly requires friends to remain platform-level unless a future product
requirement changes the model.

**Current fact.** `FriendRequest` and `Friendship` reference `PlayerId`, consistent with the one
shared profile. They do not carry a game key.

If profiles ever become game-scoped, social migration cannot be inferred mechanically. Product
must first decide whether each existing friendship:

- remains an account-level relationship and moves to account-scoped identity;
- is copied to every game profile;
- is attached to one specified game; or
- requires player confirmation.

Until that decision exists, do not add speculative `GameId` columns or parallel social graphs.

## 4. Registration and email

**Selected policy (issue #58): optional verified recovery email.** Registration continues to accept
exactly `username + password + displayName`. It atomically
creates an active Account, one PlayerProfile/PlayerTag, and one AuthSession. It does not accept or
populate email.

`Account` and persistence support an optional private email. The database has nullable
`Email` and `EmailCanonical` columns and a unique index on canonical email; multiple null values are
allowed. `Email` performs structural validation, trimming, and canonicalization. A signed-in active
account may attach or replace an unverified address only after current-password verification. A
verified address cannot be changed by this flow; issue #64 owns that lifecycle.

`EmailVerifiedAt` is the verification authority. A dedicated, one-per-account challenge stores a
target-address snapshot and only a SHA-256 hash of a 256-bit opaque credential. Credentials expire
after the configured lifetime, rotate on resend, and are single-use through optimistic concurrency.
The configured operational defaults are a 24-hour token lifetime and a five-minute persistent
per-account dispatch cooldown shared by request and resend, supplemented by independent per-IP
endpoint limits.

An unverified stored email remains contact data, not proof of control, and must never authorize a
password reset. Public PlayerProfile projections and JWTs remain email-free. Delivery is behind a
provider-neutral port; until operations supplies a provider adapter, persistence succeeds first and
delivery fails explicitly so an explicit resend remains possible.

## 5. Password lifecycle

**Current fact (issue #59).** Verified private Account email is the only password-recovery factor.
An unverified address is contact data only. Anonymous reset requests always return `202 Accepted`
for syntactically valid email input, including unknown, unverified, disabled, cooled-down, and
delivery-unavailable cases.

### Authenticated password change

**Selected policy.** `POST /api/v2/me/password` requires bearer authentication and the current
password, reuses the existing password policy, and returns no replacement credentials. A successful
change increments `Account.SessionGeneration`, invalidating every existing refresh session,
including the caller's. A failed change does not increment the generation.

### Forgotten-password recovery

**Selected policy.** Password reset uses a dedicated one-per-account challenge bound to the verified
canonical email. The raw 256-bit base64url token is delivered only through the provider-neutral
delivery port; persistence stores its SHA-256 hash. The operational defaults are a one-hour lifetime
and five-minute persistent per-account request cooldown, both validated configuration rather than
domain constants. Completion is single-use and atomic with the password/generation change.

The default delivery adapter reports `Unavailable` without throwing or changing the enumeration-
safe HTTP response. The application boundary also collapses unexpected provider faults into that
same response and records only a low-cardinality outcome, never an address, account identifier,
token, or exception text. Production recovery is therefore not operationally usable until
operations configures a real delivery adapter.

### Token revocation policy

**Selected requirement.** Reset and authenticated change invalidate all existing refresh sessions
by incrementing `Account.SessionGeneration`. Each `AuthSession` captures the generation at login;
refresh rotation is one database decision conditioned on session revision, active account status,
and matching generation. Existing rows migrated at generation zero remain mutually valid.

**Selected requirement.** Preserve the certified stateless access-token policy unless a stronger
requirement is separately approved. Under that policy, revoking refresh sessions stops future token
issuance but existing access JWTs remain valid until expiry. Immediate invalidation would require a
new stateful validation/denylist design with latency, availability, cleanup, and multi-instance
operational consequences; it is not implicit in password change/reset.

## 6. Account deletion and anonymization

### Current data inventory

| Data category | Classification | Ownership/history notes |
| --- | --- | --- |
| `Account` | Privately owned security data | Username, optional email, status, password hash, timestamps; one player's private principal |
| `AuthSession` | Privately owned security data | Refresh-token hash and session lifecycle for one Account; secret material must never be exported |
| `PlayerProfile` | Public identity | One player's DisplayName/PlayerTag/PlayerId, but referenced by other players' social and competition history |
| `FriendRequest` | Shared social history | Directional relationship and outcome involving two players; not solely owned by either participant |
| `Friendship` | Shared social relationship | Symmetric relationship involving two players; current removal behavior does not define account erasure |
| `CompetitiveSeries` (`VersusSeries` domain / `competitive_series` table) | Shared multi-player historical data | Participants, frozen rules, attempts/results, winner, status, and timestamps; neither participant solely owns it |
| `MatchResult` | Player-attributed game history | Submitted by one player and therefore part of that player's data, but may also feed shared/public leaderboard history |

### Persistence constraints

**Current fact.** Foreign keys intentionally use `ON DELETE RESTRICT`:

- AuthSession -> Account;
- PlayerProfile -> Account;
- FriendRequest's four participant/canonical-pair references -> PlayerProfile;
- Friendship's two participants -> PlayerProfile;
- CompetitiveSeries challenger/opponent/optional winner -> PlayerProfile;
- MatchResult player -> PlayerProfile.

These constraints make naive hard deletion fail loudly. They prevent an account/profile delete from
silently orphaning sessions or pruning shared social, correspondence, and result history. This is
intentional because deletion semantics have not been defined.

### Policy alternatives

**Decision required.** Product/privacy/legal owners must choose a retention and erasure policy,
including jurisdiction, grace period, restoration, audit, abuse/financial/competitive retention,
and treatment of shared records. The architectural alternatives are:

| Alternative | Benefits | Costs and unresolved consequences |
| --- | --- | --- |
| Hard deletion | Removes the principal and owned data | Requires an explicit ordered graph delete; conflicts with shared FriendRequest/Friendship/CompetitiveSeries history and leaderboard references; may erase the other participant's record |
| Disable and retain | Preserves all references/history and is operationally simple | Is not erasure; retains private identifiers and requires a retention/legal basis plus rules for reactivation and access |
| Anonymize identity, preserve shared history | Can remove/directly de-identify private identity while retaining multiplayer records | Requires an anonymous/tombstone identity contract, collision-safe replacement values, rules for public attribution and re-identification risk, and migrations/API changes |

No alternative is selected here. Account disable already exists as a domain status but there is no
public/admin status-changing endpoint and no repository requirement equating disablement with a
deletion request.

## 7. Data export

**Current fact.** There is no user-data export endpoint, job, artifact, or UI.

**Decision required.** Define requester verification, response/delivery format, synchronous versus
asynchronous generation, expiry/download controls, rate limits, audit/support behavior, data
retention, and whether shared records include full counterpart data or a privacy-minimized
projection.

A future export may contain, subject to those decisions:

- private account information: AccountId, username, email if present, email-verification state if
  later introduced, account status, and timestamps;
- public player profile: PlayerId, DisplayName, PlayerTag, and timestamps;
- social data: the requester's friend requests and friendships, with only the minimum permissible
  counterpart identity;
- correspondence history: series in which the requester participates, including statuses, frozen
  rules, attempts/results, outcomes, and timestamps, with other-player data minimized;
- ordinary match results attributed to the requester, including mode/level/client metadata,
  metrics, modifiers, and timestamps.

An export must explicitly exclude credentials and secret/internal material, including password
hashes, refresh-token hashes or raw tokens, access JWTs, JWT signing material, web-session
credentials/encryption material, provider secrets, database credentials, and unrelated internal
diagnostics.

## 8. Identifier mutability

| Identifier | Current intent | Lifecycle requirement |
| --- | --- | --- |
| `Username` | Private authentication identifier; case-insensitive canonical uniqueness; no change flow | Do not add username changes without a concrete requirement covering uniqueness, login transition, audit, recovery, and enumeration |
| `DisplayName` | Mutable public presentation; authenticated self-update exists | Remains mutable on the shared public profile under the selected cross-game model |
| `PlayerTag` | Immutable, unique, exact-match public lookup identifier | Remains stable; any future mutation needs redirect/history/abuse and social-lookup semantics |
| `AccountId` | Stable private security identifier and JWT subject | Never reuse or substitute a Unity local profile ID |
| `PlayerId` | Stable public identity identifier | Never derive from or substitute a Unity local profile ID |

## 9. Capability matrix

“Deferred” means a potential capability is recognized but no implementation is approved.

| Capability | Current | Requirement/direction | Decision required before implementation |
| --- | --- | --- | --- |
| Registration | Implemented: username + password + displayName; creates Account, PlayerProfile, AuthSession | Preserve unchanged under optional-email policy | None for issue #58 |
| Login | Implemented by username/password with generic failures and Active-status enforcement | Shared Sweat This account login | None for current behavior |
| Refresh | Implemented as one-time rotation with optimistic concurrency | Preserve current semantics | Only if later lifecycle operations change revocation scope |
| Logout | Implemented for one presented refresh session | Preserve stateless access-token policy | Whether a future “log out all” capability is required |
| Display-name update | Implemented for the caller's PlayerProfile | Shared platform profile | None for current behavior |
| Email attachment | Implemented: authenticated Active account plus current password; unverified targets replaceable | Optional verified recovery email | Verified-address change remains #64 |
| Email verification | Implemented: expiring, rotating, single-use hashed challenge with cooldown and generic failures | Only verified email may become a recovery factor | Production delivery provider remains operational work |
| Password change | Implemented: authenticated current-password confirmation and global refresh invalidation | Client discards its session and logs in again | Production UI remains follow-up work |
| Password recovery | Implemented: verified-email-only, enumeration-safe request and single-use reset | Global refresh invalidation; access JWTs remain bounded by `exp` | Production delivery adapter and recovery UI remain operational follow-up work |
| Account disable | Domain status and login/refresh enforcement exist; no status-changing API | Existing bounded revocation remains authoritative | Actor/authorization, reason/audit, reactivation, notification, token cutoff |
| Data export | Not implemented | Deferred | Scope, shared-data privacy, format/delivery, retention and requester verification |
| Account deletion/anonymization | Not implemented; restrictive FKs prevent naive deletion | Deferred | Hard-delete vs retain vs anonymize; legal/retention/shared-history rules |
| Cross-game login | Product requirement selected; current account is structurally game-neutral | One Sweat This Account across games | Onboarding/entitlement details only when another game integrates |
| Cross-game profile identity | Product requirement selected as one shared profile/PlayerTag | Keep current one-Account/one-PlayerProfile model | Reopen only on an explicit per-game-profile requirement |

## 10. Follow-up implementation slices

Create independent issues only after each slice's product decisions are accepted. Do not combine
these into one implementation PR.

| Order | Deferred slice | Dependencies | Schema migration | Public API | Unity client | Frontend BFF/UI | Email provider / infrastructure |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | Email attachment and verification | Implemented by issue #58 with optional verified recovery email; verified-address change stays in #64 | Yes: `EmailVerifiedAt` plus dedicated challenge table | Yes | No initial change | Future account-security UI may consume the additive API | Yes |
| 2 | Authenticated password change | Reauthentication and refresh-session revocation policy | Likely no for password replacement; reassess if password history/audit is required | Yes | Only if exposed in Unity | Yes | No, unless notifications are selected |
| 3 | Forgotten-password recovery | Requires an approved verified recovery factor; normally slice 1 | Yes for hashed, expiring, single-use reset state unless an approved provider owns it | Yes | Only if recovery is offered in Unity; a web handoff may suffice | Yes | Yes |
| 4 | Account data export | Approve data scope, shared-record privacy, format/delivery, retention, and requester verification | Not necessarily for a synchronous current-data export; yes if jobs/artifacts/audit state are required | Yes | Not required unless product selects in-client export | Yes | No; separate job/object storage may be needed if async |
| 5 | Account deletion or anonymization | Select erasure/retention model and shared-history treatment; define grace/reactivation/audit rules | Yes | Yes | Required only if self-service is exposed there; session teardown still needs handling | Yes for web self-service if selected | Optional notification delivery, not inherently required |
| 6 | Cross-game profile/game scoping | Only an explicit product reversal of the current shared-profile/social requirements | Yes, broad relational migration | Yes, versioned | Yes | Yes | No |

Password change is not technically dependent on email verification and may be scheduled in parallel
after its own requirements resolve. Password recovery is dependent on an approved verified factor.
Deletion/anonymization must not proceed as a cascade tweak; it requires a data-by-data migration and
retention plan first.

## Architectural constraints

- Preserve `Domain <- Application <- Infrastructure/API`.
- Keep private/security `Account` identity separate from public/game-facing `PlayerProfile`.
- Never collapse Account and PlayerProfile.
- Never use Unity local profile IDs as online AccountId or PlayerId values.
- Do not restore legacy V1 identity or migration behavior.
- Do not add `GameId`, per-game profiles, or game scoping speculatively.
- Do not create a shared-account microservice without a concrete deployment/domain requirement;
  the modular-monolith boundary is sufficient.
- Preserve the current stateless access-token policy unless a stronger, explicitly approved
  requirement justifies its operational cost.

## Explicit non-goals of this requirements baseline

This work does not implement or approve:

- verification emails or an email provider;
- password-history, breached-password, MFA, or immediate access-token revocation systems;
- account deletion or anonymization;
- export generation;
- OAuth/social login or new auth providers;
- MFA;
- username changes;
- account merging;
- `GameId` schema additions or per-game profiles;
- a shared-account microservice;
- V1 account migration;
- Unity lifecycle UI;
- frontend lifecycle UI.

No production code, schema migration, OpenAPI contract, Unity production file, or frontend runtime
behavior is changed by this document.
