# Blood Money friend-challenge details API

Implemented against fetched `origin/dev` at
`9d2483f88dbd802b7e77c7d37ddf4a3622d28e62` on 2026-10-10. No Backend PRs were
open at implementation start. The [challenge contract](blood-money-challenge-contract.md)
owns the lifecycle and roster; this HTTP adapter exposes its existing
`GetBloodMoneyChallengeUseCase` projection.

## Request and authorization

`GET /api/v2/games/blood-money/challenges/{challengeId}` requires bearer authentication.
`challengeId` is the canonical challenge UUID, also used by the
[chat routes](blood-money-chat-api.md). The actor is resolved through
`ICurrentPlayerProvider`; no player, account or actor request field grants access.
Only an existing roster member may read the projection. Creator, invited, accepted and
declined participants retain the existing membership-based access, independent of current
friendship or challenge status.

| Result | Meaning |
| --- | --- |
| 200 | Participant-authorized projection, with `Cache-Control: no-store` |
| 401 | Missing or invalid bearer authentication; empty response body |
| 404 `not_found` | Missing challenge or caller is not a participant; identical safe title |
| 503 `service_unavailable` | Persistence unavailable; existing safe retryable ProblemDetails |
| 500 `internal_error` | Unclassified failure; no exception details returned |

This endpoint follows the standard Backend bearer/account identity boundary. Chat retains
its separate current-account eligibility checks and activation requirements. Reading details
does not itself authorize messaging, wagers, results or settlement. A pending or terminal
challenge can be readable while its chat is unavailable.

## Response

The response is the existing application projection mapped to public JSON primitives:

| Field | Wire type |
| --- | --- |
| `challengeId`, `creatorPlayerId` | UUID strings |
| `status` | `PendingAcceptance`, `Active`, `Declined`, `Cancelled`, `Expired` |
| `stakePerParticipant`, `revision` | Signed 64-bit integers |
| `rulesetId`, `rulesetVersion` | String and signed 32-bit integer |
| `createdAt`, `acceptanceDeadlineAt` | Date-time strings |
| `activatedAt`, `gameplayDeadlineAt`, `terminalAt` | Nullable date-time strings |
| `participants` | Canonical ordered roster array |

Each participant contains `playerId` (UUID), `seatIndex` (signed 32-bit integer), `status`
(`Invited`, `Accepted`, `Declined`) and nullable `acceptedAt`. The creator remains at seat
zero. No client-derived roster, display identity, UGS identifier or future lifecycle status
is introduced. Current creation admits 2–4 participants; this read projection preserves the
durable roster without imposing a new maximum.

Account IDs, create request keys, terminal actor internals, balances, reservations, ledger
operation IDs and postings are excluded. Reads perform no lifecycle, financial or chat writes.
An elapsed acceptance/gameplay deadline does not synthesize a transition or release stakes.
Only the existing lifecycle/settlement owners may change those records.

## OpenAPI and visibility correction

The canonical [OpenAPI document](../openapi/level5-v2.openapi.json) is generated with the
existing `Level5.Api.OpenApiExport` host tool. It publishes the details route, typed success
and error responses, UUIDs, nullable timestamps, 64-bit values and named challenge/participant
statuses. Registering this read use case requires no timing policy or new lifecycle writer.

`BloodMoneyChatMessageDto.visibility` already serializes as `Visible` or `Suppressed` at
runtime. Its schema now also declares that exact string enum, replacing the incorrect
integer enum. Runtime chat JSON is unchanged, including null bodies for suppressed tombstones.
The existing report-reason schema remains a string enum with its original values.

## Validation

`BloodMoneyChallengeFlowTests` exercises the real HTTP host and PostgreSQL for 2–4 player
projections, every existing lifecycle status, canonical ordered roster and timestamp mapping,
private-field exclusion, unchanged challenge/financial snapshots, past-deadline reads,
unauthenticated access, spoofed actor inputs, safe missing/nonparticipant 404s, persistence
outage 503s and live Swagger metadata. The chat Swagger regression checks visibility's string
type and exact values; existing HTTP flows verify both visible and suppressed runtime values.

The 12 new details cases failed against the original API; the strengthened visibility
regression separately failed with expected `string`, actual `integer`. PostgreSQL fixture
timestamps use its microsecond precision so the projection assertions compare exact values.
Local results are recorded in the API project's `TestResults/challenge-details-final.trx`.
Contract generation and drift validation use the existing export tool; no contract is hand-edited.

| Local check | Result |
| --- | --- |
| Full API integration suite | 288 passed, 0 failed, 0 skipped |
| Final focused details contract suite | 12 passed after refining empty-401 metadata |
| Architecture suite | 30 passed, 0 failed, 0 skipped |
| API/exporter managed build | Passed |
| Generated contract vs second fresh export | Byte-identical |
| `git diff --check` | Passed |

The full API run precedes the final 401 documentation refinement; the final focused suite
rechecks the details route after it. Its result is `TestResults/challenge-details-contract-final.trx`.
Architecture evidence is in that project's `TestResults/challenge-details-final.trx`.
These are local HTTP/PostgreSQL checks, not hosted CI, production deployment or operator evidence.

This change does not implement challenge create/lifecycle HTTP endpoints, Unity UI, Platform
integration, realtime messaging, operator moderation or retention/erasure policy. Those
downstream contracts and release gates retain their existing owners.
