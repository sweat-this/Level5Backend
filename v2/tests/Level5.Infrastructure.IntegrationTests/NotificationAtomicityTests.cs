using Level5.Application.Abstractions;
using Level5.Application.Social;
using Level5.Domain.Ids;
using Level5.Domain.Platform;
using Level5.Infrastructure.Persistence;
using Level5.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Level5.Infrastructure.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class NotificationAtomicityTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Notification_failure_rolls_back_the_staged_friend_request()
    {
        PlayerId sender;
        PlayerId recipient;
        await using (var seedDb = fixture.CreateDbContext())
        {
            sender = await PlayerSeeding.CreatePlayerAsync(seedDb, "atomicsend", Now);
            recipient = await PlayerSeeding.CreatePlayerAsync(seedDb, "atomicrecv", Now);
        }

        await using (var writeDb = fixture.CreateDbContext())
        {
            var useCase = new SendFriendRequestUseCase(
                new FriendshipStore(writeDb),
                new PlayerProfileStore(writeDb),
                new InvalidRecipientNotificationWriter(new NotificationStore(writeDb)),
                new EfUnitOfWork(writeDb),
                new FixedClock());

            await Assert.ThrowsAsync<DbUpdateException>(() => useCase.ExecuteAsync(
                new SendFriendRequestRequest(sender, recipient), CancellationToken.None));
        }

        await using var verifyDb = fixture.CreateDbContext();
        Assert.False(await verifyDb.FriendRequests.AnyAsync(row =>
            row.FromPlayerId == sender.Value && row.ToPlayerId == recipient.Value));
        Assert.False(await verifyDb.PlayerNotifications.AnyAsync(row => row.SourceEventKey == "forced-invalid-recipient"));
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class InvalidRecipientNotificationWriter(INotificationStore store) : INotificationWriter
    {
        public Task WriteAsync(NotificationDraft draft, CancellationToken cancellationToken)
            => store.AddAsync(PlayerNotification.Create(
                PlayerId.New(),
                draft.Source,
                draft.Kind,
                draft.Title,
                draft.Body,
                draft.ActionPath,
                "forced-invalid-recipient",
                Now), cancellationToken);
    }
}
