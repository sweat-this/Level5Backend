namespace Level5.Application.Abstractions;

/// <summary>Configuration for the automatic pending-challenge expiry sweep, kept behind a port so Application depends only on a value, not on Infrastructure's configuration binding (mirrors <see cref="IAuthSessionPolicy"/>).</summary>
public interface IChallengeExpiryPolicy
{
    /// <summary>How long a challenge may sit in <c>PendingAcceptance</c> before it becomes eligible to expire.</summary>
    TimeSpan PendingAcceptanceTimeout { get; }

    /// <summary>How often the background sweep runs.</summary>
    TimeSpan SweepInterval { get; }

    /// <summary>Upper bound on how many stale challenges one sweep tick will expire, so a single tick can never scan or lock an unbounded number of rows.</summary>
    int SweepBatchSize { get; }
}
