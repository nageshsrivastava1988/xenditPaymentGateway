using System.Security.Cryptography;

namespace PaymentGateway.Helpers;

public static class ResetTokenHelper
{
    public static (string RawToken, byte[] TokenHash) CreateToken()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(32);
        string raw = Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

        return (raw, HashToken(raw));
    }

    public static byte[] HashToken(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
    }
}
