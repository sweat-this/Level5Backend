using Level5.Domain.Identity;

namespace Level5.Api.Security;

/// <summary>Maps an optional allowlisted request hint to coarse, non-authoritative metadata.</summary>
public interface IClientKindAccessor
{
    ClientKind GetClientKind();
}
