using Level5.Application.Abstractions;
using Level5.Application.Social;
using Level5.Application.Tests.Fakes;
using Level5.Domain.Identity;
using Level5.Domain.Ids;
using Level5.Domain.Players;
using Xunit;

namespace Level5.Application.Tests.Social;

public class ListFriendRequestsUseCaseTests
{
    private readonly InMemoryFriendshipStore _friendships = new();
    private readonly InMemoryPlayerProfileStore _profiles = new();
    private readonly FakeClock _clock = new();
    private readonly SendFriendRequestUseCase _send;
    private readonly ListIncomingFriendRequestsUseCase _listIncoming;
    private readonly ListOutgoingFriendRequestsUseCase _listOutgoing;

    public ListFriendRequestsUseCaseTests()
    {
        _send = new SendFriendRequestUseCase(_friendships, _profiles, new NoOpUnitOfWork(), _clock);
        _listIncoming = new ListIncomingFriendRequestsUseCase(_friendships, _profiles);
        _listOutgoing = new ListOutgoingFriendRequestsUseCase(_friendships, _profiles);
    }

    private async Task<PlayerId> SeedPlayerAsync(string tag)
    {
        var profile = PlayerProfile.Create(AccountId.New(), tag, PlayerTag.Create($"{tag}#0001"), _clock.UtcNow);
        await _profiles.AddAsync(profile, CancellationToken.None);
        return profile.Id;
    }

    [Fact]
    public async Task Incoming_only_returns_pending_requests_where_the_current_player_is_the_recipient()
    {
        var a = await SeedPlayerAsync("ListA");
        var b = await SeedPlayerAsync("ListB");
        var c = await SeedPlayerAsync("ListC");
        var aToB = await _send.ExecuteAsync(new SendFriendRequestRequest(a, b), CancellationToken.None);

        var bIncoming = await _listIncoming.ExecuteAsync(b, CancellationToken.None);
        var aIncoming = await _listIncoming.ExecuteAsync(a, CancellationToken.None);
        var cIncoming = await _listIncoming.ExecuteAsync(c, CancellationToken.None);

        var only = Assert.Single(bIncoming);
        Assert.Equal(aToB.Id, only.Id);
        Assert.Equal(a, only.OtherPlayer.PlayerId);
        Assert.Equal("ListA", only.OtherPlayer.DisplayName);
        Assert.Empty(aIncoming);
        Assert.Empty(cIncoming);
    }

    [Fact]
    public async Task Outgoing_only_returns_pending_requests_where_the_current_player_is_the_sender()
    {
        var a = await SeedPlayerAsync("ListD");
        var b = await SeedPlayerAsync("ListE");
        var c = await SeedPlayerAsync("ListF");
        var aToB = await _send.ExecuteAsync(new SendFriendRequestRequest(a, b), CancellationToken.None);

        var aOutgoing = await _listOutgoing.ExecuteAsync(a, CancellationToken.None);
        var bOutgoing = await _listOutgoing.ExecuteAsync(b, CancellationToken.None);
        var cOutgoing = await _listOutgoing.ExecuteAsync(c, CancellationToken.None);

        var only = Assert.Single(aOutgoing);
        Assert.Equal(aToB.Id, only.Id);
        Assert.Equal(b, only.OtherPlayer.PlayerId);
        Assert.Equal("ListE", only.OtherPlayer.DisplayName);
        Assert.Empty(bOutgoing);
        Assert.Empty(cOutgoing);
    }

    [Fact]
    public async Task A_third_player_cannot_see_another_pairs_requests_through_either_list()
    {
        var a = await SeedPlayerAsync("ListG");
        var b = await SeedPlayerAsync("ListH");
        var outsider = await SeedPlayerAsync("ListI");
        await _send.ExecuteAsync(new SendFriendRequestRequest(a, b), CancellationToken.None);

        Assert.Empty(await _listIncoming.ExecuteAsync(outsider, CancellationToken.None));
        Assert.Empty(await _listOutgoing.ExecuteAsync(outsider, CancellationToken.None));
    }

    [Fact]
    public async Task Multiple_incoming_requests_resolve_their_senders_in_a_single_batched_profile_lookup()
    {
        var counting = new CountingPlayerProfileStore(_profiles);
        var listIncoming = new ListIncomingFriendRequestsUseCase(_friendships, counting);
        var recipient = await SeedPlayerAsync("ListJ");
        var senderOne = await SeedPlayerAsync("ListK");
        var senderTwo = await SeedPlayerAsync("ListL");
        await _send.ExecuteAsync(new SendFriendRequestRequest(senderOne, recipient), CancellationToken.None);
        await _send.ExecuteAsync(new SendFriendRequestRequest(senderTwo, recipient), CancellationToken.None);

        var incoming = await listIncoming.ExecuteAsync(recipient, CancellationToken.None);

        Assert.Equal(2, incoming.Count);
        Assert.Equal(1, counting.FindByIdsCallCount);
    }

    [Fact]
    public async Task An_empty_request_list_never_queries_player_profiles()
    {
        var counting = new CountingPlayerProfileStore(_profiles);
        var listIncoming = new ListIncomingFriendRequestsUseCase(_friendships, counting);
        var lonely = await SeedPlayerAsync("ListM");

        var incoming = await listIncoming.ExecuteAsync(lonely, CancellationToken.None);

        Assert.Empty(incoming);
        Assert.Equal(0, counting.FindByIdsCallCount);
    }

    [Fact]
    public async Task A_sender_profile_missing_despite_a_pending_request_is_dropped_rather_than_failing_the_whole_list()
    {
        var recipient = await SeedPlayerAsync("ListN");
        var ghostSender = PlayerId.New(); // never registered - simulates an FK-backed inconsistency
        var visibleSender = await SeedPlayerAsync("ListO");
        await _send.ExecuteAsync(new SendFriendRequestRequest(ghostSender, recipient), CancellationToken.None);
        var visibleRequest = await _send.ExecuteAsync(new SendFriendRequestRequest(visibleSender, recipient), CancellationToken.None);

        var incoming = await _listIncoming.ExecuteAsync(recipient, CancellationToken.None);

        // The orphaned row is silently omitted; the unaffected row is still returned.
        var only = Assert.Single(incoming);
        Assert.Equal(visibleRequest.Id, only.Id);
        Assert.Equal(visibleSender, only.OtherPlayer.PlayerId);
    }

    /// <summary>Wraps a real store to count <see cref="IPlayerProfileStore.FindByIdsAsync"/> calls, proving enrichment batches instead of querying per row.</summary>
    private sealed class CountingPlayerProfileStore(IPlayerProfileStore inner) : IPlayerProfileStore
    {
        public int FindByIdsCallCount { get; private set; }

        public Task<PlayerProfile?> FindByIdAsync(PlayerId id, CancellationToken cancellationToken) => inner.FindByIdAsync(id, cancellationToken);

        public Task<IReadOnlyList<PlayerProfile>> FindByIdsAsync(IReadOnlyCollection<PlayerId> ids, CancellationToken cancellationToken)
        {
            FindByIdsCallCount++;
            return inner.FindByIdsAsync(ids, cancellationToken);
        }

        public Task<PlayerProfile?> FindByAccountIdAsync(AccountId accountId, CancellationToken cancellationToken) => inner.FindByAccountIdAsync(accountId, cancellationToken);

        public Task<PlayerProfile?> FindByTagAsync(PlayerTag tag, CancellationToken cancellationToken) => inner.FindByTagAsync(tag, cancellationToken);

        public Task<bool> TagExistsAsync(PlayerTag tag, CancellationToken cancellationToken) => inner.TagExistsAsync(tag, cancellationToken);

        public Task AddAsync(PlayerProfile profile, CancellationToken cancellationToken) => inner.AddAsync(profile, cancellationToken);

        public Task UpdateAsync(PlayerProfile profile, CancellationToken cancellationToken) => inner.UpdateAsync(profile, cancellationToken);
    }
}
