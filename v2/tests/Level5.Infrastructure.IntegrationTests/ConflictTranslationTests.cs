using Level5.Application.Common;
using Level5.Domain.Identity;
using Level5.Infrastructure.Persistence;
using Level5.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Level5.Infrastructure.IntegrationTests;

/// <summary>
/// Pins the boundary of what counts as a client-visible conflict. Run against real Postgres
/// because the distinction is made on actual SQLSTATE values, which no in-memory fake produces.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ConflictTranslationTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public async Task A_unique_violation_is_translated_into_a_ConflictException()
    {
        await using var db = fixture.CreateDbContext();
        var store = new AccountStore(db);
        var unitOfWork = new EfUnitOfWork(db);

        await store.AddAsync(Account.Register(Username.Create("raceWinner"), "hash", Now), CancellationToken.None);
        await unitOfWork.SaveChangesAsync(CancellationToken.None);

        // Simulates the loser of a registration race: the application-layer existence check
        // passed, but the unique index rejects the insert at commit time.
        await store.AddAsync(Account.Register(Username.Create("RACEWINNER"), "hash2", Now), CancellationToken.None);

        await Assert.ThrowsAsync<ConflictException>(() => unitOfWork.SaveChangesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_database_failure_that_is_not_a_unique_violation_is_left_untranslated()
    {
        await using var db = fixture.CreateDbContext();
        var unitOfWork = new EfUnitOfWork(db);

        // PasswordHash is varchar(512); overflowing it produces SQLSTATE 22001, which is a
        // server-side fault rather than a conflict and must not be reported to a client as one.
        await new AccountStore(db).AddAsync(
            Account.Register(Username.Create("truncation"), new string('x', 600), Now), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => unitOfWork.SaveChangesAsync(CancellationToken.None));

        Assert.IsNotType<ConflictException>(exception);
    }
}
