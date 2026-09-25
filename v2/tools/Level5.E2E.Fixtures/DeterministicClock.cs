using Level5.Application.Abstractions;

namespace Level5.E2E.Fixtures;

/// <summary>
/// Replaces Infrastructure's real <c>SystemClock</c> for the duration of a fixture-seeding run, so
/// every timestamp a scenario produces (CreatedAt/UpdatedAt/CompletedAt/etc.) is derived from one
/// fixed epoch rather than wall-clock time - the same fixed clock, driven forward by explicit,
/// scenario-scripted steps, is what makes "reset + reseed the same scenario" produce byte-identical
/// state instead of merely structurally-identical state.
/// </summary>
public sealed class DeterministicClock : IClock
{
    public static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public DateTimeOffset UtcNow { get; private set; } = Epoch;

    /// <summary>Moves the clock forward by a fixed amount before the next scripted step, so successive steps in a scenario get distinct, strictly increasing timestamps.</summary>
    public void Advance(TimeSpan by) => UtcNow += by;
}
