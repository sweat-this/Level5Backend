using Level5.Application.BloodMoney;
using Level5.Domain.BloodMoney;
using Level5.Domain.Ids;

namespace Level5.Application.Tests.BloodMoney;

public sealed class BloodMoneyChatTests
{
    private static readonly BloodMoneyChatRatePolicy Rate = new(5, TimeSpan.FromSeconds(10), 30, TimeSpan.FromMinutes(1));

    [Fact]
    public async Task Trusted_actor_key_and_normalized_body_reach_the_narrow_persistence_port()
    {
        var store = new Store(); var useCase = new SendBloodMoneyChallengeMessageUseCase(store, Rate);
        var request = new SendBloodMoneyChallengeMessageRequest(new(Guid.NewGuid()), PlayerId.New(), Guid.NewGuid(), " e\u0301\r\nx ");
        var result = await useCase.ExecuteAsync(request, CancellationToken.None);
        Assert.Equal(request with { Body = "é\nx" }, store.Request);
        Assert.Same(Rate, store.Policy); Assert.Same(store.Result, result);
    }

    [Fact]
    public async Task Invalid_text_and_empty_key_never_reach_persistence()
    {
        var store = new Store(); var useCase = new SendBloodMoneyChallengeMessageUseCase(store, Rate);
        var request = new SendBloodMoneyChallengeMessageRequest(new(Guid.NewGuid()), PlayerId.New(), Guid.NewGuid(), "hello");
        foreach (var invalid in new[] { request with { ClientMessageId = Guid.Empty }, request with { Body = "\ud800" }, request with { Body = "\t" } })
            Assert.Equal("invalid_chat_message", (await Assert.ThrowsAsync<BloodMoneyChatException>(() => useCase.ExecuteAsync(invalid, CancellationToken.None))).Code);
        Assert.Null(store.Request);
    }

    [Fact]
    public void Suppressed_projection_never_exposes_accepted_body()
    {
        var message = new BloodMoneyChatMessage(Guid.NewGuid(), new(Guid.NewGuid()), 1, PlayerId.New(), Guid.NewGuid(), "protected", DateTimeOffset.UtcNow, BloodMoneyChatVisibility.Suppressed);
        Assert.Null(BloodMoneyChatMessageView.From(message).Body);
    }

    [Fact]
    public void Rate_policy_requires_positive_limits_and_ordered_windows()
    {
        Assert.Throws<ArgumentException>(() => new BloodMoneyChatRatePolicy(0, TimeSpan.FromSeconds(10), 30, TimeSpan.FromMinutes(1)));
        Assert.Throws<ArgumentException>(() => new BloodMoneyChatRatePolicy(5, TimeSpan.Zero, 30, TimeSpan.FromMinutes(1)));
        Assert.Throws<ArgumentException>(() => new BloodMoneyChatRatePolicy(5, TimeSpan.FromMinutes(1), 30, TimeSpan.FromSeconds(10)));
    }

    private sealed class Store : IBloodMoneyChatStore
    {
        public Task<BloodMoneyChatPage> ListAsync(ListBloodMoneyChallengeMessagesRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BloodMoneyChatReportAcceptance> ReportAsync(ReportBloodMoneyChatMessageRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public SendBloodMoneyChallengeMessageRequest? Request { get; private set; }
        public BloodMoneyChatRatePolicy? Policy { get; private set; }
        public SendBloodMoneyChallengeMessageResult Result { get; } = new(new(Guid.NewGuid(), new(Guid.NewGuid()), 1,
            PlayerId.New(), Guid.NewGuid(), "ok", DateTimeOffset.UtcNow, BloodMoneyChatVisibility.Visible), true);
        public Task<SendBloodMoneyChallengeMessageResult> SendAsync(SendBloodMoneyChallengeMessageRequest request, BloodMoneyChatRatePolicy ratePolicy, CancellationToken cancellationToken)
        { Request = request; Policy = ratePolicy; return Task.FromResult(Result); }
        public Task<BloodMoneyChatParticipantState> GetParticipantStateAsync(BloodMoneyChallengeId challengeId, PlayerId actor, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BloodMoneyChatParticipantState> AdvanceReadAsync(BloodMoneyChallengeId challengeId, PlayerId actor, long sequence, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BloodMoneyChatParticipantState> SetNotificationsMutedAsync(BloodMoneyChallengeId challengeId, PlayerId actor, bool muted, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
