using Level5.Domain.Ids;

namespace Level5.Api.Security;

/// <summary>Parses the signed JWT <c>sid</c> claim only; it never performs persistence work.</summary>
public interface ICurrentAuthSessionAccessor
{
    AuthSessionId? GetCurrentAuthSessionId();
}
