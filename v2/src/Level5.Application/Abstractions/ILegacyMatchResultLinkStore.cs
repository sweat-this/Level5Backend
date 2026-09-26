using Level5.Domain.Migration;
using Level5.Domain.Results;

namespace Level5.Application.Abstractions;

/// <summary>
/// Provenance store for the legacy score migration tool. No Update/Delete: a link is never edited
/// once created. Deliberately owns the one migration-specific persistence boundary this slice
/// needs: <see cref="ImportAsync"/> commits a brand-new <see cref="MatchResult"/> together with its
/// provenance link in a single transaction - something <see cref="IMatchResultStore.AddAsync"/>
/// cannot be composed to do, since it already calls <c>SaveChanges</c> internally on its own. This
/// is the smallest boundary that guarantees the atomicity the migration tool needs without changing
/// ordinary runtime result-submission semantics or introducing a generic unit-of-work abstraction.
/// </summary>
public interface ILegacyMatchResultLinkStore
{
    Task<LegacyMatchResultLink?> FindByLegacyHighscoreIdAsync(int legacyHighscoreId, CancellationToken cancellationToken);

    /// <summary>Persists a newly migrated <see cref="MatchResult"/> together with its provenance link, atomically.</summary>
    Task ImportAsync(MatchResult result, LegacyMatchResultLink link, CancellationToken cancellationToken);

    /// <summary>Attaches a provenance link to an already-existing, payload-compatible V2 result. Writes no <see cref="MatchResult"/>.</summary>
    Task AttachAsync(LegacyMatchResultLink link, CancellationToken cancellationToken);
}
