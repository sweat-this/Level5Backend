using Level5.Application.Abstractions;
using Level5.Application.Competition;
using Level5.Application.Social;
using Level5.Domain.Competition;
using Level5.Domain.Identity;
using Level5.Domain.Ids;
using Level5.Domain.Players;
using Microsoft.Extensions.DependencyInjection;

namespace Level5.E2E.Fixtures;

/// <summary>
/// Everything a scenario builder needs, resolved once from the real DI container
/// (<c>AddLevel5Infrastructure</c>) plus the deterministic clock substituted for it. Every method
/// here goes through a real Application use case or a real Infrastructure store - never raw SQL -
/// so seeded state is exactly what the equivalent real client action would have produced.
/// </summary>
public sealed class FixtureContext(IServiceProvider services, DeterministicClock clock)
{
    public DeterministicClock Clock { get; } = clock;

    private readonly Dictionary<string, PlayerId> _playerIdsByLabel = [];

    public PlayerId PlayerId(FixtureIdentity identity) => _playerIdsByLabel[identity.Label];

    public T Resolve<T>() where T : notnull => services.GetRequiredService<T>();

    /// <summary>Registers all four fixture accounts + player profiles (spec: every scenario includes the full baseline roster, even scenarios that only use a subset of them).</summary>
    public async Task CreateBaselineRosterAsync(CancellationToken cancellationToken)
    {
        var passwordHasher = Resolve<IPasswordHasher>();
        var accountStore = Resolve<IAccountStore>();
        var profileStore = Resolve<IPlayerProfileStore>();
        var unitOfWork = Resolve<IUnitOfWork>();

        foreach (var identity in FixtureIdentities.All)
        {
            var account = Account.Register(Username.Create(identity.Username), passwordHasher.Hash(FixtureIdentities.Password), Clock.UtcNow);
            await accountStore.AddAsync(account, cancellationToken);

            var profile = PlayerProfile.Create(account.Id, identity.DisplayName, PlayerTag.Create(identity.Tag), Clock.UtcNow);
            await profileStore.AddAsync(profile, cancellationToken);

            // AccountStore/PlayerProfileStore.AddAsync only stage the insert (EF change
            // tracking) - nothing is actually committed until SaveChangesAsync, exactly like
            // every real use case that calls these stores (e.g. RegisterAccountUseCase).
            await unitOfWork.SaveChangesAsync(cancellationToken);

            _playerIdsByLabel[identity.Label] = profile.Id;
            Clock.Advance(TimeSpan.FromSeconds(1));
        }
    }

    /// <summary>Sends and accepts a friend request between two already-created fixture players, via the real use cases.</summary>
    public async Task<FriendRequestId> BefriendAsync(FixtureIdentity from, FixtureIdentity to, CancellationToken cancellationToken)
    {
        var requestId = await SendFriendRequestAsync(from, to, cancellationToken);

        var acceptFriendRequest = Resolve<AcceptFriendRequestUseCase>();
        await acceptFriendRequest.ExecuteAsync(new AcceptFriendRequestRequest(PlayerId(to), requestId), cancellationToken);
        Clock.Advance(TimeSpan.FromSeconds(1));

        return requestId;
    }

    /// <summary>Sends (but does not resolve) a friend request between two fixture players.</summary>
    public async Task<FriendRequestId> SendFriendRequestAsync(FixtureIdentity from, FixtureIdentity to, CancellationToken cancellationToken)
    {
        var sendFriendRequest = Resolve<SendFriendRequestUseCase>();
        var result = await sendFriendRequest.ExecuteAsync(new SendFriendRequestRequest(PlayerId(from), PlayerId(to)), cancellationToken);
        Clock.Advance(TimeSpan.FromSeconds(1));
        return result.Id;
    }

    /// <summary>Creates a challenge from one fixture player to another via the real use case, using the "most-points" ruleset (the one Unity can actually resolve/launch) at best-of-1 unless overridden.</summary>
    public async Task<SeriesView> CreateChallengeAsync(FixtureIdentity challenger, FixtureIdentity opponent, CancellationToken cancellationToken, int totalGames = 1)
    {
        var createChallenge = Resolve<CreateChallengeUseCase>();
        var view = await createChallenge.ExecuteAsync(
            new CreateChallengeRequest(PlayerId(challenger), PlayerId(opponent), "most-points", RulesetVersion: null, totalGames, InformationPolicy: null, Guid.NewGuid()),
            cancellationToken);
        Clock.Advance(TimeSpan.FromSeconds(1));
        return view;
    }
}
