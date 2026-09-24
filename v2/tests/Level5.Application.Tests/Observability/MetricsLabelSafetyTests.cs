using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Level5.Application.Abstractions;
using Level5.Application.Competition;
using Level5.Application.Identity;
using Level5.Application.Observability;
using Level5.Application.Tests.Competition;
using Level5.Application.Tests.Fakes;
using Level5.Domain.Competition;
using Level5.Domain.Identity;
using Level5.Domain.Ids;
using Level5.Domain.Social;
using Xunit;

namespace Level5.Application.Tests.Observability;

/// <summary>
/// Issue #22 requires that the small set of service-level counters never carry sensitive or
/// high-cardinality tag values (account/player/series/attempt ids, emails, tokens, raw exception
/// text). Rather than trusting a code-review read of each call site, this subscribes a real
/// <see cref="MeterListener"/> to <see cref="ApplicationMetrics.MeterName"/> - exactly how
/// OpenTelemetry's own <c>MeterProvider</c> observes it - and drives every instrumented failure
/// path, then asserts every tag key/value pair recorded falls inside a small fixed vocabulary. A
/// value outside that vocabulary (a username, a GUID, ...) fails the test immediately.
/// </summary>
public sealed class MetricsLabelSafetyTests : IDisposable
{
    private static readonly HashSet<string> AllowedTagKeys =
        [ApplicationMetrics.ReasonCategoryTag, ApplicationMetrics.OutcomeTag, ApplicationMetrics.OperationTag];

    private static readonly HashSet<string> AllowedTagValues =
    [
        // auth.login.failure / reason_category
        "bad_username_format", "unknown_account", "bad_password", "account_disabled",
        // auth.refresh.outcome / outcome
        "success", "unknown", "expired", "revoked", "account_inactive", "replay_conflict",
        // series.concurrency.conflict / operation
        "start_attempt", "complete_attempt", "accept_challenge", "decline_challenge", "cancel_challenge",
        // challenge.create.replay_or_conflict / outcome
        "created", "idempotent_replay", "conflict",
        // attempt.complete.outcome / outcome
        "conflicting_result",
    ];

    // A ConcurrentQueue, not a List: this Meter is a process-wide static, so xunit running other
    // test classes' use cases in parallel on other threads can enqueue measurements concurrently
    // with this test's own enumeration - a plain List's enumerator throws
    // InvalidOperationException the instant that happens.
    private readonly ConcurrentQueue<(string Instrument, KeyValuePair<string, object?>[] Tags)> _measurements = new();
    private readonly MeterListener _listener;

    public MetricsLabelSafetyTests()
    {
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == ApplicationMetrics.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            _measurements.Enqueue((instrument.Name, tags.ToArray())));
        _listener.Start();
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public async Task Every_recorded_measurement_uses_only_the_documented_low_cardinality_tag_vocabulary()
    {
        await DriveEveryInstrumentedFailurePathAsync();

        var measurements = _measurements.ToArray();
        Assert.NotEmpty(measurements);

        foreach (var (instrument, tags) in measurements)
        {
            var tag = Assert.Single(tags);
            Assert.Contains(tag.Key, AllowedTagKeys);
            Assert.Contains(tag.Value, AllowedTagValues);
            Assert.True(instrument.Length < 64, $"Instrument name '{instrument}' looks unexpectedly long for a fixed metric name.");
        }
    }

    [Fact]
    public async Task No_recorded_tag_value_equals_an_identifier_email_or_token_used_by_the_driving_scenarios()
    {
        // A stricter, sensitive-data-specific check alongside the vocabulary allow-list above:
        // even if a future counter's vocabulary grew, none of the real secrets/identifiers this
        // test happens to generate while driving the scenarios should ever show up as a tag value.
        var sensitiveValues = await DriveEveryInstrumentedFailurePathAsync();

        foreach (var (_, tags) in _measurements.ToArray())
        {
            foreach (var tag in tags)
            {
                var value = tag.Value?.ToString() ?? string.Empty;
                Assert.DoesNotContain(value, sensitiveValues);
                Assert.False(Guid.TryParse(value, out _), $"Tag value '{value}' looks like a raw identifier (parses as a GUID).");
            }
        }
    }

    /// <summary>Exercises every metric-emitting branch across the five instrumented use cases and returns the real (sensitive) values used, for the negative-assertion test above.</summary>
    private static async Task<List<string>> DriveEveryInstrumentedFailurePathAsync()
    {
        var sensitiveValues = new List<string>();

        // auth.login.failure: all four reason categories.
        var accounts = new InMemoryAccountStore();
        var profiles = new InMemoryPlayerProfileStore();
        var sessions = new InMemoryAuthSessionStore();
        var hasher = new FakePasswordHasher();
        var clock = new FakeClock();
        var register = new RegisterAccountUseCase(
            accounts, profiles, sessions, hasher, new FakePasswordPolicy(),
            new FakeRefreshTokenGenerator(), new FakeAuthSessionPolicy(), new FakeTokenIssuer(), new NoOpUnitOfWork(), clock);
        var login = new LoginUseCase(
            accounts, profiles, sessions, hasher, new FakeRefreshTokenGenerator(), new FakeAuthSessionPolicy(),
            new FakeTokenIssuer(), new NoOpUnitOfWork(), clock);

        var registered = await register.ExecuteAsync(new RegisterAccountRequest("metricsuser", "P@ssw0rd!", "MetricsUser"), CancellationToken.None);
        sensitiveValues.Add("metricsuser");
        sensitiveValues.Add(registered.AccountId.Value.ToString());
        sensitiveValues.Add(registered.PlayerId.Value.ToString());
        sensitiveValues.Add(registered.RefreshToken);

        await Assert.ThrowsAsync<InvalidCredentialsException>(() => login.ExecuteAsync(new LoginRequest("###", "whatever"), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidCredentialsException>(() => login.ExecuteAsync(new LoginRequest("nobody-registered", "whatever"), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidCredentialsException>(() => login.ExecuteAsync(new LoginRequest("metricsuser", "wrong-password"), CancellationToken.None));

        var disabledLoginAccount = await register.ExecuteAsync(new RegisterAccountRequest("metricsdisabledlogin", "P@ssw0rd!", "MetricsDisabledLogin"), CancellationToken.None);
        sensitiveValues.Add("metricsdisabledlogin");
        sensitiveValues.Add(disabledLoginAccount.AccountId.Value.ToString());
        sensitiveValues.Add(disabledLoginAccount.PlayerId.Value.ToString());
        sensitiveValues.Add(disabledLoginAccount.RefreshToken);
        await DisableAccountAsync(accounts, disabledLoginAccount.AccountId);
        await Assert.ThrowsAsync<InvalidCredentialsException>(() => login.ExecuteAsync(new LoginRequest("metricsdisabledlogin", "P@ssw0rd!"), CancellationToken.None));

        // auth.refresh.outcome: unknown, success (via the session created above), expired, revoked, account_inactive, replay_conflict.
        var refresh = new RefreshSessionUseCase(sessions, accounts, profiles, new FakeRefreshTokenGenerator(), new FakeAuthSessionPolicy(), new FakeTokenIssuer(), clock);
        await Assert.ThrowsAsync<InvalidRefreshTokenException>(() => refresh.ExecuteAsync(new RefreshSessionRequest("not-a-real-token"), CancellationToken.None));
        var refreshed = await refresh.ExecuteAsync(new RefreshSessionRequest(registered.RefreshToken), CancellationToken.None);
        sensitiveValues.Add(refreshed.RefreshToken);

        // Isolated clock so advancing it past one session's expiry doesn't affect every other
        // scenario in this method, which all share the outer `clock`.
        var expiredClock = new FakeClock();
        var registerForExpiry = new RegisterAccountUseCase(
            accounts, profiles, sessions, hasher, new FakePasswordPolicy(),
            new FakeRefreshTokenGenerator(), new FakeAuthSessionPolicy(), new FakeTokenIssuer(), new NoOpUnitOfWork(), expiredClock);
        var refreshForExpiry = new RefreshSessionUseCase(
            sessions, accounts, profiles, new FakeRefreshTokenGenerator(), new FakeAuthSessionPolicy(), new FakeTokenIssuer(), expiredClock);
        var expiringAccount = await registerForExpiry.ExecuteAsync(new RegisterAccountRequest("metricsexpired", "P@ssw0rd!", "MetricsExpired"), CancellationToken.None);
        sensitiveValues.Add("metricsexpired");
        sensitiveValues.Add(expiringAccount.AccountId.Value.ToString());
        sensitiveValues.Add(expiringAccount.PlayerId.Value.ToString());
        sensitiveValues.Add(expiringAccount.RefreshToken);
        expiredClock.UtcNow = expiringAccount.RefreshTokenExpiresAt.AddSeconds(1);
        await Assert.ThrowsAsync<InvalidRefreshTokenException>(() => refreshForExpiry.ExecuteAsync(new RefreshSessionRequest(expiringAccount.RefreshToken), CancellationToken.None));

        var revokedAccount = await register.ExecuteAsync(new RegisterAccountRequest("metricsrevoked", "P@ssw0rd!", "MetricsRevoked"), CancellationToken.None);
        sensitiveValues.Add("metricsrevoked");
        sensitiveValues.Add(revokedAccount.AccountId.Value.ToString());
        sensitiveValues.Add(revokedAccount.PlayerId.Value.ToString());
        sensitiveValues.Add(revokedAccount.RefreshToken);
        await new LogoutUseCase(sessions, new FakeRefreshTokenGenerator(), clock).ExecuteAsync(new LogoutRequest(revokedAccount.RefreshToken), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidRefreshTokenException>(() => refresh.ExecuteAsync(new RefreshSessionRequest(revokedAccount.RefreshToken), CancellationToken.None));

        var inactiveAccount = await register.ExecuteAsync(new RegisterAccountRequest("metricsinactive", "P@ssw0rd!", "MetricsInactive"), CancellationToken.None);
        sensitiveValues.Add("metricsinactive");
        sensitiveValues.Add(inactiveAccount.AccountId.Value.ToString());
        sensitiveValues.Add(inactiveAccount.PlayerId.Value.ToString());
        sensitiveValues.Add(inactiveAccount.RefreshToken);
        await DisableAccountAsync(accounts, inactiveAccount.AccountId);
        await Assert.ThrowsAsync<InvalidRefreshTokenException>(() => refresh.ExecuteAsync(new RefreshSessionRequest(inactiveAccount.RefreshToken), CancellationToken.None));

        var replayAccount = await register.ExecuteAsync(new RegisterAccountRequest("metricsreplay", "P@ssw0rd!", "MetricsReplay"), CancellationToken.None);
        sensitiveValues.Add("metricsreplay");
        sensitiveValues.Add(replayAccount.AccountId.Value.ToString());
        sensitiveValues.Add(replayAccount.PlayerId.Value.ToString());
        sensitiveValues.Add(replayAccount.RefreshToken);
        var refreshAlwaysConflicting = new RefreshSessionUseCase(
            new AlwaysConflictingAuthSessionStore(sessions), accounts, profiles,
            new FakeRefreshTokenGenerator(), new FakeAuthSessionPolicy(), new FakeTokenIssuer(), clock);
        await Assert.ThrowsAsync<InvalidRefreshTokenException>(() => refreshAlwaysConflicting.ExecuteAsync(new RefreshSessionRequest(replayAccount.RefreshToken), CancellationToken.None));

        // challenge.create.replay_or_conflict: created, idempotent_replay, conflict.
        var seriesStore = new InMemoryVersusSeriesStore();
        var friendships = new InMemoryFriendshipStore();
        var catalog = new FakeRulesetCatalog();
        var challenger = registered.PlayerId;
        var opponent = Level5.Domain.Ids.PlayerId.New();
        sensitiveValues.Add(opponent.Value.ToString());
        await friendships.AddFriendshipAsync(Friendship.Between(challenger, opponent, clock.UtcNow), CancellationToken.None);
        var createChallenge = new CreateChallengeUseCase(seriesStore, friendships, catalog, clock);
        var clientRequestId = Guid.NewGuid();
        sensitiveValues.Add(clientRequestId.ToString());

        var created = await createChallenge.ExecuteAsync(
            new CreateChallengeRequest(challenger, opponent, "score-only", null, 3, null, clientRequestId), CancellationToken.None);
        sensitiveValues.Add(created.Id.Value.ToString());

        await createChallenge.ExecuteAsync(
            new CreateChallengeRequest(challenger, opponent, "score-only", null, 3, null, clientRequestId), CancellationToken.None);

        await Assert.ThrowsAsync<Level5.Application.Common.ConflictException>(() => createChallenge.ExecuteAsync(
            new CreateChallengeRequest(challenger, opponent, "score-only", null, 5, null, clientRequestId), CancellationToken.None));

        // series.concurrency.conflict (start_attempt) and attempt.complete.outcome (success, conflicting_result); series.concurrency.conflict (complete_attempt).
        await new AcceptChallengeUseCase(seriesStore, clock).ExecuteAsync(new AcceptChallengeRequest(opponent, created.Id), CancellationToken.None);

        var alwaysConflicting = new AlwaysConflictingVersusSeriesStore(seriesStore);
        await Assert.ThrowsAsync<Level5.Application.Common.ConflictException>(() =>
            new StartAttemptUseCase(alwaysConflicting, clock).ExecuteAsync(new StartAttemptRequest(challenger, created.Id, 1), CancellationToken.None));

        var started = await new StartAttemptUseCase(seriesStore, clock).ExecuteAsync(new StartAttemptRequest(challenger, created.Id, 1), CancellationToken.None);
        sensitiveValues.Add(started.AttemptId.Value.ToString());

        var completeAttempt = new CompleteAttemptUseCase(seriesStore, clock);
        await completeAttempt.ExecuteAsync(new CompleteAttemptRequest(challenger, created.Id, 1, started.AttemptId, AttemptResult.OfScore(50)), CancellationToken.None);
        await Assert.ThrowsAsync<ConflictingAttemptResultException>(() =>
            completeAttempt.ExecuteAsync(new CompleteAttemptRequest(challenger, created.Id, 1, started.AttemptId, AttemptResult.OfScore(999)), CancellationToken.None));

        await Assert.ThrowsAsync<Level5.Application.Common.ConflictException>(() =>
            new CompleteAttemptUseCase(new AlwaysConflictingVersusSeriesStore(seriesStore), clock)
                .ExecuteAsync(new CompleteAttemptRequest(challenger, created.Id, 1, started.AttemptId, AttemptResult.OfScore(50)), CancellationToken.None));

        return sensitiveValues;
    }

    private static async Task DisableAccountAsync(InMemoryAccountStore accounts, Level5.Domain.Ids.AccountId accountId)
    {
        var account = await accounts.FindByIdAsync(accountId, CancellationToken.None);
        var disabled = Account.Rehydrate(account!.Id, account.Username, account.Email, AccountStatus.Disabled, account.PasswordHash, account.CreatedAt);
        await accounts.UpdateAsync(disabled, CancellationToken.None);
    }

    /// <summary>Mirrors AlwaysConflictingVersusSeriesStore (Competition/ConcurrencyTests.cs): a store whose write always loses the race, to drive `auth.refresh.outcome{outcome=replay_conflict}` without depending on real thread timing.</summary>
    private sealed class AlwaysConflictingAuthSessionStore(IAuthSessionStore inner) : IAuthSessionStore
    {
        public Task<AuthSession?> FindByIdAsync(AuthSessionId id, CancellationToken cancellationToken) => inner.FindByIdAsync(id, cancellationToken);
        public Task<AuthSession?> FindByRefreshTokenHashAsync(string refreshTokenHash, CancellationToken cancellationToken) => inner.FindByRefreshTokenHashAsync(refreshTokenHash, cancellationToken);
        public Task AddAsync(AuthSession session, CancellationToken cancellationToken) => inner.AddAsync(session, cancellationToken);
        public Task<bool> TrySaveAsync(AuthSession session, long expectedRevision, CancellationToken cancellationToken) => Task.FromResult(false);
    }
}
