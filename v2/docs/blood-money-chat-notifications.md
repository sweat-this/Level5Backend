# MSG-004 / Backend #89: coalesced inbox notifications

Implemented against Backend `dev` `3bfedcbeb989dcadbf71183fe11196df7867c25e`.
Before coding, `origin/dev` was fetched and the open PR list was empty.
Platform `dev` `9f2ba2c8836fb9ca9534a921755586bf0b0a239b` contains the authorized
`/account/games/blood-money` hub. Challenge-detail navigation remains future Platform #54 work.

## Policy and copy

The API registers `BloodMoneyChatNotificationPolicy("v1", TimeSpan.FromMinutes(5))`
next to the chat rate policy. Versions contain 1–32 ASCII letters, digits or hyphens;
windows must be positive whole seconds. Policy v1 uses server-owned message `CreatedAt`, UTC:

```text
bucketStartUnixSeconds = floor(message.CreatedAt.UnixSeconds / 300) * 300
SourceEventKey = chat-v1:{challengeId:N}:{bucketStartUnixSeconds}
```

The key is deterministic across hosts, devices, retries and restarts, and fits the existing
128-character bound. Platform's unchanged unique tuple `(RecipientPlayerId, Source, SourceEventKey)`
limits a recipient to one notification for each challenge/bucket. The trusted envelope is exactly:

```text
Source: blood-money
Kind: challenge-chat
Title: New challenge chat activity
Body: null
ActionPath: /account/games/blood-money
```

No message text, sender name/ID, account identity, financial values or gameplay secrets enter
the copy or key. The existing public inbox DTO continues to omit the private dedupe key and recipient ID.

## Eligibility and state

Blood Money selects canonical challenge participants excluding the sender. Shared
`IPlayerProfileStore` and `IAccountStore` resolve current `AccountStatus.Active` eligibility;
their ID lookups bypass tracked snapshots. The participant's existing `NotificationsMuted`
state is checked under the challenge lock. These checks affect only notification production.
No recipient/account locks, additional statuses, moderation, restrictions or financial mutations are introduced.

Muting leaves existing inbox items intact. Unmuting produces no historical replay. The next
new eligible message fills an absent current bucket; an existing bucket remains coalesced.
Chat `LastReadSequence` and inbox `ReadAt` are independent. Listing/reading chat never marks
inbox items read; reading/unreading inbox items never advances chat state or resets bucket
identity. The next exact five-minute boundary permits a new notification.

## Atomicity and retries

`SendAsync` retains its challenge-first `FOR UPDATE` transaction. Exact normalized
`ClientMessageId` recovery runs before active-state/rate checks and before any notification
work. For a new message, all eligible drafts are staged through the scoped shared
`INotificationWriter`; one `SaveChanges` and transaction commit persist the message and inbox
rows together. The send is acknowledged only after commit. No table, migration, outbox,
broker, worker, delivery service or API is added.

The writer's narrow `ExistsAsync(recipient, source, key)` delegates to the shared notification
store's `AnyAsync`. Every send on this challenge holds the same lock through commit, making
existence-check-then-stage safe for this producer. Unique violations are not normal coalescing
control flow; the existing constraint remains defense in depth. Platform has no Blood Money dependency.

API composition resolves the writer, notification/profile/account stores and chat store in
the same service scope with the same `Level5V2DbContext`. The composition test uses actual
`AddLevel5Infrastructure` registrations and uncommitted visibility to verify this. Attempt
cleanup detaches newly tracked message/notification candidates even if staging, saving or
commit fails, preserving previously tracked caller entities. A transient rollback retries
from durable state; a lost commit response recovers the original message with `Created=false`
and performs no repeated notification production.

## Validation scope

PostgreSQL regressions cover 2–4 participants, concurrent sends and device retries, exact bucket
boundaries, recipient isolation, safe persisted/public envelopes, shared writer existence,
mute winners in both lock orders, unmute, read independence, current inactive accounts,
partial staging failure, rollback before commit, ambiguous successful commit and scoped composition.
Existing notification constraints/paging/read/API/friend-request atomicity remain covered.

Current canonical states have no terminal transition from Active. As documented in
[chat persistence](blood-money-chat-persistence.md), actual send-versus-closure and replay
after active closure runtime certification remain #83/#84 integration requirements. The
challenge-first lock, canonical reload, active guard, revision isolation and early replay path
are preserved; this slice does not invent terminal states or settlement behavior.

Review pass 1 checked transaction placement, sender exclusion, privacy, exact-replay bypass,
mute eligibility and shared persistence boundaries. Retry cleanup covers partially staged
notifications; current account reads bypass stale tracked snapshots. Review pass 2 checked
serialized existence/staging without unique-exception control flow, fixed bucket identity
after inbox reads, no unmute replay, the existing action route, and absence of lifecycle,
financial, moderation, realtime, Unity or Platform UI changes. No unresolved implementation findings remained.

Local validation on 2026-10-10 used real ephemeral `postgres:18-alpine` containers with the
production retrying PostgreSQL configuration (Docker Engine 28.3.2):

| Check | Result |
| --- | --- |
| Focused Domain / Application / PostgreSQL / API regressions | 94 / 59 / 107 / 44 passed |
| Full Backend V2 suite after review repair | 1,406 passed: Domain 384, Application 247, Infrastructure 469, API 276, Architecture 30; no failures or skips |
| Targeted account, email, password, session, chat and inbox regressions after repair | 151 passed; no failures or skips |
| Legacy repository suite | 32 passed |
| V2 and legacy builds | Passed |
| Fresh OpenAPI export vs committed contract | Equal; no API changes |
| EF pending-model-changes | None; no migration |
| Repository whitespace / tracked build artifacts | Clean / none |

Reproduce with `dotnet test v2/Level5BackendV2.sln --no-restore -m:1
--logger "trx;LogFileName=msg004-publish.trx"`. Per-project evidence is in
`TestResults/msg004-publish.trx`; focused runs use `msg004-domain.trx`, `msg004-application.trx`,
`msg004-infrastructure.trx` and `msg004-api.trx`. The post-review targeted run uses
`msg004-review-fixed.trx`; legacy evidence uses `msg004-publish-legacy.trx`.
These local files are ignored by Git.
Hosted CI and actual post-Active closure races are separate evidence, not claimed here.

## Shared account concurrency regression

Fresh account eligibility reads do not track a mutation snapshot. `StageEmailUpdateAsync`
therefore pins email-only writes to the validated account's `SessionGeneration` explicitly,
without writing that generation. A password rotation before staging or before commit rejects
the stale email write and rolls back its verification challenge. Fresh snapshots also work
when an older unchanged account row is already tracked, without reverting credentials.
Credential changes already staged in the same unit of work retain their original guard.

Four PostgreSQL regressions cover these cases. Before the repair, two failed: a stale email
write committed after password rotation, and a fresh snapshot with an older tracked row was
rejected. Evidence is in `TestResults/msg004-email-before-fix.trx` and the repaired targeted run
in `TestResults/msg004-review-fixed.trx`.
