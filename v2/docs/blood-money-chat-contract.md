# Blood Money challenge chat contract v1 (MSG-001 / #86)

This is the normative contract for durable, participant-only Blood Money challenge chat.
The policies below are selected by the MSG-001 request. This document specifies future
behavior; it does not claim that chat storage, API, notification production or moderation
is implemented. **Unrestricted persistent free text remains release-blocked on approved
normal-message retention/account-erasure policy and operational report handling.**

## Audited authority and scope

All three `origin/dev` refs were re-fetched on 2026-10-10 (America/Chicago). Each repository's
open-PR list was empty. Live Backend #86–#92, #83/#84 and Blood Money #109 were read.

| Repository | Exact audited dev SHA |
| --- | --- |
| `sweat-this/Level5Backend` | `6403d4063c490356e36e8b1efc17986d7c54fdf1` |
| `patrickrcharles/bloodmoney` | `95c92cc3c0c3c188fe7f8cf6502042194a768d80` |
| `sweat-this/platform` | `9f2ba2c8836fb9ca9534a921755586bf0b0a239b` |

Repository authority takes precedence over open tracker labels. #79 module entry
(`66602a71ebe682b8c7e7a765b6bc71436c0cf26b`), #80 ledger
(`cdffb27fd08b7d1211789d5b2fab9033556302c0`), #81 reservations
(`c7dc6bd0e6ea34aed54ce13a8d9c22c72d5d06bf`) and #82 challenges (Backend baseline above)
are materially landed. #61/#62/#63 likewise have repository decisions/implementation/evidence;
MSG-001 does not recreate their capabilities.

| Existing owner | Contract consumed by chat |
| --- | --- |
| [Blood Money module boundaries](blood-money-module-entry.md), [shared kernel](platform-kernel-and-product-entitlements.md), [module isolation](modular-game-boundaries.md) | Blood Money owns message content/use cases; shared Platform owns identity, social relationships and notifications. No generic cross-game messaging service or Level5 competition dependency. |
| [Challenge contract](blood-money-challenge-contract.md), [domain](../src/Level5.Domain/BloodMoney/BloodMoneyChallenge.cs), [store](../src/Level5.Infrastructure/BloodMoney/BloodMoneyChallengeStore.cs) | `BloodMoneyChallengeId`, immutable ordered participant roster, creator, stake, ruleset ID/version, acceptance/gameplay deadlines and revision. Canonical participant projection is already private to participants. |
| [Ledger](blood-credit-ledger-contract.md), [reservations](blood-credit-reservations-contract.md) | Sole financial authority; chat never reserves, releases, transfers, adjudicates or settles. |
| [Identity/lifecycle](account-lifecycle-and-cross-game-identity.md), [current player provider](../src/Level5.Api/Security/CurrentPlayerProvider.cs) | Authenticated private Account resolves to shared public `PlayerId`; account status is currently `Active` or `Disabled`. |
| [Notification writer port](../src/Level5.Application/Abstractions/INotificationWriter.cs), [writer](../src/Level5.Application/Platform/NotificationWriter.cs), [PlayerNotification](../src/Level5.Domain/Platform/PlayerNotification.cs) | Trusted producer stages shared inbox entries in the caller's unit of work; unique key is `(RecipientPlayerId, Source, SourceEventKey)`. No independent writer commit. |
| [Backend #83](https://github.com/sweat-this/Level5Backend/issues/83), [#84](https://github.com/sweat-this/Level5Backend/issues/84) | Future active-game outcomes, terminal transitions, expiry processing and settlement. Chat consumes their canonical state. |
| [Unity Quick Chat source][quick-chat], [Quick Chat contract][quick-chat-doc], [Blood Money #109](https://github.com/patrickrcharles/bloodmoney/issues/109) | Transient enum-only listen-host communication; not durable account attribution. |
| [Platform friends surface][platform-friends] | Friendship removal exists; no shared account-level block model exists at this baseline. Removal is not block. |

## Conversation identity, lifecycle and access

One durable challenge chat is keyed **directly by `BloodMoneyChallengeId`**. There is no
separate Conversation, ChatRoom, thread roster or chat participant list. Message/read/mute
records may reference the challenge and player; their existence never grants membership.
Every operation consults the canonical immutable challenge roster and lifecycle.

The current `BloodMoneyChallengeStatus` is exactly the five named rows below. No
invitation-stage creator note is supported. Activation makes the chat resource available
even when its message list is empty; no separate thread creation is required.

| Canonical state/history | Eligible participant behavior |
| --- | --- |
| `PendingAcceptance` | No conversation; all chat operations return enumeration-safe 404. |
| `Declined` | No conversation; all chat operations return enumeration-safe 404. |
| `Cancelled` | No conversation; all chat operations return enumeration-safe 404. |
| `Expired` | No conversation; all chat operations return enumeration-safe 404. |
| `Active` | Read/page history, send, report retained messages, advance own read position and change own notification mute. |
| Any future canonical terminal state reached from an already-Active challenge | Existing conversation becomes participant-only read-only archive: read/page, report retained messages, advance own read position and change own notification mute remain permitted; new sends return 409. |

A challenge that never became Active has no chat archive. The archive row is a rule for
future canonical transitions, **not a new enum value**. #83 must preserve canonical evidence
of prior activation (currently `ActivatedAt`) when defining those transitions. Unknown or
otherwise unsupported future states must not be assumed writable; version/review the
mapping when #83 lands. Chat must not infer activation from funding or message presence.

`GameplayDeadlineAt` is not a chat cutoff. If the canonical state remains `Active`, chat
remains writable, including at and after that deadline. Only #83/#84 change challenge state.
Friendship is checked at challenge creation only. Removing friendship later leaves the
funded immutable roster and every chat permission intact. Social changes or communication
sanctions must not cancel/release/settle a challenge or strategically silence the other
participant. Outcome decisions continue through canonical adjudication, never chat.

### Caller and current communication eligibility

Every public operation derives identity through:

```text
authenticated Account -> ICurrentPlayerProvider -> PlayerId -> canonical challenge roster
```

`ICurrentPlayerProvider` is an API boundary; resolve the caller there and pass trusted
`PlayerId` into module use cases. Do not import API types into Application/Domain. A body,
query, cursor, display name, Unity local profile, UGS member ID or QuickChatSenderId cannot
provide sender/reader/reporter/preference authority. Public inputs have no actor field.

Missing/invalid authentication returns 401. For authenticated callers, revalidate current
account/communication eligibility on **every** operation. A `Disabled` account cannot list
chat, send, advance read state, report or mutate chat preferences (403). A current
communication restriction has the same effect, independent of challenge outcome. MSG-005
owns the restricted, audited operational enforcement path; this is no new global auth model.

Existing access JWTs may remain valid until expiry. The current player provider resolves
identity, not current account eligibility. MSG-003/005 must consult current Backend
eligibility rather than treating successful bearer validation as emergency-suspension
enforcement. This requirement does not redesign global JWT/session behavior. Fail closed
with 503 if eligibility cannot be determined because its service/persistence is unavailable.

For eligible callers, unknown challenge, nonparticipant and never-activated/no-chat resource
all return the same 404/code with no state or roster disclosure. Perform this resource
authorization before state-specific errors or target-message/cursor lookups. An account-wide
403 must apply consistently regardless of the requested challenge, so it reveals no existence.
Revalidate authorization on every page/retry; possession of a cursor or MessageId grants none.

**Account-level block: unsupported in chat contract v1.** No Blood Money block table or
social graph is added. A future shared Platform block policy requires an explicit contract
version/extension; neither friendship removal nor thread mute substitutes for it.

## Immutable messages, text and send retries

| Accepted-message field | Authority / semantics |
| --- | --- |
| `MessageId` | Server-generated opaque identifier; never supplied as a send authority. |
| `ChallengeId` | Canonical `BloodMoneyChallengeId` for this operation. |
| `Sequence` | Positive, server-committed, unique and strictly increasing per challenge in commit order. Gaps are permitted; clients never require contiguous numbers. |
| `SenderPlayerId` | Server-derived authenticated shared `PlayerId`. |
| `ClientMessageId` | Nonempty caller-generated GUID; sender-scoped retry key. |
| `Body` | Validated, normalized immutable plain text. |
| `CreatedAt` | Backend server clock (`IClock`), UTC timestamp; ordering uses Sequence rather than timestamps. |
| `Visibility` | `Visible` or `Suppressed`; only authorized moderation may transition visibility. |

Immutable accepted identity, sequence, sender, client key, body and time never change.
Players cannot edit, delete, rewrite or renumber messages. Visibility is the separate
moderation state, not permission to rewrite original accepted content.

Normalize deterministically before fingerprint comparison and both length checks:

1. Decode valid UTF-8/Unicode; reject malformed encoding or unpaired surrogates rather than
   silently replacing them.
2. Normalize CRLF and remaining CR to LF, then Unicode NFC.
3. Reject Unicode control characters (category Cc, including tabs/NUL/DEL/C1) except LF.
   Check before trimming so outer control characters cannot evade rejection.
4. Trim outer Unicode whitespace. The result must be nonempty, at most **500 Unicode scalar
   values** and at most **2,000 UTF-8 bytes**. Count scalars, not UTF-16 code units/graphemes.

Storage and both clients treat Body as plain text. No HTML, Markdown execution, rich-text
markup, clickable user links or attachments. Escape text in HTML contexts and disable Unity
rich-text parsing. URLs may remain inert text. Text resembling a system notice receives no
trusted badge or financial meaning.

The unique retry fingerprint is `(ChallengeId, SenderPlayerId, ClientMessageId)`:

| Retry intent | Outcome |
| --- | --- |
| Same key and same normalized body | 200 with the original committed message's current safe projection; no second insert, sequence allocation, accepted-rate charge or notification. |
| Same key and changed normalized body | 409 conflict; original message remains unchanged. |
| New key in writable Active challenge | 201 with committed message; never acknowledge uncommitted work. |
| New key in archive | 409 read-only; no insert. |

After current caller/resource authorization, resolve an existing key before the new-send
lifecycle/rate check. Thus an exact lost-response retry after closure can recover the
accepted message, without reopening the archive. A disabled/restricted caller still gets
403. A retry of a suppressed message returns its tombstone, never its original body.
Clients must keep the same key/body after timeout, lost response or ambiguous 503; changing
the key could create a second message. Concurrency losers recover the winner from a fresh
scope/transaction; same intent replays, changed intent conflicts. No uncommitted candidate
is returned as success. Unexpected faults retain the existing safe 500 convention; 503 is
reserved for transient service/persistence unavailability, not all errors.

### Rate policy

Versioned host configuration, not Domain constants, supplies initial defaults of **5 accepted
messages per 10 seconds** and **30 accepted messages per minute**, both per
`(PlayerId, ChallengeId)`. Apply both rolling windows using server time; acceptance at time
T counts committed sends in `(T - window, T]`. Concurrent requests cannot overshoot either
budget. Rejected/rolled-back sends and exact retries do not count. A rejected new send returns
429 with Retry-After; it changes no message, financial or challenge state. MSG-002/003/005
must implement consistent enforcement across concurrent requests/restarts in the supported
deployment rather than presenting a process-local bypass as this guarantee.

## Send versus closure: required serialization

MSG-002 must serialize new sends against the **existing PostgreSQL canonical challenge row**,
using a row lock or an equivalent transactionally correct mechanism. An earlier ordinary
read of Active followed by an independent message insert is insufficient. Current #82's
optimistic lifecycle revision checks do not, by themselves, serialize a chat-only insert.

Canonical write order is:

```text
begin transaction
canonical challenge authorization/serialization (lock authoritative row, reload state/roster)
    -> chat sequence/message state (retry, lifecycle, rate checks; allocate/order new message)
    -> final SaveChanges / transaction commit
```

Hold serialization through commit. #83/#84 closure writers must participate at the same
challenge row before committing a lifecycle transition, and coordinate their lock order with
MSG-002. Canonical state must be read after serialization is acquired, not from stale tracked
state or an earlier snapshot. A transaction/isolation strategy unable to see the winner must
abort and retry in a fresh scope. Ordinary sends acquire **no financial/reservation locks**.
Dependent chat writes and any in-transaction notification staging precede final commit;
there is no message acknowledgment or external notification before that commit.

| Serialization winner | Required outcome |
| --- | --- |
| New message wins | It verifies Active and commits before closure can commit. |
| Closure wins | Sender observes canonical terminal state and rejects the new send with 409. |
| Send races activation | Sender sees either no chat (404 before activation wins) or Active after activation commits; never a pre-activation insert. |
| Concurrent new sends/retries | Unique sequence and retry constraints plus serialized allocation produce ordered committed messages; identical keys produce one accepted message. |

The unsafe sequence “read Active; closure commits; insert/commit message after closure”
must be impossible. Row locking must cover the original challenge read through the final
transaction, including any EF implicit/explicit transaction boundary. Rollback leaves no
partial message/read/notification changes; consumed sequence gaps are harmless. Chat sends
**do not increment `BloodMoneyChallenge.Revision`**: they are not challenge-domain transitions.
Do not change creator, roster, stake, rules, deadlines, status, reservations, ledger or result.

## Private read state and thread mute

One server-owned participant state is keyed by `(ChallengeId, PlayerId)` and contains
`LastReadSequence` and `NotificationsMuted`. Defaults are zero and false. This is a preference/
read projection, never another roster. Every access still checks the canonical challenge.

`LastReadSequence` accepts a nonnegative integer up to the latest committed challenge
sequence observed during validation. A value beyond that bound returns 400. Gaps are valid
positions. Persist `max(current, requested)` atomically: stale smaller/equal updates are
successful no-ops, and concurrent devices never regress durable state. Responses expose
only the caller's read pointer. Listing/sending does not implicitly mark history as read.
Clients advance after actually presenting the relevant messages and merge delayed responses
monotonically. No other participant's pointer, read timestamp or social read receipt is exposed.
Sequence gaps and own messages mean `latestSequence - lastReadSequence` is not an unread count;
an unread badge must count retained Visible messages from other senders beyond the pointer.

`NotificationsMuted` suppresses **chat notifications only**, scoped to this challenge and
caller. It changes no read/send/report permission, history, challenge lifecycle or financial
state. No server-side per-sender hiding exists in v1. Set the explicit boolean (not toggle);
an unchanged value is a no-op. Concurrent differing sets serialize, with the last committed
set winning. Read-position and mute updates must preserve each other's fields. Muting affects
future notification eligibility; it does not erase existing inbox entries or chat history.

## Safe projections, pagination and polling

A message projection contains `messageId`, `challengeId`, `sequence`, `senderPlayerId`,
`clientMessageId`, `body`, `createdAt`, `visibility`. For Suppressed, `body` is null and the
message remains a chronological tombstone at its original sequence. Never return original
content via list, send replay, report response, error or notification. The accepted body is
preserved as protected evidence for authorized moderation/report handling. Participants may
report retained suppressed messages; they need not regain access to the hidden body.

List accepts optional `limit` and at most one `cursor`. Default limit **50**, maximum **100**;
values outside 1–100, malformed limits or cursors longer than **1,024 ASCII characters** return
400. A single opaque, integrity-protected, versioned cursor format binds challenge ID, traversal
direction and sequence boundary (and the history snapshot upper bound when paging older).
Clients never parse or construct cursors. Wrong version, tampering, malformed encoding and
cross-challenge use all produce stable `invalid_chat_cursor` after resource authorization.
Cursors contain no message body, account identity, private read pointer or financial state.

Every successful list response provides:

```text
items                 chronological ascending Sequence, at most limit
olderCursor           nullable; next older page, exclusive lower boundary
resumeCursor          only newer committed sequences; no offset pagination
latestSequence        current committed high-water sequence (0 when empty)
lastReadSequence      caller only
isReadOnly            derived from canonical lifecycle
notificationsMuted    caller only
```

- Initial list captures committed high-water H and returns the newest bounded messages at
  or below H, **presented ascending**. `olderCursor` anchors below the first returned sequence
  and fixes upper bound H; `resumeCursor` starts strictly after H. Empty chat resumes after 0.
- Older pages return the next newest bounded slice below their exclusive boundary and at
  or below their original H, again ascending. New inserts cannot shift these pages. Older
  traversal preserves its initial H in the returned resume cursor; it must not move a client's
  existing live resume position forward. Null older cursor means that history is exhausted.
- Resume requests return the **earliest** bounded committed messages strictly after their
  cursor boundary, ascending. Advance the returned resume boundary only to the last returned
  sequence; an empty page retains its boundary. Never jump it to `latestSequence` when the
  response is truncated, or intermediate messages would be skipped. A client may immediately
  request the next bounded batch when its returned messages lag the high-water mark.
- The same sequence is stable across retries/pages; clients deduplicate by MessageId/Sequence
  and tolerate gaps. Include suppressed tombstones in bounds/order so suppression neither
  renumbers history nor prevents cursor progress. `latestSequence` includes those commits.
- List authorization, lifecycle metadata, high-water and private state must reflect a
  consistent committed read; responses can become stale immediately afterward. Sending always
  rechecks canonical state. Read responses may arrive out of order; the durable pointer still
  advances only. Do not expose protected bodies through caches or shared responses.

Resume means newly committed messages, not a moderation-change feed. It cannot by itself
invalidate bodies already cached by a client. Unity/Platform must refresh currently displayed
history through authorized list reads during bounded foreground polling/reconnect, replace
cached bodies with tombstones on suppression, and clear inaccessible views on 403/404. No
continuous background loop or realtime provider is selected here. Poll interval/backoff is a
client policy; honor Retry-After and pause/back off on failures. Server suppression guarantees
apply to current projections and cannot retract text a participant previously saw.

## Immutable reports and operator handling

Public report input is exactly `MessageId` and `Reason`, with Reason one of
`Harassment`, `Hate`, `Threat`, `SexualContent`, `Spam`, `Other`. Unknown/undefined enum values
are 400; Other adds no free-text explanation field. Backend derives reporter PlayerId and
server report time. Resolve the message **inside the authorized challenge**, including an
archive; an absent or other-challenge MessageId returns the same safe 404. Only an eligible
canonical participant able to access its retained message/tombstone may report it.

Accept one immutable report per `(ChallengeId, MessageId, ReporterPlayerId)`; duplicates
return 409 `chat_report_already_exists`, regardless of reason, and create no second evidence
record. Response confirms acceptance without echoing original content or revealing another
player's reports. Preserve immutable message identity, original sender/body/time, reporter,
reason and report time with adequate provenance, even if suppression precedes/follows reporting.
Players cannot edit/delete reports or evidence. Suppression changes participant visibility,
not evidence. No automated purge/retention duration is selected here.

MSG-003 owns authenticated report intake; MSG-005 owns exact protected storage and a usable
least-privilege operator path/runbook on the smallest existing ops surface or an audited manual
procedure. Before production, it must demonstrate restricted report review, evidence access,
visibility changes, emergency communication restriction, and audit of access/actions. No
speculative generic moderation console is required. Operator actions cannot settle or release
money. Ordinary participant APIs never expose moderation evidence or review internals.

## Shared notification contract

Reuse `INotificationWriter` and `PlayerNotification` only, with `Source = "blood-money"` and
`Kind = "challenge-chat"`. No Blood Money notification store, queue or broker. Notifications
use fixed safe text (for example, “New challenge chat activity”), no message body, hidden
gameplay state, financial values or sender-private identity. Body can be null. An action path,
when present, must satisfy existing safe authenticated `/account` path validation and link to
the corresponding Platform challenge surface when that surface exists; do not invent a live UI.

Recipients are other eligible canonical participants, never the sender or disabled/restricted
accounts. Respect each recipient's `NotificationsMuted` at the serialized notification
eligibility decision. Listing chat does not mark shared inbox items read, and reading a shared
notification does not advance chat read state. No per-device notification identity exists.

MSG-004 must implement **deterministic bounded coalescing**, not one entry per message.
A conforming time-bucket policy uses a versioned, configured positive window, the server-owned
CreatedAt of the committed message and a SourceEventKey binding challenge ID, policy version
and UTC bucket. The
existing `(RecipientPlayerId, Source, SourceEventKey)` uniqueness limits each recipient/challenge
to at most one inbox entry per bucket across devices, concurrent sends, retries and restarts.
The key must fit the existing 128-character limit. MSG-004 must publish its exact window before
enablement. Read/unmute does not reset bucket identity or produce historical replay storms;
only new unread eligible activity may generate a later entry. Equivalent deterministic bounded
unread-batch policies are permitted if they document the same guarantees. A first send after
unmute can notify in the current bucket only if that recipient's bucket has no prior entry.

Stage notifications atomically with the message in the same existing unit of work when using
the shared writer; uniqueness/coalescing must not cause message retries to double-notify or a
duplicate inbox key to discard an otherwise valid new message. If MSG-004 justifies post-commit
production, it must specify durable retry/revalidation guarantees using existing mechanisms.
Never notify for rolled-back messages, and never claim delivery before commit. Mute/restriction
changes concurrent with notification production serialize at its eligibility decision; a
previously committed inbox entry is not a new send and is not retrospectively deleted by mute.

## MSG-003 operation and error semantics

Exact route spelling follows existing V2 conventions under the Blood Money challenge
namespace; these are contracts, not implemented controllers or OpenAPI additions. JSON names
use existing camelCase conventions, identifiers are opaque GUIDs, timestamps UTC ISO 8601.
Sequences/read positions use nonnegative signed-64-bit integers (message sequences positive);
clients must preserve integer precision instead of rounding through JavaScript Number.

| Operation | Public input (challenge ID in resource path) | Success |
| --- | --- | --- |
| List messages | Optional cursor/limit only | 200 with the page contract above. |
| Send | `clientMessageId`, `body` only | 201 new message; 200 exact replay, both current safe message projections. |
| Advance own read position | `lastReadSequence` only | 200 with effective caller `lastReadSequence`; stale values succeed unchanged. |
| Report message | `messageId`, bounded `reason` only | 201 with an opaque server report ID/acceptance confirmation, no evidence body. |
| Set own chat notification mute | `notificationsMuted` only | 200 with effective caller `notificationsMuted`. |

No request can create a “system message.” Missing required fields/invalid GUIDs/types,
malformed Unicode, length violations, bad cursors/limits, out-of-range read positions and
invalid report reasons return 400. Any targeted preference/read/report mutation is scoped
to the trusted caller, not a supplied PlayerId. Every operation leaves financial state intact.

Use existing `application/problem+json` ProblemDetails conventions from
[ApiExceptionHandler](../src/Level5.Api/ErrorHandling/ApiExceptionHandler.cs): status, safe
title, type `https://level5.game/errors/{code}`, extensions `code` and `traceId`. The new chat
codes below are specified for MSG-003, **not currently implemented**. Framework authentication/
model-validation responses retain established V2 behavior; use stable chat codes for semantic
validation after authorization. Never echo input body, private IDs, DB errors or evidence.

| Status | Stable outcome / code |
| --- | --- |
| 401 | Missing/invalid authentication; established bearer challenge behavior. |
| 404 | Unknown challenge, nonparticipant, no activated chat or inaccessible report target: `not_found`, same safe response. |
| 400 | `invalid_chat_message`, `invalid_chat_cursor`, `invalid_chat_limit`, `invalid_chat_read_position`, `invalid_chat_report` as appropriate. |
| 403 | Current disabled account/communication restriction: `chat_communication_restricted`; never a financial transition. |
| 409 | New send to archive: `chat_read_only`; changed retry intent: `chat_message_conflict`; duplicate report: `chat_report_already_exists`. |
| 429 | New-send rate exceeded: `chat_rate_limited`, Retry-After. |
| 503 | Transient persistence/service outage: existing `service_unavailable`; no acknowledgment of uncommitted work. |

After authentication/current eligibility and basic request-shape validation, resource
authorization precedes semantic cursor/message/state lookups. Authorization failures reveal
no target details. Validation/conflict/rate failures cannot partially persist a mutation.
Transport failure or 503 can leave the caller uncertain whether commit happened: recover
with the original send fingerprint, monotonic read update or explicit mute set, never by
assuming failure implies no commit. A duplicate report conflict confirms no new report;
no change of reason can overwrite the original.

## Financial notices, privacy and retention release gates

Player-authored text is never authoritative for stake held, challenge accepted, winner,
refund, payout, settlement or expiration. Unity/Platform render those only from canonical
challenge/ledger/result projections. No chat text, visibility change, report, mute, account
restriction or social change can mutate reservations, ledger, settlement or challenge status.

Routine application telemetry must contain no message body, auth token, private AccountId,
wallet/escrow details or hidden gameplay state. Use low-cardinality operation/result metrics,
not identifiers/content as labels. Disable/redact chat request/response body logging and keep
validation/error/exception paths free of content; no body-derived exception text. Reported
content is protected moderation evidence, not ordinary logs. Operator evidence access and
actions require restricted audit records; raw content must not be copied into telemetry.

The following remain explicit:

- No player message edit/delete in v1.
- Report evidence survives ordinary suppression; no player may delete it.
- Account deletion/anonymization is not implemented in current Backend authority.
- **Exact normal-message retention and account-erasure behavior require product/privacy
  approval before production enablement.** That decision must cover shared history, evidence
  holds/access and erasure/redaction interactions; engineering selects no legal duration.
- No automated purge policy is added by MSG-001. “Durable” means committed storage/recovery,
  not a promise of indefinite retention. This contract neither approves indefinite retention
  nor assumes account disable is erasure.
- Unresolved retention/erasure policy and unproven MSG-005 operator handling block unrestricted
  persistent free text. Downstream work may proceed in controlled validation without claiming
  production approval. Do not silently mark these gates passed when persistence/API lands.

## Quick Chat and trusted live multiplayer remain separate

Blood Money #109 Quick Chat is transient, enum-only, listen-host relayed and non-durable.
Its bounded surviving-session buffer is not Backend history or trusted moderation evidence.
Do not persist it in Backend or map QuickChatSenderId to durable PlayerId. A player-controlled
host cannot attest durable sender identity, game results or financial notices.

[MSG-009 / Backend #91](https://github.com/sweat-this/Level5Backend/issues/91) must independently
prove `Backend PlayerId <-> trusted attestation <-> UGS multiplayer session member`, including
membership/reconnect/restriction revalidation. Arbitrary multiplayer free text remains
**prohibited until that gate passes** and approved follow-up implementation/operational gates
are met. Challenge membership is not multiplayer-session membership. #92 cannot infer trust
from host assertions or this challenge contract. No WebSocket/realtime provider is selected.

## Downstream ownership and required test matrix

| Owner | Required handoff |
| --- | --- |
| [MSG-002 / #87](https://github.com/sweat-this/Level5Backend/issues/87) | Module persistence/use cases in existing PostgreSQL/DbContext/migration stream; retry/sequence uniqueness, canonical serialization, private read/mute state, rollback and restart durability. Coordinate closure locking with #83/#84. |
| [MSG-003 / #88](https://github.com/sweat-this/Level5Backend/issues/88) | Authenticated operations, current eligibility, safe projections, cursor/errors/rate policy, report intake and later OpenAPI/contract publication. |
| [MSG-004 / #89](https://github.com/sweat-this/Level5Backend/issues/89) | Shared writer production, concrete deterministic coalescing window/bounds, recipient/mute/read semantics and atomic/retry guarantees. |
| [MSG-005 / #90](https://github.com/sweat-this/Level5Backend/issues/90) | Abuse enforcement, current communication restriction, protected immutable report evidence, suppression, usable audited operator procedure and approved retention/erasure implementation. |
| #83/#84 | Canonical active-game state/terminal transitions and financial authority; same-row serialization with sends, no chat-owned deadline transition. |
| MSG-009/#91 and MSG-010/#92 | Independent trusted live-session identity/delivery gate, then only approved implementation. No automatic extension of challenge chat into session chat. |
| Unity/Platform client owners | Consume published MSG-003 wire contract, inert text, integer-safe cursors/sequences, private state, bounded polling, retry recovery and suppression refresh; implement no Backend authority. |
| Product/privacy and operations | Approve unresolved retention/account-erasure policy and prove report processing before production enablement. |

These are **downstream required tests**, not fake implementation tests added by MSG-001.
Persistence/concurrency claims require real PostgreSQL integration evidence, not only mocks.

| Case | Required assertion | Primary owner |
| --- | --- | --- |
| PendingAcceptance | Creator/accepted creator/invitees all get no chat/404; no note or persisted message. | MSG-002/003 |
| Active | All 2–4 canonical participants can read/write/report; empty Active chat lists successfully. | MSG-002/003 |
| Declined | No chat resource or archive, including for creator/decliner. | MSG-002/003 |
| Cancelled | No chat resource or archive, including for creator. | MSG-002/003 |
| Expired | No chat resource/archive; no inferred active timeout transition. | MSG-002/003 |
| Future post-Active terminal | Prior participants read/page/report and update private state; new sends 409. Exact prior retry recovers; no invented state. | MSG-002/003, #83/#84 |
| Deadline | At/after GameplayDeadlineAt, canonical Active remains writable; no financial/lifecycle effects. | MSG-002/003 |
| Unknown/nonparticipant/spoofed actor | Same safe 404; posted actor/session/host claims cannot grant access; auth failures 401. | MSG-003 |
| Friendship removed after activation | Both participants retain access; roster, reserves and ledger unchanged. | MSG-002/003 |
| Disabled/restricted account with valid JWT | All five operations 403 based on current eligibility; no money/state changes; outage fails closed. | MSG-003/005 |
| Unsupported account block | No module block graph/table or interpretation of friend removal as block. | MSG-003/005 |
| Thread muted | Send/read/report/history unchanged; no new eligible notification, no per-sender hiding; archive prefs permitted. | MSG-003/004 |
| Normalization/limits | NFC and newline-equivalent retries match; empty/control/malformed input rejected; exact 500-scalar/2,000-byte bounds and supplementary Unicode tested; markup/URLs inert in both clients. | MSG-002/003, clients |
| Duplicate/lost-response retry | Same normalized intent returns original ID/sequence; one message and no repeat notification/rate charge, including after closure/restart. | MSG-002/003/004 |
| Changed retry fingerprint | Same sender/client key with changed normalized body 409; sender/challenge keys remain isolated. | MSG-002/003 |
| Concurrent sends | 2–4 senders, same/different keys: unique monotonic committed sequences; gaps tolerated; concurrent budgets not exceeded. | MSG-002/003 |
| Send versus challenge closure | Force each same-row serialization winner and stale-read interleaving; commit before closure or reject afterward; unchanged challenge Revision on send. | MSG-002, #83/#84 |
| Activation versus send | No pre-activation insert; observe absent chat or committed Active state. | MSG-002 |
| Rollback/outage/restart | No partial message/notification/read write; retry recovers ambiguous commit; timestamps server-owned, no wallet/reservation lock or mutation. | MSG-002/003/004 |
| Read-pointer regression/multi-device | Smaller/equal updates no-op; concurrent max wins; beyond-high-water 400; gaps valid; pointers private; mute/read writes preserve each other. | MSG-002/003 |
| Pagination | Newest initial page ascending; stable older pages under inserts; bounded earliest-newer catchup never skips when backlog exceeds limit; empty/tombstone-only pages and sequence gaps progress. | MSG-002/003 |
| Cursor tamper/version/cross-challenge | Stable 400 for authorized resource; no unauthorized challenge access or details; oversized limits/tokens bounded. | MSG-003 |
| Rate limiting | Both windows, exact boundaries, per-player/challenge isolation, retries excluded, concurrent/restart enforcement, 429/Retry-After without side effects. | MSG-002/003/005 |
| Report authorization/duplicates | Participant retained target only, including archive/suppressed; other-thread/unknown target safe 404; bounded enum; duplicate 409; immutable evidence. | MSG-003/005 |
| Suppression | Body absent from list/replay/report/errors; sequence/tombstone retained; evidence survives; client refresh replaces cached body. | MSG-003/005, clients |
| Operator access | Unauthorized ops cannot read evidence/change visibility; authorized review, restriction and audited handling work without financial mutation. | MSG-005 |
| Notification privacy/coalescing | No body/secrets/financial values/private sender identity; sender/muted/restricted excluded; safe path; bounded deterministic keys under concurrency/restart, read/unmute cannot reset bucket; rollback emits none. | MSG-004 |
| Privacy/release gates | Logs/errors omit forbidden data; retention/erasure approval and usable operator path remain required, never inferred from passing implementation tests. | MSG-003/005, product/ops |
| Quick Chat/live free-text gate | No Backend Quick Chat persistence/PlayerId mapping; forged host/session claims grant no durable authority; #91 gate required. | MSG-009/010, clients |

## Contract review record and explicit non-goals

Review pass 1 checked duplicate thread/roster authority, friendship substitution, financial
effects, invitation-stage complexity, client sender spoofing and check-then-insert races.
The contract fixes these at canonical challenge authorization and same-row transaction
serialization. Exact retry-after-closure recovers accepted work without accepting a new send.

Review pass 2 checked invented #83 enum names, game-specific social blocking, invented
account-deletion policy, generic messaging/realtime infrastructure, duplicate notification
storage, Quick Chat persistence and Unity/Platform scope creep. None is authorized. Retention
and operational gates remain explicit. Pagination review additionally requires resume cursors
to advance through delivered messages only; suppression refresh is distinct from new-message
polling. These are document review results, not runtime certification.

MSG-001 adds no chat persistence, EF migration, DbContext change, HTTP endpoint, OpenAPI change,
notification producer, moderation runtime, operator UI, Unity/Platform UI or runtime, package,
WebSocket, broker, attachment, voice, direct message, wallet change, settlement or social/block
system. Downstream implementation must re-audit latest dev/PRs and version this contract for
material incompatible policy/wire changes; this snapshot does not waive future review.

[quick-chat]: https://github.com/patrickrcharles/bloodmoney/blob/95c92cc3c0c3c188fe7f8cf6502042194a768d80/Assets/Game/Runtime/Multiplayer/MultiplayerQuickChatAuthority.cs
[quick-chat-doc]: https://github.com/patrickrcharles/bloodmoney/blob/95c92cc3c0c3c188fe7f8cf6502042194a768d80/docs/multiplayer-quick-chat.md
[platform-friends]: https://github.com/sweat-this/platform/tree/9f2ba2c8836fb9ca9534a921755586bf0b0a239b/src/app/%28account%29/account/friends
