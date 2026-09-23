using Level5.Application.Abstractions;

namespace Level5.Infrastructure.Persistence;

public sealed class EfUnitOfWork(Level5V2DbContext db) : IUnitOfWork
{
    public Task SaveChangesAsync(CancellationToken cancellationToken)
        => db.SaveChangesTranslatingConflictsAsync(cancellationToken);
}
