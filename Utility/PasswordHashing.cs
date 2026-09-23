using Level5Backend.Models;
using Microsoft.AspNetCore.Identity;

namespace Level5Backend.Utility
{
    // Wraps ASP.NET Core's PBKDF2-based PasswordHasher so user passwords are never stored or
    // compared in plaintext. HashPassword's output is self-contained (format marker, iteration
    // count, salt, and hash all in one string), so no separate salt column is needed.
    public static class PasswordHashing
    {
        private static readonly PasswordHasher<User> hasher = new();

        public static string Hash(string password)
        {
            return hasher.HashPassword(null!, password);
        }

        public static bool Verify(string hashedPassword, string suppliedPassword)
        {
            var result = hasher.VerifyHashedPassword(null!, hashedPassword, suppliedPassword);
            return result is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;
        }
    }
}
