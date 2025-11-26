using System.Security.Cryptography;
using System.Text;
using GameService.ServiceDefaults.Data;
using Konscious.Security.Cryptography;
using Microsoft.AspNetCore.Identity;

namespace GameService.ApiService.Infrastructure.Security;

public class Argon2PasswordHasher : IPasswordHasher<ApplicationUser>
{
    public string HashPassword(ApplicationUser user, string password)
    {
        var salt = CreateSalt();
        var hash = HashPassword(password, salt);
        
        // Format: $argon2id$v=19$m=65536,t=3,p=1$salt$hash
        return $"$argon2id$v=19$m=65536,t=3,p=1${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public PasswordVerificationResult VerifyHashedPassword(ApplicationUser user, string hashedPassword, string providedPassword)
    {
        var parts = hashedPassword.Split('$');
        if (parts.Length != 6) return PasswordVerificationResult.Failed;

        var salt = Convert.FromBase64String(parts[4]);
        var storedHash = Convert.FromBase64String(parts[5]);

        var newHash = HashPassword(providedPassword, salt);

        return CryptographicOperations.FixedTimeEquals(storedHash, newHash)
            ? PasswordVerificationResult.Success
            : PasswordVerificationResult.Failed;
    }

    private byte[] CreateSalt()
    {
        var buffer = new byte[16];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(buffer);
        return buffer;
    }

    private byte[] HashPassword(string password, byte[] salt)
    {
        var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            DegreeOfParallelism = 1,
            MemorySize = 65536,
            Iterations = 3
        };

        return argon2.GetBytes(32);
    }
}