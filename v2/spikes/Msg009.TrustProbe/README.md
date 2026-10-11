# MSG-009 isolated account/member trust probe

This executable investigates the candidate in the [trust ADR](../../docs/blood-money-live-chat-trust-adr.md).
It is outside `Level5BackendV2.sln`, has no production project references and creates no binding,
account, session, message or database record. Its only package uses the existing repository's
IdentityModel version. Do not embed this spike as a production authentication owner.

## Offline checks

From the repository root:

```text
dotnet run --project v2/spikes/Msg009.TrustProbe -- --self-test
```

The checks use fresh RSA fixture keys and actual JWT signature/lifetime validation. They cover
trusted and attacker keys with the same key ID, wrong issuer/project/environment and mismatched
project claim, expired/future/unsigned tokens, missing expiration/subject, disallowed signature
algorithm, a host nonce substituted for a token,
removed/different member, changed host, wrong room/environment, nonprivate/oversized lobby,
duplicate member IDs, valid/different/disabled Backend account, duplicate JSON properties and
Backend origin restrictions. A pass is offline evidence only.

## Live observation inputs

Use two isolated Unity clients with separate UGS Authentication profiles in an approved non-production
project/environment. Link and certify #32's provider setup first. Authenticate each participant
with the local V2 API through the existing client auth flow. No hosted Backend is needed.
The local Docker container `level5-postgres-local` is the database; the V2 API must run separately.
Do not treat database availability as proof that the HTTP API is running.

Supply these environment variables to the probe process using the approved local secret mechanism.
Do not paste credentials into chat, command-line arguments, logs or evidence files:

| Variable | Input |
| --- | --- |
| `MSG009_BACKEND_ORIGIN` | Approved bare API origin; HTTPS normally, or numeric-loopback HTTP with `--live-local` |
| `MSG009_BACKEND_ACCESS_TOKEN` | Current Backend access token; never a refresh token |
| `MSG009_EXPECTED_BACKEND_PLAYER_ID` | The independently known test account PlayerId, not a Unity ParticipantId |
| `MSG009_UGS_ACCESS_TOKEN` | Current Unity Authentication access token; never the UGS session/refresh token |
| `MSG009_PROJECT_ID` | Approved Unity project UUID |
| `MSG009_ENVIRONMENT_ID` | Approved non-production environment UUID, not its display name |
| `MSG009_LOBBY_ID` | Actual provider Lobby ID for this MPS session; confirm the mapping rather than guessing |
| `MSG009_EXPECTED_UGS_HOST_ID` | Original host subject captured from the independently observed test setup |

```text
dotnet run --project v2/spikes/Msg009.TrustProbe -- --live-local
```

`--live-local` permits HTTP only at `127.0.0.1` or `[::1]` for the local Backend. It does not relax
Unity TLS, redirect, scope or signature checks. `--live` requires HTTPS for Backend too. The tool
never changes TLS validation, follows redirects, persists tokens, refreshes credentials or prints
response/error/exception bodies. Read-only HTTP calls have a total 15-second cancellation budget,
64 KiB response cap and bounded JSON depth; non-200, non-JSON and duplicate properties fail closed.

The probe calls only existing `GET /api/v2/me`, Unity's documented fixed JWKS URL and Lobby's
documented `GET https://lobby.services.api.unity.com/v1/{lobbyId}`. It checks current Active Backend
identity against the expected test account, verifies the candidate Unity claim profile, and checks
the returned private five-slot lobby's scope, original host and signed-subject membership.
It sends no impersonation header, host proof, text or service-account credential.

Exit 0 in live mode means **one successful observation**, not gate acceptance. Exit 2 means blocked
configuration, rejected evidence or unavailable service; no raw diagnostics containing tokens are
printed. Unknown claim representations must be investigated through protected provider evidence;
do not weaken validation until the exact profile is independently verified and reviewed.

## Required isolated live matrix

Record source/probe commit, Unity/MPS versions, approved configuration references and timestamps.
Use pseudonymous fixture labels in retained evidence; keep tokens, private account details, join codes
and raw member payloads out of general logs. Live evidence belongs in the approved restricted store.

| Case | Required evidence |
| --- | --- |
| Host and member, then five humans | Separate processes; each directly authenticates and observes its own actual provider membership and Backend account. |
| Claimed victim subject / copied host nonce | Host's own token cannot become victim identity; nonce cannot replace a signed token. Editing lobby metadata cannot create authenticated victim attribution. |
| Scope and expiry | Other project/environment token, expired token, wrong room, nonmember and disabled Backend caller fail. |
| MPS/Lobby correlation | Independently confirm session-to-Lobby mapping for pinned MPS 2.3.3; no arbitrary property supplies it. |
| Remove / rejoin / retained reconnect | Observe rejection after removal and correct reconnect behavior. Determine whether a provider membership incarnation can distinguish an unobserved remove/rejoin. If not, retain the consistency blocker. |
| Host crash / host replacement / host-back | Prove the agreed detection bound. Get-on-inactive-Lobby is not a pass. A server renewal lease is only a proposal until implemented/reviewed. |
| Provider failure and key changes | Fail closed on timeout, 403/404/429/5xx, malformed response and unknown signing key; verify bounded key refresh behavior in the eventual verifier. |
| Backend account replacement | The eventual binding must reject changing PlayerId for retained UGS membership and reject old-generation asynchronous writes. This stateless probe does not implement that binding. |

Concurrent registration, lost-ack replay, generation/revocation commits and server restart require
the later accepted MSG-010 database implementation; this read-only probe cannot certify them.
Real text delivery/moderation and gameplay isolation remain later Backend/Unity acceptance work.

## Execution record

2026-10-11 final evidence:

| Check | Result |
| --- | --- |
| Release-mode offline probe | PASS, 32 assertions, including unsigned/missing-expiry/project-claim rejection |
| Standalone Release build | PASS, zero warnings/errors |
| Resolved transitive NuGet vulnerability check | No vulnerable packages reported by current configured sources |
| Unconfigured `--live-local` attempt | Exit 2 / BLOCKED before HTTP calls; no credentials/configuration supplied |
| Full Backend/Unity suites | NOT RUN; no production/solution/Unity source changes |
| Live UGS/Backend observation | NOT RUN / BLOCKED: no linked UGS project/environment or real authenticated session supplied |

Security review added final token-lifetime revalidation after the membership response and explicit
unsigned/missing-expiry/mismatched-project-claim checks. It also retained provider-read/commit,
unobserved membership changes and host liveness as unresolved acceptance gates. Scope review
confirmed the spike is outside the production solution and changes no existing auth, transport,
gameplay, chat, database or Platform implementation. The initial 22- and 29-check passes preceded
the final additional checks; only the 32-check result describes the final probe source.

Docker inspection found `level5-postgres-local` running; no Backend API container exists in the
current running/stopped container inventory. No hosted Backend is required or assumed.
