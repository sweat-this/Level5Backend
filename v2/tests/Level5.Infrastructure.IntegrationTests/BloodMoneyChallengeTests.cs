using System.Data.Common;
using Level5.Application.Abstractions;
using Level5.Application.BloodMoney;
using Level5.Application.Common;
using Level5.Domain.BloodMoney;
using Level5.Domain.Ids;
using Level5.Domain.Social;
using Level5.Infrastructure.BloodMoney;
using Level5.Infrastructure.Persistence;
using Level5.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Level5.Infrastructure.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class BloodMoneyChallengeTests(PostgresFixture fixture)
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTimeOffset Start = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly BloodMoneyChallengeTimingPolicy Timing = new(TimeSpan.FromHours(1), TimeSpan.FromHours(2));

    [Theory]
    [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public async Task Create_partial_accept_activate_and_replay_commit_once_per_effective_transition(int count)
    {
        var players = await Roster(count); var request = Request(players);
        BloodMoneyChallengeView view;
        await using (var db = fixture.CreateDbContext())
        {
            var saves = new CountingUnitOfWork(db);
            view = await Create(db, request, saves);
            Assert.Equal(1, saves.Saves);
            Assert.Equal(1, view.Revision);
            Assert.Equal(BloodMoneyChallengeStatus.PendingAcceptance, view.Status);
            Assert.Equal(Start.AddHours(1), view.AcceptanceDeadlineAt);
            Assert.Null(view.GameplayDeadlineAt);
            var replay = await Create(db, request with { RulesetId = " classic " }, saves);
            Assert.Equal(view.ChallengeId, replay.ChallengeId); Assert.Equal(1, saves.Saves);
        }
        await AssertCoherent(view.ChallengeId, players);
        foreach (var actor in players.Skip(1))
        {
            await using var db = fixture.CreateDbContext(); var saves = new CountingUnitOfWork(db);
            view = await Command(db, view.ChallengeId, actor, "accept", Start.AddMinutes(10), saves);
            Assert.Equal(1, saves.Saves);
            var replay = await Command(db, view.ChallengeId, actor, "accept", Start.AddMinutes(11), saves);
            Assert.Equal(view.Revision, replay.Revision); Assert.Equal(1, saves.Saves);
            await AssertCoherent(view.ChallengeId, players);
        }
        Assert.Equal(BloodMoneyChallengeStatus.Active, view.Status);
        Assert.Equal(count, view.Revision);
        Assert.Equal(Start.AddMinutes(10), view.ActivatedAt);
        Assert.Equal(Start.AddHours(2).AddMinutes(10), view.GameplayDeadlineAt);
        await using var check = fixture.CreateDbContext();
        Assert.Equal(count, await check.Set<BloodCreditReservationRow>().CountAsync(r => r.ChallengeId == view.ChallengeId.Value));
        Assert.Equal(players, view.Participants.Select(p => p.PlayerId));
        // Each invitee is friends only with the creator. No invitee-to-invitee relationship is needed.
        if (count > 2) Assert.False(await new FriendshipStore(check).AreFriendsAsync(players[1], players[2], Ct));
    }

    [Theory]
    [InlineData("decline")] [InlineData("cancel")] [InlineData("expire")]
    public async Task Pending_terminal_paths_release_only_existing_holds_once_and_preserve_replay_attribution(string operation)
    {
        var players = await Roster(4); var view = await CreateFresh(Request(players));
        await using (var db = fixture.CreateDbContext()) await Command(db, view.ChallengeId, players[1], "accept", Start.AddMinutes(1));
        var actor = operation == "decline" ? players[2] : players[0];
        var now = operation == "expire" ? view.AcceptanceDeadlineAt : Start.AddMinutes(2);
        await using (var db = fixture.CreateDbContext())
        {
            var saves = new CountingUnitOfWork(db);
            var terminal = await Command(db, view.ChallengeId, actor, operation, now, saves);
            Assert.Equal(3, terminal.Revision); Assert.Equal(1, saves.Saves);
            Assert.Equal(now, terminal.TerminalAt);
            Assert.Equal(terminal.Revision, (await Command(db, view.ChallengeId, actor, operation, now.AddDays(1), saves)).Revision);
            Assert.Equal(1, saves.Saves);
            if (operation == "decline") await Assert.ThrowsAsync<InvalidBloodMoneyChallengeException>(() => Command(db, view.ChallengeId, players[3], "decline", now));
        }
        await AssertCoherent(view.ChallengeId, players);
        await using var check = fixture.CreateDbContext();
        var holds = await check.Set<BloodCreditReservationRow>().Where(r => r.ChallengeId == view.ChallengeId.Value).ToListAsync();
        Assert.Equal(2, holds.Count);
        var reason = operation == "decline" ? "Declined" : operation == "cancel" ? "Cancelled" : "PendingExpired";
        Assert.All(holds, r => { Assert.Equal("Released", r.Status); Assert.Equal(reason, r.ReleaseReason); });
    }

    [Theory]
    [InlineData("accept")] [InlineData("decline")] [InlineData("cancel")] [InlineData("creator-replay")]
    public async Task Deadline_equality_wins_over_player_actions_including_creator_accept_replay(string action)
    {
        var players = await Roster(2); var view = await CreateFresh(Request(players));
        var actor = action is "cancel" or "creator-replay" ? players[0] : players[1];
        await using var db = fixture.CreateDbContext();
        var expired = await Command(db, view.ChallengeId, actor, action == "creator-replay" ? "accept" : action, view.AcceptanceDeadlineAt);
        Assert.Equal(BloodMoneyChallengeStatus.Expired, expired.Status); Assert.Equal(2, expired.Revision);
        await AssertCoherent(view.ChallengeId, players);
    }

    [Fact]
    public async Task Active_gameplay_deadline_never_authorizes_cancel_decline_pending_expiry_or_credit_release()
    {
        var players = await Roster(2); var view = await CreateFresh(Request(players));
        await using (var db = fixture.CreateDbContext()) view = await Command(db, view.ChallengeId, players[1], "accept", Start);
        var late = view.GameplayDeadlineAt!.Value;
        foreach (var action in new[] { "cancel", "decline", "expire" })
        {
            await using var db = fixture.CreateDbContext(); var saves = new CountingUnitOfWork(db);
            await Assert.ThrowsAsync<InvalidBloodMoneyChallengeException>(() => Command(db, view.ChallengeId, action == "decline" ? players[1] : players[0], action, late, saves));
            Assert.Equal(0, saves.Saves);
            Assert.DoesNotContain(db.ChangeTracker.Entries(), e => e.State is EntityState.Added or EntityState.Modified);
        }
        await using (var db = fixture.CreateDbContext())
        {
            var saves = new CountingUnitOfWork(db);
            Assert.Equal(view.Revision, (await Command(db, view.ChallengeId, players[1], "accept", late.AddDays(1), saves)).Revision);
            Assert.Equal(BloodMoneyChallengeStatus.Active, (await new GetBloodMoneyChallengeUseCase(new BloodMoneyChallengeStore(db)).ExecuteAsync(view.ChallengeId, players[0], Ct)).Status);
            Assert.Equal(0, saves.Saves);
        }
        await AssertCoherent(view.ChallengeId, players);
    }

    [Fact]
    public async Task Insufficient_creator_or_acceptor_leaves_every_durable_piece_unchanged()
    {
        var players = await Roster(3, 20); var request = Request(players);
        await using (var db = fixture.CreateDbContext())
            await Assert.ThrowsAsync<InsufficientCreditsException>(() => Create(db, request));
        await using (var db = fixture.CreateDbContext())
        {
            Assert.Null(await new BloodMoneyChallengeStore(db).FindByCreateRequestAsync(players[0], request.ClientRequestId, Ct));
            Assert.Equal(3, await db.Set<BloodCreditTransactionRow>().CountAsync(r => players.Select(p => p.Value).Contains(r.SubjectPlayerId)));
            await new IssueBloodCreditsUseCase(new BloodCreditLedgerStore(db), new EfUnitOfWork(db), new Clock(Start))
                .ExecuteAsync(new(BloodCreditTransactionId.New(), players[0], 80, "top-up"), Ct);
        }
        var view = await CreateFresh(request);
        await using (var db = fixture.CreateDbContext())
            await Assert.ThrowsAsync<InsufficientCreditsException>(() => Command(db, view.ChallengeId, players[1], "accept", Start));
        await using var check = fixture.CreateDbContext();
        var challenge = (await new BloodMoneyChallengeStore(check).FindAsync(view.ChallengeId, Ct))!;
        Assert.Equal(1, challenge.Revision); Assert.Equal(BloodMoneyParticipantStatus.Invited, challenge.Participant(players[1]).Status);
        Assert.Null(await new BloodCreditReservationStore(check).FindAsync(view.ChallengeId, players[1], Ct));
        Assert.Single(await check.Set<BloodCreditTransactionRow>().Where(r => r.SubjectPlayerId == players[1].Value).ToListAsync());
    }

    [Fact]
    public async Task Current_admission_rejects_five_duplicate_self_and_empty_rosters_before_financial_staging()
    {
        var players = await Roster(5); var request = Request(players);
        foreach (var bad in new[] { request, request with { Invitees = [] }, request with { Invitees = [players[0]] },
                     request with { Invitees = [players[1], players[1]] }, request with { Invitees = [default] },
                     request with { CreatorPlayerId = default, Invitees = [players[1]] } })
        {
            await using var db = fixture.CreateDbContext();
            Assert.NotNull(await Record.ExceptionAsync(() => Create(db, bad)));
            Assert.Empty(db.ChangeTracker.Entries());
        }
        await using var check = fixture.CreateDbContext();
        Assert.Empty(await check.Set<BloodMoneyChallengeRow>().Where(r => r.CreatorPlayerId == players[0].Value).ToListAsync());
    }

    [Fact]
    public async Task Missing_friendship_rejects_create_but_unfriending_does_not_mutate_existing_authority_or_replays()
    {
        var players = await Roster(3); var request = Request(players);
        var view = await CreateFresh(request);
        await using (var db = fixture.CreateDbContext())
        {
            var friends = new FriendshipStore(db);
            await friends.RemoveFriendshipAsync((await friends.FindFriendshipBetweenAsync(players[0], players[1], Ct))!, Ct);
            await db.SaveChangesAsync();
        }
        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal(view.ChallengeId, (await Create(db, request)).ChallengeId);
            await Assert.ThrowsAsync<FriendshipRequiredException>(() => Create(db, request with { ClientRequestId = Guid.NewGuid() }));
            Assert.DoesNotContain(db.ChangeTracker.Entries(), e => e.State is EntityState.Added or EntityState.Modified);
        }
        await AssertCoherent(view.ChallengeId, players);
        await using (var db = fixture.CreateDbContext()) await Command(db, view.ChallengeId, players[1], "accept", Start);
        await AssertCoherent(view.ChallengeId, players);
    }

    [Fact]
    public async Task Create_fingerprint_includes_order_stake_and_versioned_rules_and_reads_are_participant_private()
    {
        var players = await Roster(3); var request = Request(players); var view = await CreateFresh(request);
        foreach (var changed in new[] { request with { Invitees = request.Invitees.Reverse().ToArray() }, request with { StakePerParticipant = 31 },
                     request with { RulesetId = "other" }, request with { RulesetVersion = 2 } })
        {
            await using var db = fixture.CreateDbContext(); await Assert.ThrowsAsync<ConflictException>(() => Create(db, changed));
        }
        await using var check = fixture.CreateDbContext(); var get = new GetBloodMoneyChallengeUseCase(new BloodMoneyChallengeStore(check));
        Assert.Equal(view.ChallengeId, (await get.ExecuteAsync(view.ChallengeId, players[1], Ct)).ChallengeId);
        await Assert.ThrowsAsync<NotFoundException>(() => get.ExecuteAsync(view.ChallengeId, PlayerId.New(), Ct));
        foreach (var action in new[] { "accept", "decline", "cancel" })
            await Assert.ThrowsAsync<NotFoundException>(() => Command(check, view.ChallengeId, PlayerId.New(), action, Start));
        await Assert.ThrowsAsync<ConflictException>(() => Command(check, view.ChallengeId, players[1], "cancel", view.AcceptanceDeadlineAt));
        Assert.DoesNotContain(typeof(BloodMoneyChallengeView).GetProperties(), p => p.Name.Contains("Transaction") || p.Name.Contains("Account") || p.Name.Contains("Treasury"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Create_rechecks_committed_request_when_duplicate_consumes_remaining_credits(bool changedIntent)
    {
        var players = await Roster(2, 30); var request = Request(players);
        await using var delayed = fixture.CreateDbContext();
        var store = new PausedCreateLookup(new BloodMoneyChallengeStore(delayed));
        var saves = new CountingUnitOfWork(delayed);
        var pending = Create(delayed, changedIntent ? request with { RulesetVersion = 2 } : request, saves, store);
        await store.Read.Task.WaitAsync(TimeSpan.FromSeconds(30));
        BloodMoneyChallengeView winner;
        try { winner = await CreateFresh(request); }
        finally { store.Resume.TrySetResult(); }

        if (changedIntent) await Assert.ThrowsAsync<ConflictException>(() => pending);
        else
        {
            var replay = await pending;
            Assert.Equal(winner.ChallengeId, replay.ChallengeId);
            Assert.Equal(1, replay.Revision);
        }
        Assert.Equal(0, saves.Saves);
        Assert.DoesNotContain(delayed.ChangeTracker.Entries(), e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
        await using var check = fixture.CreateDbContext();
        Assert.Single(await check.Set<BloodMoneyChallengeRow>().Where(r => r.CreatorPlayerId == players[0].Value).ToListAsync());
        Assert.Single(await check.Set<BloodCreditReservationRow>().Where(r => r.PlayerId == players[0].Value).ToListAsync());
        Assert.Equal(winner.ChallengeId, (await CreateFresh(request)).ChallengeId);
        await AssertCoherent(winner.ChallengeId, players, 30);
    }

    [Theory]
    [InlineData("accept", "accept", 3)]
    [InlineData("accept", "cancel", 3)]
    [InlineData("accept", "decline", 3)]
    [InlineData("accept", "expire", 3)]
    [InlineData("cancel", "expire", 3)]
    [InlineData("same-accept", "decline", 3)]
    [InlineData("duplicate-create", "duplicate-create", 3)]
    [InlineData("accept", "cancel", 2)]
    [InlineData("accept", "expire", 2)]
    [InlineData("same-accept", "decline", 2)]
    public async Task Barrier_races_commit_one_coherent_outcome_and_loser_financial_staging_rolls_back(string first, string second, int count)
    {
        var players = await Roster(count); var request = Request(players);
        var view = first == "duplicate-create" ? null : await CreateFresh(request);
        var barrier = new SaveBarrier();
        async Task<Exception?> Run(string action, int actorIndex)
        {
            await using var db = fixture.CreateDbContext();
            var uow = new CountingUnitOfWork(db, barrier.Arrive);
            return await Record.ExceptionAsync(async () =>
            {
                if (action == "duplicate-create") await Create(db, request, uow);
                else await Command(db, view!.ChallengeId, players[actorIndex], action == "same-accept" ? "accept" : action,
                    action == "expire" ? view.AcceptanceDeadlineAt : Start.AddMinutes(1), uow);
            });
        }
        var outcomes = await Task.WhenAll(Run(first, first == "cancel" ? 0 : 1), Run(second, second is "cancel" or "expire" ? 0 : first == "same-accept" ? 1 : 2));
        Assert.Single(outcomes, e => e is null);
        var loser = Assert.Single(outcomes, e => e is not null);
        Assert.True(loser is ConflictException, loser!.ToString());
        if (view is null)
        {
            view = await CreateFresh(request); // Fresh retry resolves committed create, never reserves twice.
            await using var check = fixture.CreateDbContext();
            Assert.Single(await check.Set<BloodMoneyChallengeRow>().Where(r => r.CreatorPlayerId == players[0].Value).ToListAsync());
        }
        await AssertCoherent(view.ChallengeId, players);
        await using (var fresh = fixture.CreateDbContext())
        {
            var current = (await new BloodMoneyChallengeStore(fresh).FindAsync(view.ChallengeId, Ct))!;
            if (current.Status == BloodMoneyChallengeStatus.PendingAcceptance)
            {
                // Recompute the losing invitation in a fresh scope; no orphan hold survived its loss.
                var next = current.Participants.First(p => p.Status == BloodMoneyParticipantStatus.Invited);
                await Command(fresh, view.ChallengeId, next.PlayerId, "accept", Start.AddMinutes(2));
            }
        }
        await AssertCoherent(view.ChallengeId, players);
    }

    [Theory]
    [InlineData("create", "INSERT INTO blood_money_challenges")]
    [InlineData("create", "INSERT INTO blood_money_challenge_participants")]
    [InlineData("create", "UPDATE blood_money_credit_accounts")]
    [InlineData("create", "INSERT INTO blood_money_credit_transactions")]
    [InlineData("create", "INSERT INTO blood_money_credit_postings")]
    [InlineData("create", "INSERT INTO blood_money_credit_reservations")]
    [InlineData("accept", "UPDATE blood_money_challenges")]
    [InlineData("accept", "UPDATE blood_money_challenge_participants")]
    [InlineData("accept", "UPDATE blood_money_credit_accounts")]
    [InlineData("accept", "INSERT INTO blood_money_credit_reservations")]
    [InlineData("cancel", "UPDATE blood_money_challenges")]
    [InlineData("cancel", "UPDATE blood_money_credit_accounts")]
    [InlineData("cancel", "UPDATE blood_money_credit_reservations")]
    [InlineData("cancel", "INSERT INTO blood_money_credit_postings")]
    [InlineData("decline", "UPDATE blood_money_challenge_participants")]
    public async Task Executed_command_failure_before_commit_rolls_back_lifecycle_participants_and_all_financial_evidence(string action, string command)
    {
        var players = await Roster(3); var request = Request(players);
        var view = action == "create" ? null : await CreateFresh(request);
        var fault = new FailAfterCommand(command);
        await using (var db = fixture.CreateDbContext(fault))
        {
            var error = await Record.ExceptionAsync(async () =>
            {
                if (action == "create") await Create(db, request);
                else await Command(db, view!.ChallengeId, players[action is "accept" or "decline" ? 1 : 0], action, Start.AddMinutes(1));
            });
            Assert.NotNull(error); Assert.True(fault.Fired, command);
        }
        await using (var fresh = fixture.CreateDbContext())
        {
            var unchanged = await new BloodMoneyChallengeStore(fresh).FindByCreateRequestAsync(players[0], request.ClientRequestId, Ct);
            if (action == "create")
            {
                Assert.Null(unchanged);
                Assert.Empty(await fresh.Set<BloodCreditReservationRow>().Where(r => r.PlayerId == players[0].Value).ToListAsync());
                Assert.Equal(100, (await new BloodCreditLedgerStore(fresh).FindAccountAsync(players[0], Ct))!.AvailableBalance);
                Assert.Single(await fresh.Set<BloodCreditTransactionRow>().Where(r => r.SubjectPlayerId == players[0].Value).ToListAsync());
            }
            else
            {
                Assert.NotNull(unchanged); Assert.Equal(1, unchanged.Revision);
                Assert.Equal(BloodMoneyChallengeStatus.PendingAcceptance, unchanged.Status);
                Assert.Equal(BloodMoneyParticipantStatus.Invited, unchanged.Participant(players[1]).Status);
            }
        }
        if (view is not null) await AssertCoherent(view.ChallengeId, players);
        await using (var retry = fixture.CreateDbContext())
        {
            if (action == "create") view = await Create(retry, request);
            else await Command(retry, view!.ChallengeId, players[action is "accept" or "decline" ? 1 : 0], action, Start.AddMinutes(1));
        }
        await AssertCoherent(view!.ChallengeId, players);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Same_participant_accept_decline_reports_revision_conflict_in_either_forced_commit_order(bool acceptWins)
    {
        var players = await Roster(2); var view = await CreateFresh(Request(players));
        var barrier = new SaveBarrier(); var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<Exception?> Run(bool accept)
        {
            await using var db = fixture.CreateDbContext(); var winner = accept == acceptWins;
            var uow = new CountingUnitOfWork(db, async () =>
            { await barrier.Arrive(); if (!winner) await committed.Task.WaitAsync(TimeSpan.FromSeconds(30)); });
            try { return await Record.ExceptionAsync(() => Command(db, view.ChallengeId, players[1], accept ? "accept" : "decline", Start, uow)); }
            finally { if (winner) committed.TrySetResult(); }
        }
        var results = await Task.WhenAll(Run(true), Run(false));
        Assert.Null(results[acceptWins ? 0 : 1]);
        Assert.IsType<ConflictException>(results[acceptWins ? 1 : 0]);
        await AssertCoherent(view.ChallengeId, players);
        await using var check = fixture.CreateDbContext();
        Assert.Equal(acceptWins ? BloodMoneyChallengeStatus.Active : BloodMoneyChallengeStatus.Declined,
            (await new BloodMoneyChallengeStore(check).FindAsync(view.ChallengeId, Ct))!.Status);
    }

    [Fact]
    public async Task Five_participant_rows_round_trip_and_store_retains_original_revision_even_after_requery()
    {
        var players = await Roster(5);
        var challenge = BloodMoneyChallenge.Create(new(Guid.NewGuid()), players[0], Guid.NewGuid(), players.Skip(1), 30, "classic", 1, Start, Start.AddHours(1));
        await using (var db = fixture.CreateDbContext())
        {
            new BloodMoneyChallengeStore(db).Add(challenge);
            await Mutator(db, Start).StageReserveAsync(new(BloodCreditTransactionId.New(), challenge.Id, players[0], 30), Ct);
            await new EfUnitOfWork(db).SaveChangesAsync(Ct);
        }
        await using var stale = fixture.CreateDbContext(); var store = new BloodMoneyChallengeStore(stale);
        var original = (await store.FindAsync(challenge.Id, Ct))!;
        Assert.Equal(5, original.Participants.Count); Assert.Equal(4, original.Participants[4].SeatIndex);
        await using (var winner = fixture.CreateDbContext()) await Command(winner, challenge.Id, players[1], "accept", Start);
        Assert.Equal(original.Revision, (await store.FindAsync(challenge.Id, Ct))!.Revision);
        await Mutator(stale, Start).StageReserveAsync(new(BloodCreditTransactionId.New(), challenge.Id, players[2], 30), Ct);
        var holds = new List<BloodCreditReservation>();
        foreach (var player in players)
        { var hold = await new BloodCreditReservationStore(stale).FindAsync(challenge.Id, player, Ct); if (hold is not null) holds.Add(hold); }
        store.StageUpdate(original.Accept(players[2], Start, Timing.GameplayWindow, holds), original.Revision);
        await Assert.ThrowsAsync<ConflictException>(() => new EfUnitOfWork(stale).SaveChangesAsync(Ct));
        await AssertCoherent(challenge.Id, players);
    }

    [Theory]
    [InlineData("missing")] [InlineData("released")] [InlineData("wrong-amount")]
    public async Task Final_acceptance_rechecks_every_persisted_hold_and_cannot_commit_against_bad_creator_funding(string invalid)
    {
        var players = await Roster(2); var view = await CreateFresh(Request(players));
        await using (var corrupt = fixture.CreateDbContext())
        {
            var row = await corrupt.Set<BloodCreditReservationRow>().SingleAsync(r => r.ChallengeId == view.ChallengeId.Value);
            if (invalid == "missing") corrupt.Remove(row);
            else if (invalid == "wrong-amount") row.Amount++;
            else await Mutator(corrupt, Start).StageReleaseAsync(new(BloodCreditTransactionId.New(), view.ChallengeId, players[0], BloodCreditReleaseReason.Cancelled), Ct);
            await corrupt.SaveChangesAsync();
        }
        await using (var scope = fixture.CreateDbContext())
            await Assert.ThrowsAsync<InvalidBloodMoneyChallengeException>(() => Command(scope, view.ChallengeId, players[1], "accept", Start));
        await using var fresh = fixture.CreateDbContext();
        var challenge = (await new BloodMoneyChallengeStore(fresh).FindAsync(view.ChallengeId, Ct))!;
        Assert.Equal(1, challenge.Revision); Assert.Equal(BloodMoneyChallengeStatus.PendingAcceptance, challenge.Status);
        Assert.Equal(BloodMoneyParticipantStatus.Invited, challenge.Participant(players[1]).Status);
        Assert.Null(await new BloodCreditReservationStore(fresh).FindAsync(view.ChallengeId, players[1], Ct));
        Assert.Equal(100, (await new BloodCreditLedgerStore(fresh).FindAccountAsync(players[1], Ct))!.AvailableBalance);
        Assert.Single(await fresh.Set<BloodCreditTransactionRow>().Where(r => r.SubjectPlayerId == players[1].Value).ToListAsync());
    }

    [Fact]
    public async Task Opt_in_composition_uses_one_scope_for_lifecycle_and_financial_ports_with_host_supplied_timing()
    {
        var players = await Roster(2);
        var services = new ServiceCollection();
        services.AddScoped(_ => fixture.CreateDbContext());
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<IFriendshipStore, FriendshipStore>();
        services.AddScoped<IBloodCreditLedgerStore, BloodCreditLedgerStore>();
        services.AddScoped<IBloodCreditReservationStore, BloodCreditReservationStore>();
        services.AddSingleton<IClock>(new Clock(Start));
        services.AddBloodMoneyChallenges(Timing);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var scope = provider.CreateAsyncScope();
        var created = await scope.ServiceProvider.GetRequiredService<CreateBloodMoneyChallengeUseCase>().ExecuteAsync(Request(players), Ct);
        var active = await scope.ServiceProvider.GetRequiredService<AcceptBloodMoneyChallengeUseCase>().ExecuteAsync(created.ChallengeId, players[1], Ct);
        Assert.Equal(BloodMoneyChallengeStatus.Active, active.Status);
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<DeclineBloodMoneyChallengeUseCase>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<CancelBloodMoneyChallengeUseCase>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ExpirePendingBloodMoneyChallengeUseCase>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<GetBloodMoneyChallengeUseCase>());
        await AssertCoherent(active.ChallengeId, players);
    }

    private async Task<PlayerId[]> Roster(int count, long balance = 100)
    {
        await using var db = fixture.CreateDbContext(); var players = new List<PlayerId>();
        for (var index = 0; index < count; index++)
        {
            var player = await PlayerSeeding.CreatePlayerAsync(db, "Challenge", Start); players.Add(player);
            await new IssueBloodCreditsUseCase(new BloodCreditLedgerStore(db), new EfUnitOfWork(db), new Clock(Start))
                .ExecuteAsync(new(BloodCreditTransactionId.New(), player, balance, "test-grant"), Ct);
            if (index > 0) await new FriendshipStore(db).AddFriendshipAsync(Friendship.Between(players[0], player, Start), Ct);
        }
        await db.SaveChangesAsync(); return players.ToArray();
    }
    private static CreateBloodMoneyChallengeRequest Request(PlayerId[] players) => new(players[0], players.Skip(1).ToArray(), 30, "classic", 1, Guid.NewGuid());
    private async Task<BloodMoneyChallengeView> CreateFresh(CreateBloodMoneyChallengeRequest request)
    { await using var db = fixture.CreateDbContext(); return await Create(db, request); }
    private static BloodCreditReservationMutator Mutator(Level5V2DbContext db, DateTimeOffset now)
        => new(new BloodCreditLedgerStore(db), new BloodCreditReservationStore(db), new Clock(now));
    private static Task<BloodMoneyChallengeView> Create(Level5V2DbContext db, CreateBloodMoneyChallengeRequest request, IUnitOfWork? uow = null, IBloodMoneyChallengeStore? store = null)
        => new CreateBloodMoneyChallengeUseCase(store ?? new BloodMoneyChallengeStore(db), new FriendshipStore(db), Mutator(db, Start), uow ?? new EfUnitOfWork(db), new Clock(Start), Timing).ExecuteAsync(request, Ct);
    private static Task<BloodMoneyChallengeView> Command(Level5V2DbContext db, BloodMoneyChallengeId id, PlayerId actor, string action, DateTimeOffset now, IUnitOfWork? uow = null)
    {
        var lifecycle = new BloodMoneyChallengeLifecycle(new BloodMoneyChallengeStore(db), new BloodCreditReservationStore(db), Mutator(db, now), uow ?? new EfUnitOfWork(db), new Clock(now), Timing);
        return action switch
        {
            "accept" => new AcceptBloodMoneyChallengeUseCase(lifecycle).ExecuteAsync(id, actor, Ct),
            "decline" => new DeclineBloodMoneyChallengeUseCase(lifecycle).ExecuteAsync(id, actor, Ct),
            "cancel" => new CancelBloodMoneyChallengeUseCase(lifecycle).ExecuteAsync(id, actor, Ct),
            "expire" => new ExpirePendingBloodMoneyChallengeUseCase(lifecycle).ExecuteAsync(id, Ct),
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
    }

    private async Task AssertCoherent(BloodMoneyChallengeId id, PlayerId[] players, long initialBalance = 100)
    {
        await using var db = fixture.CreateDbContext(); var challenge = (await new BloodMoneyChallengeStore(db).FindAsync(id, Ct))!;
        var reservations = await db.Set<BloodCreditReservationRow>().Where(r => r.ChallengeId == id.Value).ToListAsync();
        foreach (var player in players)
        {
            var participant = challenge.Participant(player); var hold = reservations.SingleOrDefault(r => r.PlayerId == player.Value);
            if (participant.Status == BloodMoneyParticipantStatus.Accepted) Assert.NotNull(hold);
            if (hold is not null) Assert.Equal(challenge.StakePerParticipant, hold.Amount);
            var terminal = challenge.Status is BloodMoneyChallengeStatus.Cancelled or BloodMoneyChallengeStatus.Declined or BloodMoneyChallengeStatus.Expired;
            if (terminal && hold is not null) Assert.Equal("Released", hold.Status);
            if (!terminal && hold is not null)
            { Assert.Equal(BloodMoneyParticipantStatus.Accepted, participant.Status); Assert.Equal("Reserved", hold.Status); }
            if (challenge.Status == BloodMoneyChallengeStatus.Active)
            { Assert.Equal(BloodMoneyParticipantStatus.Accepted, participant.Status); Assert.NotNull(hold); Assert.Equal("Reserved", hold.Status); }
            var transactions = await db.Set<BloodCreditTransactionRow>().Include(r => r.Postings).Where(r => r.SubjectPlayerId == player.Value).ToListAsync();
            var account = (await new BloodCreditLedgerStore(db).FindAccountAsync(player, Ct))!;
            Assert.Equal(hold?.Status == "Reserved" ? initialBalance - challenge.StakePerParticipant : initialBalance, account.AvailableBalance);
            Assert.Equal(account.AvailableBalance, transactions.Sum(r => r.PlayerDelta));
            Assert.All(transactions, r => { Assert.Equal(2, r.Postings.Count); Assert.Equal(0, r.Postings.Sum(p => p.Amount)); });
            Assert.Equal(hold is null ? 0 : 1, transactions.Count(r => r.Kind == "Reserve"));
            Assert.Equal(hold?.Status == "Released" ? 1 : 0, transactions.Count(r => r.Kind == "Release"));
            if (hold is not null)
            {
                Assert.Equal(-hold.Amount, Assert.Single(transactions, t => t.Id == hold.ReserveTransactionId).PlayerDelta);
                if (hold.Status == "Released") Assert.Equal(hold.Amount, Assert.Single(transactions, t => t.Id == hold.ReleaseTransactionId).PlayerDelta);
            }
        }
    }

    private sealed class Clock(DateTimeOffset now) : IClock { public DateTimeOffset UtcNow => now; }
    private sealed class CountingUnitOfWork(Level5V2DbContext db, Func<Task>? beforeSave = null) : IUnitOfWork
    {
        public int Saves { get; private set; }
        public async Task SaveChangesAsync(CancellationToken ct)
        { Saves++; if (beforeSave is not null) await beforeSave(); await new EfUnitOfWork(db).SaveChangesAsync(ct); }
    }
    private sealed class PausedCreateLookup(IBloodMoneyChallengeStore inner) : IBloodMoneyChallengeStore
    {
        public TaskCompletionSource Read { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<BloodMoneyChallenge?> FindAsync(BloodMoneyChallengeId id, CancellationToken ct) => inner.FindAsync(id, ct);
        public async Task<BloodMoneyChallenge?> FindByCreateRequestAsync(PlayerId creator, Guid requestId, CancellationToken ct)
        {
            var challenge = await inner.FindByCreateRequestAsync(creator, requestId, ct);
            Read.TrySetResult();
            await Resume.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            return challenge;
        }
        public void Add(BloodMoneyChallenge challenge) => inner.Add(challenge);
        public void StageUpdate(BloodMoneyChallenge challenge, long expectedRevision) => inner.StageUpdate(challenge, expectedRevision);
    }
    private sealed class SaveBarrier
    {
        private int _arrivals;
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task Arrive() { if (Interlocked.Increment(ref _arrivals) == 2) _ready.TrySetResult(); await _ready.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
    }
    private sealed class FailAfterCommand(string target) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData data, DbDataReader result, CancellationToken ct = default)
        {
            if (command.CommandText.Contains(target, StringComparison.Ordinal))
            { Fired = true; await result.DisposeAsync(); throw new InvalidOperationException("Injected failure after command execution before commit."); }
            return result;
        }
    }
}
