using Level5.Application.Abstractions;
using Level5.Application.Common;
using Level5.Domain.Ids;
using Level5.Domain.Migration;
using Level5.Domain.Results;

namespace Level5.Application.Migration;

public sealed record ImportLegacyMatchResultRequest(
    int LegacyHighscoreId,
    int LegacyUserId,
    LegacyHighscoreMappingInput SourceRow);

public enum ImportLegacyMatchResultOutcome
{
    Imported,
    AttachedToExistingCompatibleResult,
    AlreadyLinkedConsistent,
    Blocked
}

public sealed record ImportLegacyMatchResultResult(
    ImportLegacyMatchResultOutcome Outcome,
    MatchResultId? MatchResultId,
    string? BlockReason);

/// <summary>
/// Imports one legacy V1 <c>highscores</c> row as a V2 <see cref="MatchResult"/> +
/// <see cref="LegacyMatchResultLink"/>, atomically, using <c>legacy_account_links</c> as the sole
/// ownership bridge - a row with no account link is always blocked, never resolved by username,
/// email, or an invented account (issue: historical score migration). Idempotent: re-running with
/// the same <see cref="ImportLegacyMatchResultRequest.LegacyHighscoreId"/> either confirms the
/// prior import is still consistent and reports
/// <see cref="ImportLegacyMatchResultOutcome.AlreadyLinkedConsistent"/> (no writes), or throws
/// <see cref="LegacyMigrationInconsistentException"/> if the prior state is corrupt, or imports
/// fresh. A pre-existing, payload-identical V2 result (a transition-period match already written by
/// both V1 and a live V2 submission) is never duplicated - only linked.
/// </summary>
public sealed class ImportLegacyMatchResultUseCase(
    ILegacyAccountLinkStore legacyAccountLinkStore,
    ILegacyMatchResultLinkStore legacyMatchResultLinkStore,
    IMatchResultStore matchResultStore,
    ILeaderboardPolicyCatalog leaderboardPolicyCatalog,
    IClock clock)
{
    public async Task<ImportLegacyMatchResultResult> ExecuteAsync(ImportLegacyMatchResultRequest request, CancellationToken cancellationToken)
    {
        var existingLink = await legacyMatchResultLinkStore.FindByLegacyHighscoreIdAsync(request.LegacyHighscoreId, cancellationToken);
        if (existingLink is not null)
        {
            return await VerifyExistingLinkAsync(existingLink, request, cancellationToken);
        }

        var accountLink = await legacyAccountLinkStore.FindByLegacyUserIdAsync(request.LegacyUserId, cancellationToken);
        if (accountLink is null)
        {
            return Blocked(
                $"No legacy_account_links row exists for legacy user {request.LegacyUserId}. Ownership cannot be established; refusing to migrate.");
        }

        var mapping = LegacyHighscoreMapper.TryMap(request.SourceRow);
        if (!mapping.IsValid)
        {
            return Blocked(mapping.BlockDetail!);
        }

        // Defense-in-depth, mirroring SubmitMatchResultUseCase's own check: unreachable in practice
        // since all six supported metrics are always mapped, but a mode with a leaderboard policy
        // must never be importable without the metric that policy ranks by.
        if (leaderboardPolicyCatalog.TryResolve(mapping.ModeId) is { } policy && mapping.Metrics!.ValueOf(policy.RankingMetric) is null)
        {
            return Blocked($"Mode {mapping.ModeId} requires the '{policy.RankingMetric}' metric to be included in the result.");
        }

        var existingResult = await matchResultStore.FindByClientResultIdAsync(accountLink.PlayerId, mapping.ClientResultId, cancellationToken);
        if (existingResult is not null)
        {
            return await AttachOrBlockAsync(existingResult, request, mapping, cancellationToken);
        }

        var now = clock.UtcNow;
        var result = MatchResult.Submit(
            accountLink.PlayerId, mapping.ClientResultId, mapping.ModeId, mapping.LevelId, mapping.CharacterId!,
            mapping.ClientVersion!, mapping.Platform!, mapping.Metrics!, mapping.Modifiers!, now);
        var link = new LegacyMatchResultLink(request.LegacyHighscoreId, result.Id, request.SourceRow.Scoreid, now);

        // A concurrent duplicate may lose an insert race under the same (PlayerId, ClientResultId)
        // idempotency key - e.g. a transition-period live submission landing between this row's
        // pre-check above and its own insert. Reload whoever actually won it and resolve exactly
        // like a sequential retry, same pattern as SubmitMatchResultUseCase.
        var raced = await IdempotentInsertRecovery.TryInsertAsync(
            ct => legacyMatchResultLinkStore.ImportAsync(result, link, ct),
            ct => matchResultStore.FindByClientResultIdAsync(accountLink.PlayerId, mapping.ClientResultId, ct),
            cancellationToken);

        if (raced is not null)
        {
            return await AttachOrBlockAsync(raced, request, mapping, cancellationToken);
        }

        return new ImportLegacyMatchResultResult(ImportLegacyMatchResultOutcome.Imported, result.Id, null);
    }

    private async Task<ImportLegacyMatchResultResult> AttachOrBlockAsync(
        MatchResult existingResult, ImportLegacyMatchResultRequest request, LegacyHighscoreMappingResult mapping, CancellationToken cancellationToken)
    {
        var sameRequest = existingResult.MatchesRequest(
            mapping.ModeId, mapping.LevelId, mapping.CharacterId!, mapping.ClientVersion!, mapping.Platform!, mapping.Metrics!, mapping.Modifiers!);
        if (!sameRequest)
        {
            return Blocked(
                $"An existing V2 match result ({existingResult.Id}) for this player/clientResultId has different material fields. Refusing to overwrite.");
        }

        var link = new LegacyMatchResultLink(request.LegacyHighscoreId, existingResult.Id, request.SourceRow.Scoreid, clock.UtcNow);

        try
        {
            await legacyMatchResultLinkStore.AttachAsync(link, cancellationToken);
        }
        catch (ConflictException)
        {
            // A concurrent migrate run on this exact LegacyHighscoreId won the race to attach first
            // (or to import fresh) between our own existing-link check at the top of ExecuteAsync and
            // this write - reload whoever actually committed and resolve it exactly like a sequential
            // rerun would, rather than letting the race surface as an unhandled conflict.
            var raced = await legacyMatchResultLinkStore.FindByLegacyHighscoreIdAsync(request.LegacyHighscoreId, cancellationToken);
            if (raced is null)
            {
                throw;
            }

            return await VerifyExistingLinkAsync(raced, request, cancellationToken);
        }

        return new ImportLegacyMatchResultResult(ImportLegacyMatchResultOutcome.AttachedToExistingCompatibleResult, existingResult.Id, null);
    }

    private async Task<ImportLegacyMatchResultResult> VerifyExistingLinkAsync(
        LegacyMatchResultLink link, ImportLegacyMatchResultRequest request, CancellationToken cancellationToken)
    {
        var result = await LegacyMatchResultLinkConsistencyChecker.CheckAsync(
            link, request.LegacyUserId, request.SourceRow, matchResultStore, legacyAccountLinkStore, leaderboardPolicyCatalog, cancellationToken);
        if (!result.IsConsistent)
        {
            throw new LegacyMigrationInconsistentException(
                result.Reason + " Refusing to proceed - this indicates manual/partial data tampering and must be investigated by hand.");
        }

        return new ImportLegacyMatchResultResult(ImportLegacyMatchResultOutcome.AlreadyLinkedConsistent, link.MatchResultId, null);
    }

    private static ImportLegacyMatchResultResult Blocked(string reason)
        => new(ImportLegacyMatchResultOutcome.Blocked, null, reason);
}
