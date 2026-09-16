namespace Level5.Application.Abstractions;

/// <summary>Commits the changes staged on the current request's stores as one atomic transaction.</summary>
public interface IUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
