namespace PaymentGateway.Helpers;

public static class CryptoHelper
{
    public static string DecryptAesGcmFromBase64(byte[] key, string data)
    {
        if (key is null || key.Length == 0)
        {
            throw new ArgumentException("AES key is required.", nameof(key));
        }

        if (string.IsNullOrWhiteSpace(data))
        {
            throw new ArgumentException("Encrypted data is required.", nameof(data));
        }

        byte[] inputBytes = Convert.FromBase64String(NormalizeBase64(data));

        const int ivLength = 12;
        const int tagLength = 16;
        const int minPayloadLength = ivLength + tagLength;

        if (inputBytes.Length < minPayloadLength)
        {
            throw new ArgumentException("Invalide: trop court pour IV et tag.", nameof(data));
        }

        byte[] iv = new byte[ivLength];
        byte[] tag = new byte[tagLength];
        byte[] cipherText = new byte[inputBytes.Length - ivLength - tagLength];

        Buffer.BlockCopy(inputBytes, 0, iv, 0, ivLength);
        Buffer.BlockCopy(inputBytes, ivLength, cipherText, 0, cipherText.Length);
        Buffer.BlockCopy(inputBytes, ivLength + cipherText.Length, tag, 0, tagLength);

        byte[] cipherTextWithTag = new byte[cipherText.Length + tag.Length];
        Buffer.BlockCopy(cipherText, 0, cipherTextWithTag, 0, cipherText.Length);
        Buffer.BlockCopy(tag, 0, cipherTextWithTag, cipherText.Length, tag.Length);

        IBufferedCipher cipher = CipherUtilities.GetCipher("AES/GCM/NoPadding");
        var parameters = new AeadParameters(new KeyParameter(key), tag.Length * 8, iv);
        cipher.Init(false, parameters);

        byte[] plainBytes = cipher.DoFinal(cipherTextWithTag);
        return Encoding.UTF8.GetString(plainBytes);
    }

    private static string NormalizeBase64(string value)
    {
        var normalized = Uri.UnescapeDataString(value).Trim();
        normalized = normalized.Replace(' ', '+');
        normalized = Regex.Replace(normalized, "[^a-zA-Z0-9/+=]", string.Empty);

        int remainder = normalized.Length % 4;
        if (remainder != 0)
        {
            normalized = normalized.PadRight(normalized.Length + (4 - remainder), '=');
        }

        return normalized;
    }
}
