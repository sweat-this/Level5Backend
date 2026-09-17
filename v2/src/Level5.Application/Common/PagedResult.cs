namespace Level5.Application.Common;

/// <summary>
/// A bounded page of <typeparamref name="T"/> plus an opaque continuation token. <see cref="NextCursor"/>
/// is <c>null</c> when the page reached the end of the result set - callers pass it back verbatim
/// (as the next request's cursor) to fetch the following page, and must not attempt to parse it.
/// </summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, string? NextCursor);
