namespace Level5.Domain.Competition;

/// <summary>
/// How much of an opponent's in-progress/completed attempt a participant may see (Competition
/// Protocol V1 section 11). <see cref="SealedAttempt"/> is enforced today by
/// <see cref="VersusSeries"/>'s projection; <see cref="OpenTarget"/>'s issuance gating and
/// partial-reveal projection are issue #10/#11's responsibility - this value is persisted as
/// part of a series' frozen rules starting with issue #9 but not yet enforced for that policy.
/// </summary>
public enum InformationPolicy
{
    SealedAttempt,
    OpenTarget
}
