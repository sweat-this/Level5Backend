using Level5.Application.Abstractions;
using Level5.Domain.Ids;
using Level5.Domain.Migration;
using Level5.Domain.Results;
using Level5.Infrastructure.Persistence.Rows;
using Microsoft.EntityFrameworkCore;

namespace Level5.Infrastructure.Persistence.Repositories;

public sealed class LegacyMatchResultLinkStore(Level5V2DbContext db) : ILegacyMatchResultLinkStore
{
    public async Task<LegacyMatchResultLink?> FindByLegacyHighscoreIdAsync(int legacyHighscoreId, CancellationToken cancellationToken)
    {
        var row = await db.LegacyMatchResultLinks.AsNoTracking()
            .SingleOrDefaultAsync(l => l.LegacyHighscoreId == legacyHighscoreId, cancellationToken);
        return row is null ? null : ToDomain(row);
    }

    public async Task ImportAsync(MatchResult result, LegacyMatchResultLink link, CancellationToken cancellationToken)
    {
        var resultRow = MatchResultStore.ToRow(result);
        var linkRow = ToRow(link);

        db.MatchResults.Add(resultRow);
        db.LegacyMatchResultLinks.Add(linkRow);

        try
        {
            // Both rows commit in one transaction: SaveChangesAsync wraps every pending Add in a
            // single database transaction by default, so a failure here (e.g. a concurrent live
            // submission winning the (PlayerId, ClientResultId) unique-index race) leaves neither
            // row committed - never a MatchResult with no provenance link, or vice versa.
            await db.SaveChangesTranslatingConflictsAsync(cancellationToken);
        }
        catch
        {
            db.Entry(resultRow).State = EntityState.Detached;
            db.Entry(linkRow).State = EntityState.Detached;
            throw;
        }
    }

    public async Task AttachAsync(LegacyMatchResultLink link, CancellationToken cancellationToken)
    {
        var linkRow = ToRow(link);
        db.LegacyMatchResultLinks.Add(linkRow);

        try
        {
            await db.SaveChangesTranslatingConflictsAsync(cancellationToken);
        }
        catch
        {
            db.Entry(linkRow).State = EntityState.Detached;
            throw;
        }
    }

    private static LegacyMatchResultLinkRow ToRow(LegacyMatchResultLink link) => new()
    {
        LegacyHighscoreId = link.LegacyHighscoreId,
        MatchResultId = link.MatchResultId.Value,
        LegacyScoreId = link.LegacyScoreId,
        MigratedAt = link.MigratedAt
    };

    private static LegacyMatchResultLink ToDomain(LegacyMatchResultLinkRow row)
        => new(row.LegacyHighscoreId, new MatchResultId(row.MatchResultId), row.LegacyScoreId, row.MigratedAt);
}
