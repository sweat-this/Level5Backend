using System.ComponentModel.DataAnnotations;

namespace Level5.Infrastructure.Identity;

/// <summary>
/// Validated at startup via AddOptions().ValidateDataAnnotations().ValidateOnStart() - see
/// ServiceCollectionExtensions - so a missing or unusable JWT configuration fails the host
/// immediately rather than on the first login attempt.
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    // MinLength counts characters, which is a conservative proxy for HS256's 256-bit (32 byte)
    // minimum: any non-ASCII character encodes to more than one UTF8 byte, never fewer.
    [Required(ErrorMessage = "Jwt:Key is not configured. Set it via user-secrets (local dev) or the Jwt__Key environment variable (production).")]
    [MinLength(32, ErrorMessage = "Jwt:Key must be at least 32 characters, to be a valid HS256 signing key.")]
    public required string Key { get; set; }

    [Required(ErrorMessage = "Jwt:Issuer is not configured.")]
    public required string Issuer { get; set; }

    [Required(ErrorMessage = "Jwt:Audience is not configured.")]
    public required string Audience { get; set; }

    [Range(1, 1440)]
    public int AccessTokenLifetimeMinutes { get; set; } = 15;
}
