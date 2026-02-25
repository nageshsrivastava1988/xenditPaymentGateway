using System.Security.Cryptography;

namespace PaymentGateway.Helpers;

public static class PasswordHasher
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Iterations = 120_000;

    public static (byte[] Hash, byte[] Salt) HashPassword(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return (hash, salt);
    }

    public static bool VerifyPassword(string password, byte[] passwordHash, byte[] passwordSalt)
    {
        if (string.IsNullOrWhiteSpace(password) || passwordHash.Length == 0 || passwordSalt.Length == 0)
        {
            return false;
        }

        byte[] computed = Rfc2898DeriveBytes.Pbkdf2(password, passwordSalt, Iterations, HashAlgorithmName.SHA256, passwordHash.Length);
        return CryptographicOperations.FixedTimeEquals(computed, passwordHash);
    }
}
