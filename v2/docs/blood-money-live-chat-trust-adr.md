# Trusted live multiplayer chat identity and delivery (MSG-009 / #91)

Status: **proposed architecture; live trust gate NOT PASSED**. This document and the
isolated [probe](../spikes/Msg009.TrustProbe/README.md) are specification/spike work only.
They do not approve MSG-010/#92 or Unity MSG-011/#110 implementation. Acceptance requires
security review and the provider evidence identified below. No production endpoint or
membership authority is introduced by this change.

## Baseline and existing owners

Fetched `origin/dev` on 2026-10-11 (America/Chicago). Open-PR lists in all three
repositories were empty during this work.

| Repository | Audited dev SHA |
| --- | --- |
| Level5Backend | `247b7096b072b5d75592ad01c34af64c58b2c0ee` |
| Blood Money | `6d3d4416ea58bd79888c133e6fb7d8bd914478ed` |
| Platform | `9f2ba2c8836fb9ca9534a921755586bf0b0a239b` |

The [challenge chat contract](blood-money-chat-contract.md), [HTTP API](blood-money-chat-api.md)
and [identity boundary](account-lifecycle-and-cross-game-identity.md) remain authoritative
in their scopes. Backend `ICurrentPlayerProvider` resolves authenticated Account to PlayerId.
Unity's application-owned `BackendSession` owns Backend authentication and cancellation.
UGS `UgsMultiplayerSessionService` uses anonymous Unity Authentication and MPS 2.3.3;
`SessionConnectionProof` authorizes UTP generations to provider members at the listen host.
Its host-readable nonce is not independent account evidence. `ParticipantId` is match identity.

Quick Chat stays phrase-only, transient and host-relayed. Durable challenge chat stays
challenge-scoped, with its canonical immutable participant roster. Neither roster authorizes
live multiplayer free text. Platform's existing pinned contract and BFF are not a browser-to-Unity
credential broker. No Platform changes are required for this candidate.

## Decision proposed for verification

Prefer **direct authenticated pairing plus a Backend-originated provider membership read**,
using the existing UGS provider. Do not route free text or account credentials through the host.
This is a per-room pairing of two authenticated principals, not a permanent account-linking product.

Unity documents JWT verification with its fixed JWKS endpoint and verification of issuer,
lifetime and project/subject claims. Lobby documents authenticated Get details with the member
list and project/environment identifiers. MPS uses Lobby underneath its session abstraction.
These establish a credible candidate, not proof of the configured project's behavior:

- [Unity client authentication and token validation](https://docs.unity.com/en-us/services-web-apis/client-auth)
- [Lobby API: Get lobby details](https://docs.unity.com/en-us/oas-lobby/1.0.0)
- [MPS glossary: underlying Lobby](https://docs.unity.com/en-us/mps-sdk/glossary)
- [MPS session properties: service membership versus host metadata](https://docs.unity.com/en-us/mps-sdk/session-properties)

The proposed Backend request carries its ordinary Backend bearer credential and a short-lived
UGS access token over TLS. Backend derives PlayerId from its authenticated caller and UGS subject
from the independently verified Unity token. It reads the requested private Lobby directly using
that UGS token, checks both configured project/environment scopes, and requires the signed subject
in the provider-owned member list. A lobby ID is only a locator. No sender ID, participant ID,
display name, host roster, private player property or local phase supplies authority.

The probe requires a deliberately strict **candidate** Unity claim profile: RS256 with a trusted
Unity key, exact issuer, expiration/not-before validation, one nonempty subject, matching
`project_id`, and audience entries for the configured project and environment (`upid:`/`envId:`).
The audience representation must be confirmed in the approved environment; a mismatch blocks
adoption rather than enabling a permissive decoder. Use supported IdentityModel validation,
never client-decoded claims or token-selected key URLs. No UGS refresh/session token is submitted
to Backend. Existing UGS authentication continues to own its refresh lifecycle.

```mermaid
sequenceDiagram
    participant C as Unity client
    participant B as Backend V2
    participant U as Unity Authentication / Lobby
    C->>B: Backend authentication + UGS access token + room locator
    B->>B: Resolve current eligible Backend PlayerId
    B->>U: Fetch trusted signing keys; read current provider membership
    U-->>B: Provider-authenticated identity and membership evidence
    B->>B: Validate scope; serialize room and immutable account/member pairing
    B-->>C: Chat-specific authorized projection or safe rejection
    Note over C,B: No listen-host credential or text relay
```

This proves concurrent possession of two credentials and observed provider membership. It does
not prove civil identity, historical account ownership, active UTP connectivity, honest gameplay
or a permanent association between anonymous UGS identity and Backend identity. Compromised or
deliberately shared bearer credentials remain outside the host-impersonation guarantee.

## Alternatives and why they are not selected now

| Option | Assessment |
| --- | --- |
| Host-signed roster, transport nonce or client PlayerId | Reject. The adversary controls the signer/relay or the claim. |
| Dual authentication with fresh provider read | Candidate with the smallest impact on the existing anonymous UGS/session flow. Needs live scope, membership and lifecycle proof. |
| Server-owned session admission using a UGS service account | Fallback if ordinary provider reads cannot supply adequate evidence. Requires least-privilege credentials and access policies; not authorized as an incidental transport migration. |
| Backend-issued Unity Custom ID sign-in | Credible future durable linkage, but changes existing UGS authentication/link/recovery behavior and adds a credential issuance responsibility. Not needed merely to test this candidate. |
| New identity provider, chat broker or dedicated game server | No demonstrated requirement; defer. |

Unity documents [service-account controlled sessions](https://docs.unity.com/en-us/mps-sdk/access-control)
and [Custom ID sign-in](https://docs.unity.com/en-us/authentication/platform-signin/custom-id).
Neither is assumed configured. Service-account impersonation must never accept a caller-selected
player identity; introducing it requires a separately reviewed server-owned mapping.

## Backend-owned room and binding rules proposed for MSG-010

Use a narrowly separate live-session chat resource in the Blood Money module and existing database.
It has no challenge ID, financial reference or Core participant dependency. Reuse concrete text
normalization/safe projection behavior only after extracting an actual shared need; do not convert
both systems into a generic conversation framework.

The room key includes configured provider project/environment, verified provider lobby ID and a
server-generated room epoch. Pin the original provider host from independently fetched membership
when that authenticated host registers the room. Other members cannot nominate an original host
or create a room by presenting an arbitrary valid lobby. Provider metadata proves no game-mode
honesty; eligibility here is private, capacity-five provider session membership, not match results.

Serialize each room's registration, revocation, generation changes and message writes using the
existing PostgreSQL deployment. Within an epoch enforce unique `(room, UGS subject)` and
`(room, Backend PlayerId)` bindings. Only the directly authenticated caller can establish its
pair. Exact registration replay recovers the same binding; changed account/subject conflicts.
Account replacement cannot silently relink a retained UGS membership. Reconnecting renews a
server binding generation without changing the pair; in-flight operations compare that generation
at commit. Never turn the binding record into a second provider roster or transport authority.

Every read, send, report and preference operation rechecks Backend eligibility and the current
binding. Provider authorization is checked independently, not inferred from `MultiplayerPhase.Active`.
Provider timeout, refusal, rate limit, malformed response, unknown key or scope mismatch fails
chat authorization closed. A cached positive membership result grants no new operation during
provider outage. Provider credentials remain in request memory only, with bounded lifetime;
no credential column, general logs, tracing baggage or host-visible property is permitted.

### Important unresolved consistency boundary

UGS membership observation and a PostgreSQL chat commit cannot share a transaction. A member can
leave after the provider response and before a send commits. A successful Get also cannot reveal
an unobserved remove/rejoin or host-away/host-back interval between observations. Do not claim
linearizable revocation or label a database binding generation as a provider membership generation.

Before approval, choose and test either a documented bounded observation policy with an accepted
revocation window, or server-owned admission/revocation that serializes the authorization lifecycle.
The current candidate proposes a fresh provider read per operation and generation checks at commit,
but **does not close this decision**. Polling/events alone cannot prove every intervening transition.
If immediate membership revocation is required, this candidate is insufficient and implementation
must stop pending the server-owned design. There is no permissive fallback.

## Lifecycle and failure policy proposed for review

| Transition | Required Backend behavior / remaining proof |
| --- | --- |
| Lobby / first registration | Only verified original host opens the epoch; each human establishes its own direct pair. CPU seats have no account/chat binding. |
| Match start / active play / results | Same provider session and room may continue. Local phase is presentation only; it cannot grant access or create authoritative game notices. |
| Reconnect wait | A retained verified member may use chat only under the independently approved membership policy; transport disconnection alone creates no Backend identity proof. |
| Reconnect completion | Revalidate both principals and provider membership; issue a new server binding generation; reject late old-generation work. Undetected leave/rejoin is an approval blocker above. |
| Rematch / return to lobby | Same approved surviving room can continue; new provider session means a new room, epoch and independent bindings. Old message IDs/cursors cannot cross rooms. |
| Explicit leave / account sign-out | Revoke the caller's binding through Backend; expire on loss of renewal if the client cannot acknowledge. Other members cannot revoke or relink that account through local roster assertions. |
| Removal / account restriction | Revoke on authoritative observation/current restriction. No new access after revocation; never change gameplay or financial state. Commit races follow the consistency decision above. |
| Host change / deletion | Observed original-host replacement or provider deletion ends the room irreversibly; no promotion or archive access is inferred. |
| Host transport loss / crash | Provider membership alone is insufficient; require the independently verified host-liveness policy described below. |
| Scene/app teardown | Cancel view requests and release input leases. Best-effort revocation plus server expiry handles a dead client; UI disposal is not authoritative server cleanup. |
| Backend/UGS outage or expired authentication | Chat unavailable/reauthentication state only. Preserve MPS, UTP, roster, engine, turn, RNG, cylinder, results, profile and economy. |

**Host liveness remains an explicit approval gate.** Unity says an inactive Lobby can still be
returned by Get; existence is not liveness ([heartbeat behavior](https://docs.unity.com/lobby/heartbeat-a-lobby)).
A candidate Backend-owned host renewal lease could require direct authenticated renewal by the
immutable host binding and fresh provider observation, with a proposed 15-second cadence and
45-second expiry. It would bound ordinary crash detection, not prove an honest host's UTP health.
Expiry closes the room permanently; renewal cannot resurrect it. No host lease is implemented
or approved here. The required crash/liveness bound, fairness/availability cost and stronger
Relay evidence, if required, must be accepted before MSG-010. Client reports may close their own
chat but never prove another account's identity or force game abandonment.

## Delivery, moderation, privacy and capacity

Propose persisted Backend messages with bounded HTTP polling first. This matches existing
deployment and retry mechanisms and adds no new realtime infrastructure. A five-second visible-view
poll cadence means up to 60 poll requests/minute for five humans, before sends, reconciliation and
membership reads. With fresh provider checks, provider demand is at least the authorized-operation
count; ordinary rate limits, subscription cost, latency and failure budgets must be measured. A
server-owned observation cache would change authorization semantics and needs explicit approval.
If the required measured latency/capacity is incompatible, revisit a specific supported server
delivery channel; do not add a speculative WebSocket/broker now.

MSG-010 must publish actual room/binding operations and message/read/report schemas in generated
OpenAPI before a Unity adapter is written. Server owns attribution and per-room committed ordering.
One logical send keeps one sender-scoped client GUID and immutable normalized body through lost
acks; exact replay succeeds once, changed intent conflicts. Pending UI is not authoritative history.
Use bounded opaque, room/epoch-scoped history and resume cursors. Bound pages, loaded client history,
room creation, active rooms/account, sends and retained data in the approved implementation contract.
The existing challenge rate/page defaults are reference points, not automatically live-room policy.

Reports must retain immutable server sender/account/member/room provenance under protected access.
Challenge notification mute is not a sender block; Quick Chat local mute is not server suppression.
Live-room mute/report/suppression/restriction semantics must be published with MSG-005. Do not invent
account-level block, report review status or a retention duration. Persisting free text needs approved
ordinary retention/account-erasure policy and operational report handling before production enablement.
Product/privacy review must also approve the pre-entry persistence/reporting disclosure and applicable
consent policy; this proposal creates no consent assertion or new account preference on a player's behalf.
No Platform inbox producer is proposed for transient online-session activity.

Keep bodies inert plain text and out of telemetry. Never include credentials, private account data,
hidden cylinder/RNG state or financial/ledger data in chat projections. System/game/financial notices
come only from separate authoritative domain projections. A player message never acquires that trust.

## Required implementation slices and evidence gates

1. **MSG-009 acceptance:** execute the isolated provider probe and adversarial lifecycle runbook;
   pin actual claim profile and MPS-to-Lobby identity mapping; resolve membership consistency and
   original-host liveness with security/product review. This is not satisfied by offline fixtures.
2. **MSG-010 trust slice:** implement only the accepted verifier, room/binding registration and
   revocation/lease policies in Blood Money using current Backend account authority. Add PostgreSQL
   race/restart tests. Publish the actual generated API; no guessed client routes.
3. **MSG-010 message slice:** implement ordering, durable retries, history/resume, safe moderation
   projections and failure behavior with real concurrent 2- and 5-human tests and capacity evidence.
4. **MSG-005 release slice:** implement/certify restricted operator review/actions, current
   communication restrictions, protected evidence/audit and approved retention/erasure.
5. **MSG-011:** fresh audit, then a narrow Unity adapter using existing `BackendSession`, scoped
   input leases and approved lifecycle only. Certify isolated real clients before acceptance.

The local Docker inventory contains a running `level5-postgres-local` database, not a Backend API
container. A locally run V2 API against it is suitable for controlled Backend tests; no hosted
Backend is required. A real linked UGS project/environment and sessions are still needed. Unity's
committed `cloudProjectId` is empty. No secrets, live accounts or provider sessions were created.

## Review and evidence record

Security review of this proposal identified the provider-read/commit race, undetected leave/rejoin,
host-back transitions and Get-on-inactive-Lobby limitation. They are explicit acceptance blockers,
not hidden behind fixture passes. Scope review keeps the probe outside the production solution,
avoids durable account-linking or transport migrations, and leaves Core, challenge chat, Quick Chat,
economy, shared auth and Platform untouched.

The probe's offline checks exercise real fixture signatures and rejected scope/member/account/origin
responses. They do not test provider behavior, durable registration, replay resistance of a deployed
binding, host liveness or messaging. Record executed results in the probe runbook; retain #91/#92/#110
as blocked until the relevant live/approval gates actually pass.
