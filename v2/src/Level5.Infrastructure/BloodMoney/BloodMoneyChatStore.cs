using System.Data;
using Level5.Application.Abstractions;
using Level5.Application.BloodMoney;
using Level5.Application.Common;
using Level5.Domain.BloodMoney;
using Level5.Domain.Ids;
using Level5.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Level5.Infrastructure.BloodMoney;

/// <summary>All chat operations lock the canonical challenge before inspecting lifecycle or chat rows.
/// Closure writers must use this same challenge-first order. Sends never lock financial rows.</summary>
public sealed class BloodMoneyChatStore(Level5V2DbContext db, IClock clock) : IBloodMoneyChatStore
{
    public Task<SendBloodMoneyChallengeMessageResult> SendAsync(SendBloodMoneyChallengeMessageRequest request,
        BloodMoneyChatRatePolicy ratePolicy, CancellationToken cancellationToken)
        => Serialized<SendBloodMoneyChallengeMessageResult>(request.ChallengeId, request.SenderPlayerId, async challenge =>
        {
            var existing = await Messages(request.ChallengeId).SingleOrDefaultAsync(row =>
                row.SenderPlayerId == request.SenderPlayerId.Value && row.ClientMessageId == request.ClientMessageId, cancellationToken);
            // Authorization has already run. Recovery precedes both writable-state and rate checks.
            if (existing is not null)
            {
                if (!string.Equals(existing.Body, request.Body, StringComparison.Ordinal))
                    throw new BloodMoneyChatException("chat_message_conflict", "The client message ID was used for different text.");
                return new(View(existing), false);
            }
            if (challenge.Status != BloodMoneyChallengeStatus.Active)
                throw new BloodMoneyChatException("chat_read_only", "Challenge chat is read-only.");

            // PostgreSQL timestamps have microsecond precision. Use that same instant for decisions and storage.
            var now = clock.UtcNow.ToUniversalTime();
            now = now.AddTicks(-(now.Ticks % 10));
            var oldest = now - ratePolicy.LongWindow;
            // A slower host or a backward clock correction must not hide already accepted sends.
            // Count future-dated commits conservatively; their expiry also determines RetryAfter.
            var times = await Messages(request.ChallengeId)
                .Where(row => row.SenderPlayerId == request.SenderPlayerId.Value && row.CreatedAt > oldest)
                .OrderByDescending(row => row.CreatedAt).ThenByDescending(row => row.Sequence)
                .Take(Math.Max(ratePolicy.ShortLimit, ratePolicy.LongLimit))
                .Select(row => row.CreatedAt).ToListAsync(cancellationToken);
            var retryAfter = Max(RetryAfter(times, now, ratePolicy.ShortLimit, ratePolicy.ShortWindow),
                RetryAfter(times, now, ratePolicy.LongLimit, ratePolicy.LongWindow));
            if (retryAfter > TimeSpan.Zero)
                throw new BloodMoneyChatException("chat_rate_limited", "Challenge chat send rate exceeded.", retryAfter);

            var next = checked((await Messages(request.ChallengeId).MaxAsync(row => (long?)row.Sequence, cancellationToken) ?? 0) + 1);
            var message = new BloodMoneyChatMessage(Guid.NewGuid(), request.ChallengeId, next, request.SenderPlayerId,
                request.ClientMessageId, request.Body, now, BloodMoneyChatVisibility.Visible);
            var row = new BloodMoneyChatMessageRow
            {
                Id = message.MessageId, ChallengeId = message.ChallengeId.Value, Sequence = message.Sequence,
                SenderPlayerId = message.SenderPlayerId.Value, ClientMessageId = message.ClientMessageId,
                Body = message.Body, CreatedAt = message.CreatedAt, Visibility = message.Visibility.ToString()
            };
            db.Add(row);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return new(BloodMoneyChatMessageView.From(message), true);
            }
            finally
            {
                // A retrying execution strategy must reload durable state, never reuse a failed candidate.
                db.Entry(row).State = EntityState.Detached;
            }
        }, cancellationToken);

    public Task<BloodMoneyChatParticipantState> GetParticipantStateAsync(BloodMoneyChallengeId challengeId, PlayerId actor, CancellationToken cancellationToken)
        => Serialized(challengeId, actor, _ => State(challengeId, actor, cancellationToken), cancellationToken);

    public Task<BloodMoneyChatParticipantState> AdvanceReadAsync(BloodMoneyChallengeId challengeId, PlayerId actor, long sequence, CancellationToken cancellationToken)
        => Serialized(challengeId, actor, async _ =>
        {
            var latest = await Messages(challengeId).MaxAsync(row => (long?)row.Sequence, cancellationToken) ?? 0;
            if (sequence < 0 || sequence > latest)
                throw new BloodMoneyChatException("invalid_chat_read_sequence", "Read position is outside committed history.");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO blood_money_chat_participant_state ("ChallengeId", "PlayerId", "LastReadSequence", "NotificationsMuted")
                VALUES ({challengeId.Value}, {actor.Value}, {sequence}, false)
                ON CONFLICT ("ChallengeId", "PlayerId") DO UPDATE
                SET "LastReadSequence" = GREATEST(blood_money_chat_participant_state."LastReadSequence", EXCLUDED."LastReadSequence")
                """, cancellationToken);
            return await State(challengeId, actor, cancellationToken);
        }, cancellationToken);

    public Task<BloodMoneyChatParticipantState> SetNotificationsMutedAsync(BloodMoneyChallengeId challengeId, PlayerId actor, bool muted, CancellationToken cancellationToken)
        => Serialized(challengeId, actor, async _ =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO blood_money_chat_participant_state ("ChallengeId", "PlayerId", "LastReadSequence", "NotificationsMuted")
                VALUES ({challengeId.Value}, {actor.Value}, 0, {muted})
                ON CONFLICT ("ChallengeId", "PlayerId") DO UPDATE SET "NotificationsMuted" = EXCLUDED."NotificationsMuted"
                """, cancellationToken);
            return await State(challengeId, actor, cancellationToken);
        }, cancellationToken);

    private async Task<T> Serialized<T>(BloodMoneyChallengeId id, PlayerId actor,
        Func<BloodMoneyChallenge, Task<T>> operation, CancellationToken cancellationToken)
    {
        // A chat commit must never flush another use case's staged challenge/financial work.
        if (db.ChangeTracker.HasChanges() || db.Database.CurrentTransaction is not null || System.Transactions.Transaction.Current is not null)
            throw new InvalidOperationException("Chat requires a clean scope with no caller-owned transaction.");
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async token =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
            var locked = await db.Database.SqlQuery<Guid>($"""
                SELECT "Id" AS "Value" FROM blood_money_challenges WHERE "Id" = {id.Value} FOR UPDATE
                """).ToListAsync(token);
            if (locked.Count == 0) throw Missing();
            // AsNoTracking is essential: a caller's earlier tracked lifecycle read must not win here.
            var row = await db.Set<BloodMoneyChallengeRow>().AsNoTracking().Include(row => row.Participants).AsSingleQuery()
                .SingleAsync(row => row.Id == id.Value, token);
            var challenge = BloodMoneyChallengeStore.FromRow(row)!;
            if (challenge.ActivatedAt is null || !challenge.Participants.Any(participant => participant.PlayerId == actor))
                throw Missing();
            var result = await operation(challenge);
            await transaction.CommitAsync(token);
            return result;
        }, cancellationToken);
    }

    private IQueryable<BloodMoneyChatMessageRow> Messages(BloodMoneyChallengeId id)
        => db.Set<BloodMoneyChatMessageRow>().AsNoTracking().Where(row => row.ChallengeId == id.Value);

    private async Task<BloodMoneyChatParticipantState> State(BloodMoneyChallengeId id, PlayerId actor, CancellationToken cancellationToken)
        => await db.Set<BloodMoneyChatParticipantStateRow>().AsNoTracking()
            .Where(row => row.ChallengeId == id.Value && row.PlayerId == actor.Value)
            .Select(row => new BloodMoneyChatParticipantState(row.LastReadSequence, row.NotificationsMuted))
            .SingleOrDefaultAsync(cancellationToken) ?? new(0, false);

    private static BloodMoneyChatMessageView View(BloodMoneyChatMessageRow row)
        => BloodMoneyChatMessageView.From(new(row.Id, new(row.ChallengeId), row.Sequence, new(row.SenderPlayerId),
            row.ClientMessageId, row.Body, row.CreatedAt, Enum.Parse<BloodMoneyChatVisibility>(row.Visibility)));

    private static TimeSpan RetryAfter(List<DateTimeOffset> descendingTimes, DateTimeOffset now, int limit, TimeSpan window)
        => descendingTimes.Count >= limit && descendingTimes[limit - 1] > now - window
            ? descendingTimes[limit - 1] + window - now : TimeSpan.Zero;
    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left > right ? left : right;
    private static NotFoundException Missing() => new("Challenge chat not found.");
}
