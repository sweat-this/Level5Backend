namespace Level5.Domain.Competition;

/// <summary>
/// The wire-contract version Unity and the Backend must agree on to interoperate (Competition
/// Protocol V1 - see v2/docs/competition-protocol/README.md section 6). This is a genuinely
/// independent axis from the Backend's own JSONB persistence schema version - it changes only
/// when the meaning of a command/query or a wire-required field changes, never when the
/// Backend's own storage shape changes.
/// </summary>
public static class CompetitionProtocol
{
    public const int CurrentVersion = 1;
}
