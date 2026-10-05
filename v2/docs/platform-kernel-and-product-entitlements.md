# Shared platform kernel and product entitlements

Status: accepted for Backend V2 issue #61

Audited: 2026-10-05

This decision establishes the smallest shared product-access capability needed by the Sweat This
platform. It is additive: existing Level 5 APIs and clients continue to work without an
entitlement, and this document does not define a generic game framework.

## Audited baseline

The current `origin/dev` refs were fetched before implementation:

| Repository | Audited `dev` SHA |
| --- | --- |
| `sweat-this/Level5Backend` | `6efa7946e551499e8f9d80c83490a7b04c34607a` |
| `sweat-this/level5` | `c4353a905d845f65e83b0a1c253168e7264d4b8b` |
| `sweat-this/level5frontend` | `aa9636d2fa96e8f458ffed2096cbf65c3089715d` |

Backend issues #57, #61, #62, and #63 were re-read at that baseline. A current organization-wide
code search found no existing product-entitlement source of truth. Unity's `UnlockSnapshot`, local
SQLite character progression, and authored level/character `Locked` flags are gameplay progression,
not product ownership.

## Identity and ownership

- `AccountId` remains the private authentication/security identity and JWT subject.
- `PlayerId` remains the canonical shared gameplay and social identity backed by the public
  `PlayerProfile`.
- A product entitlement belongs to `PlayerId`. No second player identity is introduced.
- Existing global player-profile and social ownership remain unchanged.

## Product identifier

`ProductId` is an immutable, opaque, persisted machine identifier. It is canonicalized to lowercase
and accepts at most 64 ASCII letters/digits with single hyphens between segments. Its text does not
encode a hierarchy or behavior and must not be parsed to infer either.

The canonical identifier for the existing Level 5 base product is:

```text
level5
```

No speculative DLC or future-game identifiers are declared. A separate `GameId` is not introduced:
issue #61 needs an access-bearing product identifier, while issue #62 owns future module/API-routing
identity. Conflating those concepts now would make DLC/add-on access and module identity the same
thing without a requirement. No Product/catalog table is added because display metadata, pricing,
localization, store SKUs, and product relationships are out of scope.

## Entitlement lifecycle

There is one current `ProductEntitlement` per `(PlayerId, ProductId)` with:

- `Kind`: `Owned`, `Demo`, `Playtest`, or `Beta`;
- `GrantedAt`;
- optional `ExpiresAt`;
- optional `RevokedAt`;
- optimistic-concurrency `Revision`.

`Revoked` is not a kind. Effective access at time `now` is exactly:

```text
RevokedAt == null && (ExpiresAt == null || now < ExpiresAt)
```

An expiry equal to `now` is inactive. No background expiry job or kind-specific expiry rule exists.
A regrant deliberately replaces the current kind/grant time/expiry, clears revocation, and advances
the revision; there is no kind precedence. Revocation is idempotent. Competing mutations use a
revision-conditioned update and cannot silently last-write-win. This is a current-state model, not
an entitlement history or external-source reconciliation ledger.

## Persistence and authority

`product_entitlements` uses the natural primary key `(PlayerId, ProductId)`. `PlayerId` has a
restrictive foreign key to `player_profiles.Id`; the key's player-first ordering supports exact
lookup and list-by-player without a speculative secondary index. No existing player is backfilled
or automatically granted `level5`.

Grant/regrant/revoke mechanics exist as trusted Platform application operations, but no mutation is
mapped to HTTP. There is not yet a trusted store-sync, provisioning, or administrative authority,
and legacy development claims are not such an authority. A future integration must supply that
trust boundary rather than allowing an ordinary authenticated player to self-grant access.

## Self-query API

Authenticated reads are additive and derive `PlayerId` from the server-side current-player
provider; neither request accepts a player identity:

- `GET /api/v2/platform/me/entitlements` lists active current entitlements only.
- `GET /api/v2/platform/me/entitlements/{productId}` returns a normal access answer for entitled,
  missing, expired, or revoked state.

Responses expose product ID, effective access, active kind, and optional expiry only. They do not
expose `AccountId`, revision, grant/revocation timestamps, or internal security metadata.

## Boundary and explicit non-enforcement

`Level5.Domain.Platform` cannot depend on the current game domains (`Competition`, `Results`, or
`Leaderboards`), and `Level5.Application.Platform` cannot depend on their Application namespaces.
Platform may use the genuinely shared `PlayerId`.

Product entitlement is not Level 5 progression or unlock state. No Unity character/level unlock
logic, local persistence, authored `Locked` flag, or frontend production code changes as part of
this decision. Authentication, profiles, friends, correspondence, match-result submission,
leaderboards, and every existing Unity workflow remain ungated. Any future enforcement requires a
separate integration issue defining product mapping, existing-player/backfill policy, failure
contract, and client UX.
