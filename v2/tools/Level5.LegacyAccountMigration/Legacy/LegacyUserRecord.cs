namespace Level5.LegacyAccountMigration.Legacy;

/// <summary>
/// Plain shape of one V1 <c>users</c> row, read via raw SQL - deliberately NOT
/// <c>Level5Backend.Models.User</c> (this tool must never reference the legacy assembly).
/// FirstName/LastName/IpAddress/SignupDate/LastLogin are deliberately omitted - nothing in this
/// migration's scope needs them, and keeping the DTO minimal avoids incidentally widening what the
/// tool touches. Password is included because classification/import needs it, but nothing in this
/// tool ever logs or writes it anywhere except directly into Account.PasswordHash (recognized-hash
/// path) or through IPasswordHasher.Hash (accept-legacy-plaintext path).
/// </summary>
public sealed record LegacyUserRecord(
    int UserId,
    string Username,
    string Password,
    string? Email,
    int? IsDev);
