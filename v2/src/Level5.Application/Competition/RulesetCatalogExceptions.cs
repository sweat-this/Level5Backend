using Level5.Application.Common;

namespace Level5.Application.Competition;

/// <summary>Thrown by <see cref="Level5.Application.Abstractions.IRulesetCatalog"/> when the requested ruleset id is not recognized.</summary>
public sealed class UnknownRulesetException : AppException
{
    public override string Code => "unknown_ruleset";

    public UnknownRulesetException(string message) : base(message)
    {
    }
}

/// <summary>
/// Thrown by <see cref="Level5.Application.Abstractions.IRulesetCatalog"/> when the requested (or
/// current) ruleset version is outside the range this build can still play - see
/// v2/docs/competition-protocol/fixtures/09-unsupported-ruleset-version.json.
/// </summary>
public sealed class RulesetVersionUnsupportedException : AppException
{
    public override string Code => "ruleset_version_unsupported";

    public RulesetVersionUnsupportedException(string message) : base(message)
    {
    }
}
