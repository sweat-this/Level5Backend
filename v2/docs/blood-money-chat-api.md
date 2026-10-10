# Blood Money authenticated challenge chat API (MSG-003 / #88)

Follow-on [MSG-004 / #89](blood-money-chat-notifications.md) adds atomic coalesced Platform
inbox production through the existing API. The MSG-003 evidence and exclusions below describe that original slice.

## Baseline and ownership

All three `origin/dev` refs were re-fetched on 2026-10-10 (America/Chicago) immediately
before implementation. Live Backend #88 and all open PRs across the three repositories
were inspected; no PRs were open.

| Repository | Audited dev SHA |
| --- | --- |
| `sweat-this/Level5Backend` | `4926e474673c0e7b03cdd39d991b0967face0b94` |
| `patrickrcharles/bloodmoney` | `95c92cc3c0c3c188fe7f8cf6502042194a768d80` |
| `sweat-this/platform` | `9f2ba2c8836fb9ca9534a921755586bf0b0a239b` |

The [module entry](blood-money-module-entry.md), [challenge contract](blood-money-challenge-contract.md),
[chat contract v1](blood-money-chat-contract.md) and [MSG-002 persistence](blood-money-chat-persistence.md)
remain authoritative. This slice exposes their existing durable authority; it adds no
challenge lifecycle, financial writer, conversation/roster, notification producer or client UI.

## HTTP boundary

Every route below is relative to `/api/v2/games/blood-money/challenges/{challengeId}`.
All require bearer authentication. The controller resolves `PlayerId` only through
`ICurrentPlayerProvider`; none of the input DTOs has an actor field.

| Method and route | Input | Success |
| --- | --- | --- |
| `GET /messages` | Optional `limit` (default 50, 1–100), `cursor` | 200 page |
| `POST /messages` | `clientMessageId`, `body` | 201 new committed message; 200 exact normalized replay |
| `PUT /read-position` | `lastReadSequence` | 200 effective caller position |
| `PUT /notifications-muted` | `notificationsMuted` | 200 effective caller boolean |
| `POST /message-reports` | `messageId`, `reason` | 201 opaque `reportId` only |

The narrow API resource filter checks the current Account through `IAccountStore` before
model binding and challenge lookup. Active continues; Disabled or unavailable account
identity yields uniform `403 chat_communication_restricted`. Persistence outages propagate
through the existing safe `503 service_unavailable` handling. This does not change global
JWT validation or introduce communication-restriction persistence.

For eligible callers, canonical unknown, nonparticipant and never-activated challenges
share `404 not_found`. Cursor/limit and report reason/target validation happen inside
the authorized store operation. Malformed JSON/DTO shape yields a bounded, generic 400
with the operation's code, without reflecting input or serializer diagnostics. Report reasons
are exactly `Harassment`, `Hate`, `Threat`, `SexualContent`, `Spam`, `Other`; numeric,
unknown and differently cased values are invalid. Invalid enum semantics are deferred
through the HTTP converter until challenge authorization.

The existing send use case still owns Unicode/newline/NFC normalization and immutable retry
intent. The host composes the approved persistent rolling budgets: 5 accepted sends/10 seconds
and 30/minute per player/challenge. Exact retries precede rate charging. The error handler maps
all normative chat errors explicitly, including `429 chat_rate_limited` with ceiling-rounded
seconds in `Retry-After`; unknown chat/internal errors remain safe 500s. The former internal
read error `invalid_chat_read_sequence` is aligned to `invalid_chat_read_position`.

Messages expose only the eight normative fields (message/challenge IDs, sequence, sender
PlayerId, client key, nullable body, timestamp and visibility). Pages expose only items,
older/resume cursors, committed latest sequence and the caller's read/mute state plus
read-only lifecycle. Sequence/read values use signed 64-bit integers. No unread arithmetic
or other participant read receipts are added. Responses use `Cache-Control: no-store`.
Suppressed bodies are projected to null in the list SQL and in existing send replay;
report responses never contain evidence.

## Consistent pages and cursor deployment

`BloodMoneyChatStore.ListAsync` executes authorization, lifecycle, maximum committed sequence,
page contents and private state within one PostgreSQL read-only RepeatableRead transaction.
It takes no challenge `FOR UPDATE` lock. A deterministic regression pauses the snapshot while
another scope commits a send/read/mute update, proving those writers finish and the page
retains its original coherent view.

Initial history returns the newest bounded slice at high-water H, ascending. Older traversal
fixes H and uses an exclusive sequence boundary. Its resume cursor stays after H even if new
messages have arrived. Resume traversal returns the earliest bounded slice after the delivered
boundary, advancing only to the last returned sequence; empty pages retain their boundary.
Suppressed tombstones and sequence gaps participate in ordering and progress.

The dedicated cursor is canonical base64url of a fixed-width V1 payload (version, direction,
challenge UUID, signed 64-bit boundary and snapshot upper bound) followed by HMAC-SHA256.
The current encoding is 88 ASCII characters, beneath the 1,024-character bound. Decode bounds
and canonicalizes input, checks the MAC in constant time, and validates version, direction,
bounds and challenge binding. Tokens contain no actor identity, body, read state or finances.

Composition derives the signing key from the deployment's existing **server-owned `Jwt:Key`**
using HMAC-SHA256 with purpose `Level5/BloodMoney/ChatCursor/v1`. This keeps cursor keys stable
across restarts and instances sharing the configured secret, without a committed secret,
per-process random key or new Domain configuration. JWT secret rotation invalidates old
cursors; clients recover through a fresh authorized initial list. Every instance must use
the same server secret. Existing JWT startup secret validation remains in force.

## Minimal immutable report intake

MSG-001 assigns authenticated intake to MSG-003 and protected operator handling to MSG-005.
The minimal report table reconciles that split: `ReportId`, `ChallengeId`, `MessageId`,
`ReporterPlayerId`, bounded `Reason`, server `ReportedAt`. There are no review states,
operator comments, sanctions, moderation actions or retention jobs.

Migration `20261010183439_AddBloodMoneyChatReports` uses the existing DbContext/migration stream.
A composite restrictive FK retains the message in its actual challenge; another references
the canonical reporter roster row. A unique index enforces one report per challenge/message/
reporter. Intake shares the existing challenge-first serialized commit, so concurrent duplicates
produce one acceptance and stable `409 chat_report_already_exists`. Changed duplicate reasons
never overwrite prior intake. Another participant can independently report the same target,
including a suppressed tombstone. Missing or other-challenge targets share the safe 404.

Original message identity/body/sender/time remain in the existing retained immutable message
row; the report references that evidence without copying it into public responses. No player
edit/delete path exists. MSG-005 still must establish protected operator access, provenance/audit,
suppression/restriction procedures and approved erasure/retention handling. A migration downgrade
drops report intake and is destructive to reports; it is not a production retention mechanism.

## Validation and release boundaries

The canonical [OpenAPI document](../openapi/level5-v2.openapi.json) is generated by the existing
`Level5.Api.OpenApiExport` tool, including all five operations, public schemas, enum reasons,
nullable tombstones, integer bounds and ProblemDetails. No competing client contract is added.
List, read-position and mute actions explicitly declare their typed 200 responses alongside
the shared error metadata. The live Swagger regression checks every success response's JSON
schema reference and the page's message items, nullable older cursor and 64-bit positions.

Regressions cover all-operation 401/current-disabled 403/outage 503, canonical safe 404,
2–4 participant attribution/replays, rate headers, Unicode errors, signed/cross-challenge
cursor failures, initial/older/resume paging, gaps/tombstones, coherent snapshots without
writer blocking, private monotonic read/mute state, report concurrency/constraints and privacy.
Upgrade/failure/retry/down-up tests preserve existing chat, canonical challenge and financial
evidence. Older upgrade tests include the newly added table in their explicit inventories.

Final local validation on 2026-10-10:

| Suite/check | Result |
| --- | --- |
| Full Domain | 384 passed |
| Full Application | 236 passed |
| Full PostgreSQL Infrastructure | 451 passed |
| Full API integration | 272 passed |
| Architecture | 30 passed |
| Full V2 suite total | **1,373 passed, 0 failed, 0 skipped** |
| Focused chat Application | 4 passed |
| Focused chat PostgreSQL before final additional coverage | 82 passed |
| Focused HTTP/chat-error suite before final pagination coverage | 44 passed |
| Solution build | Passed, no errors; existing unrelated warnings remain |
| OpenAPI generation and fresh-export drift comparison | Passed |
| EF model/snapshot drift | No pending model changes |
| Whitespace | `git diff --check` passed |

The full suites include the final additional coverage. Evidence is in each test project's
`TestResults/msg003-final.trx`; Infrastructure's final result comes from the full-suite rerun
after table-inventory updates. Commands used `dotnet test v2/Level5BackendV2.sln --no-build -m:1`,
then `dotnet test v2/tests/Level5.Infrastructure.IntegrationTests -m:1`, with file-backed TRX
logging. Local logs are `v2/artifacts/msg003-final-tests.log` and
`v2/artifacts/msg003-infrastructure-final.log`. OpenAPI generation and comparison both drive
the existing `Level5.Api.OpenApiExport` tool; the EF drift command uses the existing
Infrastructure design-time startup project. These are local PostgreSQL/HTTP tests, not hosted
CI or production/operator evidence.

The subsequent OpenAPI review found missing implicit 200 response metadata for list/read/mute.
The strengthened regression failed before the annotation fix (`GET messages must document 200`)
and all 34 `BloodMoneyChatFlowTests` passed afterward. Evidence is in the API project's
`TestResults/msg003-success-contract-before.trx` and `msg003-success-contract-after.trx`.
The canonical document was regenerated and a second fresh export matched exactly. The full
suite above predates this metadata-only correction; it was not repeated for that correction.

Review pass 1 checked thin controllers, current eligibility before disclosure, authenticated
cursor construction/ordering, trusted actor attribution, durable rate composition, error statuses/
Retry-After and suppression privacy. Review pass 2 checked ownership and excluded MSG-004
notifications, MSG-005 operations, invented #83 terminal states, generic messaging, realtime,
Unity/Platform/Quick Chat work and financial/lifecycle mutations.

Current Domain has no legitimate post-Active terminal transition. Archive/closure behavior
remains generic around activation evidence, and actual closure-race certification stays with
#83/#84; no impossible archived rows or future enum values are fabricated for tests.

**Unrestricted production persistent free text remains release-blocked pending approved
retention/account-erasure policy and operational MSG-005 report handling.** Passing API/storage
tests does not approve production enablement. No Platform notification production is included.
