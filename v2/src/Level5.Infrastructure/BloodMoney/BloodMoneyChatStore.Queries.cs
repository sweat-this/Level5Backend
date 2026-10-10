using System.Data;
using System.Globalization;
using Level5.Application.BloodMoney;
using Level5.Domain.BloodMoney;
using Microsoft.EntityFrameworkCore;

namespace Level5.Infrastructure.BloodMoney;

public sealed partial class BloodMoneyChatStore
{
    public async Task<BloodMoneyChatPage> ListAsync(ListBloodMoneyChallengeMessagesRequest request, CancellationToken cancellationToken)
    {
        RequireCleanScope();
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async token =>
        {
            // PostgreSQL MVCC supplies one committed view without holding the send/closure row lock.
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
            await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", token);
            var challenge = await Authorize(request.ChallengeId, request.Actor, token);
            var limit = 50;
            if (request.Limit is not null && (!int.TryParse(request.Limit, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > 100))
                throw new BloodMoneyChatException("invalid_chat_limit", "Chat limit must be between 1 and 100.");
            // Token parsing is deliberately after canonical authorization.
            var cursor = request.Cursor is null ? null : cursors.Decode(request.Cursor, request.ChallengeId);
            var messages = Messages(request.ChallengeId);
            var latest = await messages.MaxAsync(row => (long?)row.Sequence, token) ?? 0;
            var snapshot = cursor?.Direction == BloodMoneyChatDirection.Older ? cursor.SnapshotUpperBound : latest;
            var query = messages.Where(row => row.Sequence <= snapshot);
            if (cursor?.Direction == BloodMoneyChatDirection.Resume)
                query = query.Where(row => row.Sequence > cursor.BoundarySequence).OrderBy(row => row.Sequence);
            else
            {
                if (cursor is not null) query = query.Where(row => row.Sequence < cursor.BoundarySequence);
                query = query.OrderByDescending(row => row.Sequence);
            }
            // Project at the SQL boundary: protected suppressed text never leaves PostgreSQL on a list.
            var items = await query.Take(limit).Select(row => new BloodMoneyChatMessageView(row.Id, new(row.ChallengeId),
                row.Sequence, new(row.SenderPlayerId), row.ClientMessageId,
                row.Visibility == "Visible" ? row.Body : null, row.CreatedAt,
                row.Visibility == "Visible" ? BloodMoneyChatVisibility.Visible : BloodMoneyChatVisibility.Suppressed)).ToListAsync(token);
            items.Sort((left, right) => left.Sequence.CompareTo(right.Sequence));
            string? older = null;
            if (items.Count > 0 && await messages.AnyAsync(row => row.Sequence < items[0].Sequence && row.Sequence <= snapshot, token))
                older = cursors.Encode(new(BloodMoneyChatDirection.Older, request.ChallengeId, items[0].Sequence, snapshot));
            var resumeBoundary = cursor?.Direction == BloodMoneyChatDirection.Resume
                ? (items.Count == 0 ? cursor.BoundarySequence : items[^1].Sequence) : snapshot;
            var resume = cursors.Encode(new(BloodMoneyChatDirection.Resume, request.ChallengeId, resumeBoundary, snapshot));
            var state = await State(request.ChallengeId, request.Actor, token);
            await transaction.CommitAsync(token);
            return new BloodMoneyChatPage(items, older, resume, latest, state.LastReadSequence,
                challenge.Status != BloodMoneyChallengeStatus.Active, state.NotificationsMuted);
        }, cancellationToken);
    }

    public Task<BloodMoneyChatReportAcceptance> ReportAsync(ReportBloodMoneyChatMessageRequest request, CancellationToken cancellationToken)
        => Serialized(request.ChallengeId, request.Reporter, async _ =>
        {
            if (!Enum.IsDefined(request.Reason))
                throw new BloodMoneyChatException("invalid_chat_report", "Chat report reason is invalid.");
            if (!await Messages(request.ChallengeId).AnyAsync(row => row.Id == request.MessageId, cancellationToken))
                throw Missing();
            var reports = db.Set<BloodMoneyChatReportRow>();
            if (await reports.AsNoTracking().AnyAsync(row => row.ChallengeId == request.ChallengeId.Value
                && row.MessageId == request.MessageId && row.ReporterPlayerId == request.Reporter.Value, cancellationToken))
                throw new BloodMoneyChatException("chat_report_already_exists", "A report for this message already exists.");
            var now = clock.UtcNow.ToUniversalTime();
            var report = new BloodMoneyChatReportRow
            {
                ReportId = Guid.NewGuid(), ChallengeId = request.ChallengeId.Value, MessageId = request.MessageId,
                ReporterPlayerId = request.Reporter.Value, Reason = request.Reason.ToString(), ReportedAt = now.AddTicks(-(now.Ticks % 10))
            };
            db.Add(report);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return new BloodMoneyChatReportAcceptance(report.ReportId);
            }
            finally { db.Entry(report).State = EntityState.Detached; }
        }, cancellationToken);
}
