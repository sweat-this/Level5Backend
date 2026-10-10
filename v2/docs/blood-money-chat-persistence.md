# Blood Money challenge chat persistence (MSG-002 / #87)

Implementation baseline: fetched `origin/dev` at
`d25876a46e32d559ac321d060cec1a6d96c020a5` on 2026-10-10 (America/Chicago).
The live issue and all open PRs were re-read immediately before coding; no PRs were open.
The [MSG-001 contract](blood-money-chat-contract.md), [challenge contract](blood-money-challenge-contract.md)
and [module entry decision](blood-money-module-entry.md) remain authoritative.

## Storage and application boundary

The canonical `BloodMoneyChallengeId` is the conversation identity. Messages and private
participant state have restrictive composite foreign keys to the existing challenge/player
roster. Neither table is a source of membership. The shared `Level5V2DbContext` and migration
`20261010174730_AddBloodMoneyChat` add only `blood_money_chat_messages` and
`blood_money_chat_participant_state`; there is no conversation row or separate migration stream.

`SendBloodMoneyChallengeMessageUseCase` takes a trusted application `PlayerId`, client retry
UUID and raw text. It rejects empty retry keys and uses the Domain's strict UTF-16 validation,
newline normalization, NFC, pre-trim Cc rejection (except LF), Unicode whitespace trim,
500-scalar and 2,000-byte limits. Ordinal normalized-body comparison defines retry intent.
Malformed text never reaches persistence. The accepted Domain message exposes only getters.
All new messages have server UUIDs, server time and Visible visibility. Suppressed replay
projects a null body while retaining the protected original in storage.

`IBloodMoneyChatStore` owns one committed operation at a time. Its PostgreSQL implementation:

1. Starts a ReadCommitted transaction inside the configured EF execution strategy.
2. Acquires `FOR UPDATE` on the canonical challenge row.
3. Reloads challenge and roster without tracking, after acquiring the lock.
4. Requires canonical membership and activation evidence, otherwise safe `not_found`.
5. Resolves the sender-scoped retry key before writable-state or rate checks.
6. Allows a new send only in Active, then checks both persistent rolling windows.
7. Allocates committed `MAX(Sequence) + 1`, stages one message and saves it.
8. Commits while holding the challenge lock, then returns the result.

Exact replay returns the original safe projection with `Created = false`. Changed intent
raises `chat_message_conflict`. New sends return `Created = true`. Domain validation becomes
`invalid_chat_message`; rate rejection carries `chat_rate_limited` and an exact retry duration.
Future activated archives take the `chat_read_only` path for new keys. Final HTTP mapping,
current account/communication eligibility checks and headers belong to #88/#90.

The store rejects a dirty context or caller-owned transaction before starting. It never
flushes staged financial/lifecycle work. Reads are untracked, and each inserted candidate is
detached even on failure. A transient rollback retries the entire locked operation against
durable state. If the commit response is lost after PostgreSQL commits, the retry recovers
the durable idempotency key. No uncommitted candidate is acknowledged. Timestamps are UTC
at PostgreSQL microsecond precision so first response and durable replay agree exactly.
The caller's cancellation token also reaches the execution strategy, so cancellation
interrupts retry backoff as well as in-flight database work.

## Rate and private state

Opt-in composition follows the existing challenge convention: after
`AddLevel5Infrastructure`, call `AddBloodMoneyChat(hostRatePolicy)`. The host supplies
`BloodMoneyChatRatePolicy(5, TimeSpan.FromSeconds(10), 30, TimeSpan.FromMinutes(1))` for the
approved initial policy, or its versioned configuration. Domain embeds no rate constants.
`IClock` is resolved through the existing composition. No HTTP route enables this slice.

The message index `(ChallengeId, SenderPlayerId, CreatedAt, Sequence)` supports a bounded
timestamp query inside the locked transaction. Both windows count committed accepted messages
in `(T - window, T]`, including suppressed messages. The larger release delay across full
windows determines RetryAfter. Retries, rejections and rollbacks consume no extra budget;
new contexts/processes recover the same accounting. No body index or in-memory counter exists.
If the local server clock is behind a committed message timestamp, that message remains
in the rate budget conservatively, including its actual expiry in RetryAfter. Clock skew
or backward correction cannot hide accepted sends or reopen either budget. Exact retries
still recover the original message before any rate check.

State operations authorize the actor against the same locked challenge. Missing state reads
as zero/false without creating a row. Read advance validates `0 <= requested <= committed max`
and uses PostgreSQL `GREATEST(current, requested)` on conflict. Explicit mute upsert changes
only mute; read upsert changes only read position. Concurrent device updates preserve both
fields, and differing mute sets take the last committed value. Sending never marks history
read. Only the requested authorized actor's state is returned.

## Required #83/#84 handoff

Any future lifecycle transition that closes Active **must acquire the same canonical
challenge-row lock before reading state or staging dependent writes and hold it through
commit**. Lock order is `challenge row -> chat rows`; ordinary sends acquire no ledger or
reservation locks. Settlement writers must coordinate financial locks after canonical
challenge serialization to avoid inversion. A chat winner commits before closure; a closure
winner makes a subsequent new send read-only. Exact retries remain recoverable after closure.

Current canonical states have no post-Active terminal transition. #83 must preserve
`ActivatedAt` as activation evidence and update its Domain/schema lifecycle contract when
adding those transitions. This slice adds no enum/archive status or settlement behavior.
Actual send-versus-closure runtime certification remains a required #83/#84 integration
test when those writers exist; current evidence certifies both activation winners and
holding the send lock through commit. GameplayDeadlineAt alone never closes chat.

## Validation and review

Final local validation on 2026-10-10:

| Suite/check | Result |
| --- | --- |
| Domain | 384 passed |
| Application | 236 passed |
| PostgreSQL Infrastructure | 416 passed |
| API integration | 227 passed |
| Architecture | 30 passed |
| Full V2 total | **1,293 passed, 0 failed, 0 skipped** |
| Focused chat PostgreSQL suite | 53 passed |
| Solution build | Passed, 0 errors |
| EF model/snapshot drift | No pending model changes |
| Whitespace | `git diff --check` and new-file checks passed |

Commands: `dotnet test v2/Level5BackendV2.sln -m:1 --logger "trx;LogFileName=msg002-publish.trx"`,
`dotnet build v2/Level5BackendV2.sln --no-restore -m:1`, and
`dotnet ef migrations has-pending-model-changes --project v2/src/Level5.Infrastructure --startup-project v2/src/Level5.Infrastructure --no-build`
(the design-time factory requires `ConnectionStrings__DefaultConnection`; this check needs no live connection).
Local output is under `v2/artifacts/msg002-publish-tests.log`, `v2/artifacts/msg002-publish-build.log`
and each test project's `TestResults/msg002-publish.trx`. Hosted CI is separate evidence
reported by the PR checks.

Tests cover strict Unicode boundaries, 2–4 participants, sender attribution, server time,
durable reload, canonical authorization, all current never-active states, immutable retry
intent, concurrent duplicate/different writes, both persistent windows and exact boundaries,
financial/challenge/notification snapshot isolation, rollback before commit, transient retries
before/after a real commit, state defaults/monotonicity/field preservation, suppression-safe
replay, friendship removal, activation races and composition. PostgreSQL tests exercise PKs,
composite FKs, restrictive deletion, unique sequence/key, nonempty UUIDs, body byte bound,
visibility and nonnegative read positions. Upgrade, transactional DDL failure/retry and
down/up tests preserve existing challenge/financial/account evidence.

The 2,001-byte application boundary necessarily also exceeds 500 scalars: valid Unicode
uses at most four UTF-8 bytes per scalar. The database byte-bound test isolates that check.

Review pass 1 confirmed post-lock untracked lifecycle reads; authorization before retry
lookup; retry before new-send/rate checks; challenge lock held through commit; persistent
rate accounting; and no financial or challenge revision mutation. Review pass 2 confirmed
no HTTP/OpenAPI, pagination API, notification production, report/operator implementation,
suppression action, generic messaging framework, extra context/process, broker, realtime,
Unity or Platform change. Retention/erasure and operational release gates remain in MSG-001.

Review regressions cover both rate windows with fresh concurrent writer contexts whose
clocks lag by 10 ms or move backward by 2 seconds, including replay, exact RetryAfter,
durable row count and expiry. A deterministic transient-failure test selects a ten-second
retry delay and verifies cancellation interrupts it promptly without another attempt or
partial message/read/financial state.
